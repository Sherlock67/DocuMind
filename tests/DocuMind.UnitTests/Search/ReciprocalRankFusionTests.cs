using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Features.Search;

namespace DocuMind.UnitTests.Search;

public class ReciprocalRankFusionTests
{
    private static ChunkHit Hit(Guid id, double score = 0) => new(id, Guid.Empty, "f.pdf", 0, 1, "text", score);

    [Fact]
    public void Chunks_found_by_both_retrievers_rank_above_single_source_hits()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

        var vector = new[] { Hit(a), Hit(b) };
        var keyword = new[] { Hit(c), Hit(b) };

        var fused = ReciprocalRankFusion.Fuse([vector, keyword], take: 3);

        Assert.Equal(b, fused[0].ChunkId);
        Assert.Equal(3, fused.Count);
    }

    [Fact]
    public void Fused_score_is_sum_of_reciprocal_ranks()
    {
        var id = Guid.NewGuid();

        var fused = ReciprocalRankFusion.Fuse([[Hit(id, 0.9)], [Hit(Guid.NewGuid()), Hit(id, 12.0)]], take: 1);

        Assert.Equal(1.0 / 61 + 1.0 / 62, fused[0].Score, precision: 10);
    }

    [Fact]
    public void Take_limits_results()
    {
        var hits = Enumerable.Range(0, 10).Select(_ => Hit(Guid.NewGuid())).ToArray();

        Assert.Equal(4, ReciprocalRankFusion.Fuse([hits], take: 4).Count);
    }
}
