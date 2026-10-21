using System.Text.RegularExpressions;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Domain.Documents;

namespace DocuMind.Infrastructure.Search;

/// <summary>
/// Lower-cases, splits on anything that is not a letter or digit, and drops English stop words and
/// one-letter tokens. Deliberately no stemming: keyword search exists to match exact terms (names,
/// codes, numbers); paraphrases are the vector search's job.
/// </summary>
public sealed partial class KeywordTokenizer : IKeywordTokenizer
{
    private static readonly HashSet<string> StopWords =
    [
        "a", "about", "above", "after", "again", "against", "all", "am", "an", "and", "any", "are", "as", "at",
        "be", "because", "been", "before", "being", "below", "between", "both", "but", "by", "can", "could",
        "did", "do", "does", "doing", "down", "during", "each", "few", "for", "from", "further", "had", "has",
        "have", "having", "he", "her", "here", "hers", "him", "his", "how", "i", "if", "in", "into", "is", "it",
        "its", "itself", "just", "me", "more", "most", "my", "no", "nor", "not", "now", "of", "off", "on", "once",
        "only", "or", "other", "our", "ours", "out", "over", "own", "same", "she", "should", "so", "some", "such",
        "than", "that", "the", "their", "theirs", "them", "then", "there", "these", "they", "this", "those",
        "through", "to", "too", "under", "until", "up", "very", "was", "we", "were", "what", "when", "where",
        "which", "while", "who", "whom", "why", "will", "with", "would", "you", "your", "yours",
    ];

    public IReadOnlyList<string> Tokenize(string text)
    {
        var terms = new List<string>();
        foreach (Match match in Word().Matches(text.ToLowerInvariant()))
        {
            var term = match.Value;
            if (term.Length < 2 && !char.IsDigit(term[0])) continue;
            if (StopWords.Contains(term)) continue;
            terms.Add(term.Length > ChunkTerm.MaxTermLength ? term[..ChunkTerm.MaxTermLength] : term);
        }

        return terms;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
