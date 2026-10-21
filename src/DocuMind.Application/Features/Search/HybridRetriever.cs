using DocuMind.Application.Abstractions.AI;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace DocuMind.Application.Features.Search;

public interface IHybridRetriever
{
    /// <summary>Only chunks of documents <paramref name="principal"/> can read are ever returned.</summary>
    Task<IReadOnlyList<ChunkHit>> RetrieveAsync(string query, int take, SearchScope scope, AccessPrincipal principal, CancellationToken ct);
}

/// <summary>
/// Semantic (vector cosine similarity) + lexical (BM25 keyword) retrieval fused with RRF.
/// Vector search catches paraphrases; keyword search catches exact terms, codes and names
/// that embeddings tend to blur.
/// </summary>
internal sealed class HybridRetriever(
    ITextEmbeddingService embeddings,
    IChunkSearchService search,
    IOptions<RagOptions> options) : IHybridRetriever
{
    public async Task<IReadOnlyList<ChunkHit>> RetrieveAsync(string query, int take, SearchScope scope, AccessPrincipal principal, CancellationToken ct)
    {
        var pool = Math.Max(options.Value.CandidatePoolSize, take);

        var queryVector = (await embeddings.EmbedAsync([query], EmbeddingInputType.Query, ct))[0];

        var vectorTask = search.VectorSearchAsync(queryVector, pool, scope, principal, ct);
        var keywordTask = search.KeywordSearchAsync(query, pool, scope, principal, ct);
        await Task.WhenAll(vectorTask, keywordTask);

        return ReciprocalRankFusion.Fuse([vectorTask.Result, keywordTask.Result], take);
    }
}
