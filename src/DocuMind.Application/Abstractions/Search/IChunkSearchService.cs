using DocuMind.Application.Abstractions.Security;

namespace DocuMind.Application.Abstractions.Search;

public sealed record ChunkHit(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int? PageNumber,
    string Content,
    double Score,
    Guid ProjectId = default);

/// <summary>
/// Which documents retrieval may draw on. Every set filter must match (logical AND);
/// <see cref="FolderId"/> includes all of its subfolders.
/// </summary>
public sealed record SearchScope(
    Guid? ProjectId = null,
    Guid? FolderId = null,
    Guid? CollectionId = null,
    IReadOnlyCollection<Guid>? DocumentIds = null)
{
    public static readonly SearchScope Everything = new();
}

/// <summary>
/// Splits text into normalised keyword terms. Used both when indexing chunks and when parsing a
/// search query, so the two always agree on what a "term" is.
/// </summary>
public interface IKeywordTokenizer
{
    IReadOnlyList<string> Tokenize(string text);

    /// <summary>Distinct terms of <paramref name="text"/> with how often each occurs.</summary>
    IReadOnlyDictionary<string, int> TermFrequencies(string text) =>
        Tokenize(text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Store-specific retrievers. Results are ordered best-first and only include Completed documents.</summary>
/// <remarks>
/// The <see cref="AccessPrincipal"/> is a required argument on purpose: there is no way to call a
/// retriever without stating whose permissions apply, so no code path can retrieve unfiltered.
/// </remarks>
public interface IChunkSearchService
{
    Task<IReadOnlyList<ChunkHit>> VectorSearchAsync(float[] queryEmbedding, int limit, SearchScope scope, AccessPrincipal principal, CancellationToken ct);

    Task<IReadOnlyList<ChunkHit>> KeywordSearchAsync(string query, int limit, SearchScope scope, AccessPrincipal principal, CancellationToken ct);
}
