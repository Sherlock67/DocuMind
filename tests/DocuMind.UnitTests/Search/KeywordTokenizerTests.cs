using DocuMind.Application.Abstractions.Search;
using DocuMind.Infrastructure.Search;

namespace DocuMind.UnitTests.Search;

public class KeywordTokenizerTests
{
    private readonly IKeywordTokenizer _tokenizer = new KeywordTokenizer();

    [Fact]
    public void Lowercases_splits_on_punctuation_and_drops_stop_words()
    {
        Assert.Equal(
            ["invoice", "inv", "2024", "001", "due", "march"],
            _tokenizer.Tokenize("The Invoice (INV-2024-001) is due in March!"));
    }

    [Fact]
    public void Keeps_single_digits_but_not_single_letters()
    {
        Assert.Equal(["tier", "3", "plan", "option"], _tokenizer.Tokenize("Tier 3 plan, option b"));
    }

    [Fact]
    public void Handles_non_english_letters()
    {
        Assert.Equal(["café", "zürich", "東京"], _tokenizer.Tokenize("Café — Zürich, 東京"));
    }

    [Fact]
    public void Term_frequencies_count_repeats()
    {
        var tf = _tokenizer.TermFrequencies("Leave policy: annual leave and sick leave.");

        Assert.Equal(3, tf["leave"]);
        Assert.Equal(1, tf["policy"]);
        Assert.False(tf.ContainsKey("and"));
    }
}
