using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Features.Chat;

namespace DocuMind.UnitTests.Chat;

public class RagPromptTests
{
    [Fact]
    public void User_turn_numbers_sources_and_includes_page_and_question()
    {
        var sources = new[]
        {
            new ChunkHit(Guid.NewGuid(), Guid.NewGuid(), "Q3 <report>.pdf", 0, 4, "Revenue grew 12%.", 0.5),
            new ChunkHit(Guid.NewGuid(), Guid.NewGuid(), "notes.md", 2, null, "Churn fell.", 0.4),
        };

        var turn = RagPrompt.BuildUserTurn("How did revenue change?", sources);

        Assert.Contains("<source id=\"1\" document=\"Q3 &lt;report&gt;.pdf\" page=\"4\">", turn);
        Assert.Contains("<source id=\"2\" document=\"notes.md\">", turn);
        Assert.EndsWith("<question>\nHow did revenue change?\n</question>", turn.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("Revenue grew [1] and churn fell [2][1].", 3, new[] { 1, 2 })]
    [InlineData("See [4] and [0] and [2].", 3, new[] { 2 })]
    [InlineData("No citations here.", 3, new int[0])]
    public void Extracts_distinct_valid_citation_numbers_in_order(string answer, int sourceCount, int[] expected)
    {
        Assert.Equal(expected, RagPrompt.ExtractCitedSourceNumbers(answer, sourceCount));
    }
}
