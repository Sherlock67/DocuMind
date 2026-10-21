using System.Data;
using System.Text.Json;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Infrastructure.AI;
using DocuMind.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.Search;

/// <summary>Write and read connection strings for hand-written SQL (search, audit inserts).</summary>
public sealed record SqlConnections(string Write, string Read);

/// <summary>
/// Hybrid retrieval in T-SQL on the read connection. Each call opens its own pooled connection,
/// so the vector and keyword searches can run in parallel.
/// <list type="bullet">
/// <item>Semantic: exact nearest-neighbour scan with SQL Server 2025 <c>VECTOR_DISTANCE('cosine', ...)</c>.</item>
/// <item>Keyword: Okapi BM25 over the <c>DocumentChunkTerms</c> inverted index (SQL Server Full-Text
/// Search is not available on LocalDB/Express, so we keep our own).</item>
/// </list>
/// Both queries share one scope/access predicate, and it cannot be skipped: the caller's
/// <see cref="AccessPrincipal"/> is a required argument.
/// </summary>
internal sealed class SqlChunkSearchService(SqlConnections connections, IKeywordTokenizer tokenizer, IOptions<EmbeddingOptions> embedding)
    : IChunkSearchService
{
    // BM25 parameters (the usual defaults).
    private const double K1 = 1.2;
    private const double B = 0.75;

    private const string Columns = "c.Id, c.DocumentId, d.FileName, c.ChunkIndex, c.PageNumber, c.Content, d.ProjectId";

    /// <summary>Folder scope includes every subfolder. Folders nest at most 10 levels, well under MAXRECURSION.</summary>
    private const string SubtreeCte = $"""
        Subtree AS (
            SELECT Id FROM {Tables.Folders} WHERE Id = @folderId
            UNION ALL
            SELECT f.Id FROM {Tables.Folders} f JOIN Subtree s ON f.ParentId = s.Id)
        """;

    // Access first: a document is readable by Admins, by members of its project, and by users a
    // collection containing it is shared with. Then the optional scope filters (NULL / '[]' = off).
    private const string ScopeFilter = $"""
        d.Status = 'Completed'
        AND (@isAdmin = 1
             OR EXISTS (SELECT 1 FROM {Tables.ProjectMembers} pm WHERE pm.ProjectId = d.ProjectId AND pm.UserId = @userId)
             OR EXISTS (SELECT 1 FROM {Tables.CollectionDocuments} acd
                        JOIN {Tables.CollectionShares} acs ON acs.CollectionId = acd.CollectionId
                        WHERE acd.DocumentId = d.Id AND acs.UserId = @userId))
        AND (@projectId IS NULL OR d.ProjectId = @projectId)
        AND (@folderId IS NULL OR d.FolderId IN (SELECT Id FROM Subtree))
        AND (@collectionId IS NULL OR EXISTS (
                SELECT 1 FROM {Tables.CollectionDocuments} cd WHERE cd.CollectionId = @collectionId AND cd.DocumentId = d.Id))
        AND (@documentIds = N'[]' OR d.Id IN (SELECT TRY_CAST([value] AS uniqueidentifier) FROM OPENJSON(@documentIds)))
        """;

    public Task<IReadOnlyList<ChunkHit>> VectorSearchAsync(float[] queryEmbedding, int limit, SearchScope scope, AccessPrincipal principal, CancellationToken ct)
    {
        var dims = embedding.Value.Dimensions;
        var sql = $"""
            DECLARE @q VECTOR({dims}) = CAST(@query AS VECTOR({dims}));

            WITH {SubtreeCte}
            SELECT TOP (@limit) {Columns}, 1.0 - VECTOR_DISTANCE('cosine', c.Embedding, @q) AS Score
            FROM {Tables.DocumentChunks} c
            JOIN {Tables.Documents} d ON d.Id = c.DocumentId
            WHERE {ScopeFilter}
            ORDER BY VECTOR_DISTANCE('cosine', c.Embedding, @q)
            OPTION (RECOMPILE);
            """;

        return QueryAsync(sql, JsonSerializer.Serialize(queryEmbedding), limit, scope, principal, ct);
    }

    public Task<IReadOnlyList<ChunkHit>> KeywordSearchAsync(string query, int limit, SearchScope scope, AccessPrincipal principal, CancellationToken ct)
    {
        var terms = tokenizer.Tokenize(query).Distinct().ToList();
        if (terms.Count == 0)
            return Task.FromResult<IReadOnlyList<ChunkHit>>([]); // e.g. a query made only of stop words

        // score = Σ idf(t) · tf·(k1+1) / (tf + k1·(1 − b + b·len/avgLen)),  idf = ln(1 + (N − df + 0.5)/(df + 0.5))
        var sql = $"""
            WITH {SubtreeCte},
            QueryTerms AS (
                SELECT DISTINCT CAST([value] AS nvarchar(64)) AS Term FROM OPENJSON(@query)),
            Corpus AS (
                SELECT CAST(COUNT_BIG(*) AS float) AS N,
                       COALESCE(NULLIF(AVG(CAST(TermCount AS float)), 0), 1) AS AvgLen
                FROM {Tables.DocumentChunks}),
            TermStats AS (
                SELECT t.Term, CAST(COUNT_BIG(*) AS float) AS Df
                FROM {Tables.DocumentChunkTerms} t
                JOIN QueryTerms q ON q.Term = t.Term
                GROUP BY t.Term),
            Scored AS (
                SELECT t.ChunkId,
                       SUM(LOG(1.0 + (k.N - ts.Df + 0.5) / (ts.Df + 0.5))
                           * (t.Frequency * {K1 + 1:0.0#}) / (t.Frequency + {K1:0.0#} * ({1 - B:0.0#} + {B:0.0#} * c.TermCount / k.AvgLen))) AS Score
                FROM {Tables.DocumentChunkTerms} t
                JOIN TermStats ts ON ts.Term = t.Term
                JOIN {Tables.DocumentChunks} c ON c.Id = t.ChunkId
                CROSS JOIN Corpus k
                GROUP BY t.ChunkId)
            SELECT TOP (@limit) {Columns}, s.Score
            FROM Scored s
            JOIN {Tables.DocumentChunks} c ON c.Id = s.ChunkId
            JOIN {Tables.Documents} d ON d.Id = c.DocumentId
            WHERE {ScopeFilter}
            ORDER BY s.Score DESC
            OPTION (RECOMPILE);
            """;

        return QueryAsync(sql, JsonSerializer.Serialize(terms), limit, scope, principal, ct);
    }

    private async Task<IReadOnlyList<ChunkHit>> QueryAsync(string sql, string queryJson, int limit, SearchScope scope, AccessPrincipal principal, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connections.Read);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        var p = command.Parameters;
        p.Add("@query", SqlDbType.NVarChar, -1).Value = queryJson;
        p.Add("@limit", SqlDbType.Int).Value = limit;
        p.Add("@projectId", SqlDbType.UniqueIdentifier).Value = (object?)scope.ProjectId ?? DBNull.Value;
        p.Add("@folderId", SqlDbType.UniqueIdentifier).Value = (object?)scope.FolderId ?? DBNull.Value;
        p.Add("@collectionId", SqlDbType.UniqueIdentifier).Value = (object?)scope.CollectionId ?? DBNull.Value;
        p.Add("@documentIds", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(scope.DocumentIds ?? []);
        p.Add("@userId", SqlDbType.UniqueIdentifier).Value = principal.UserId;
        p.Add("@isAdmin", SqlDbType.Bit).Value = principal.IsAdmin;

        var hits = new List<ChunkHit>(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            hits.Add(new ChunkHit(
                ChunkId: reader.GetGuid(0),
                DocumentId: reader.GetGuid(1),
                FileName: reader.GetString(2),
                ChunkIndex: reader.GetInt32(3),
                PageNumber: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Content: reader.GetString(5),
                Score: reader.IsDBNull(7) ? 0 : Convert.ToDouble(reader.GetValue(7)),
                ProjectId: reader.GetGuid(6)));
        }

        return hits;
    }
}
