using DocuMind.Application.Abstractions.Search;

namespace DocuMind.Application.Features.Search;

/// <summary>
/// Merges several best-first result lists into one using Reciprocal Rank Fusion:
/// score(d) = Σ 1 / (k + rank_i(d)). Rank-based, so it needs no score normalisation
/// between cosine similarity and full-text rank, and rewards chunks both retrievers agree on.
/// </summary>
public static class ReciprocalRankFusion
{
    public const int DefaultK = 60;

    public static IReadOnlyList<ChunkHit> Fuse(IEnumerable<IReadOnlyList<ChunkHit>> rankings, int take, int k = DefaultK)
    {
        var fused = new Dictionary<Guid, (ChunkHit Hit, double Score)>();

        foreach (var ranking in rankings)
        {
            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var hit = ranking[rank];
                var contribution = 1.0 / (k + rank + 1);

                fused[hit.ChunkId] = fused.TryGetValue(hit.ChunkId, out var existing)
                    ? (existing.Hit, existing.Score + contribution)
                    : (hit, contribution);
            }
        }

        return fused.Values
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x => x.Hit with { Score = x.Score })
            .ToList();
    }
}
