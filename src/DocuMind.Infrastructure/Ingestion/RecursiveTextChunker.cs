using System.Text;
using System.Text.RegularExpressions;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.Ingestion;

/// <summary>
/// Splits text on the coarsest boundary that fits (paragraph, line, sentence, word),
/// then packs the pieces into chunks of at most <see cref="RagOptions.ChunkSize"/> characters
/// with <see cref="RagOptions.ChunkOverlap"/> characters carried over between neighbours.
/// Chunks never span pages, so every chunk has a single page number to cite.
/// </summary>
public sealed partial class RecursiveTextChunker(IOptions<RagOptions> options) : ITextChunker
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", "? ", "! ", "; ", ", ", " "];

    private readonly int _size = options.Value.ChunkSize;
    private readonly int _overlap = Math.Min(options.Value.ChunkOverlap, options.Value.ChunkSize / 2);

    public IReadOnlyList<TextChunk> Chunk(ExtractedDocument document)
    {
        var chunks = new List<TextChunk>();

        foreach (var page in document.Pages)
        {
            var text = Normalize(page.Text);
            if (text.Length == 0) continue;

            foreach (var content in Merge(Split(text, 0)))
                chunks.Add(new TextChunk(chunks.Count, content, page.PageNumber));
        }

        return chunks;
    }

    private IEnumerable<string> Split(string text, int separatorIndex)
    {
        if (text.Length <= _size)
        {
            yield return text;
            yield break;
        }

        if (separatorIndex == Separators.Length)
        {
            for (var i = 0; i < text.Length; i += _size)
                yield return text.Substring(i, Math.Min(_size, text.Length - i));
            yield break;
        }

        var separator = Separators[separatorIndex];
        var parts = text.Split(separator);
        for (var i = 0; i < parts.Length; i++)
        {
            // Keep the separator attached so merged chunks read naturally.
            var part = i < parts.Length - 1 ? parts[i] + separator : parts[i];
            if (part.Length == 0) continue;

            foreach (var piece in Split(part, separatorIndex + 1))
                yield return piece;
        }
    }

    private IEnumerable<string> Merge(IEnumerable<string> pieces)
    {
        var current = new StringBuilder();

        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + piece.Length > _size)
            {
                var emitted = current.ToString().Trim();
                if (emitted.Length > 0) yield return emitted;

                var tail = OverlapTail(current.ToString());
                current.Clear();
                if (tail.Length + piece.Length <= _size)
                    current.Append(tail);
            }

            current.Append(piece);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>Last ~overlap characters of a chunk, starting at a word boundary.</summary>
    private string OverlapTail(string chunk)
    {
        if (_overlap == 0 || chunk.Length <= _overlap) return _overlap == 0 ? "" : chunk;

        var start = chunk.Length - _overlap;
        var space = chunk.IndexOf(' ', start);
        return space < 0 ? chunk[start..] : chunk[(space + 1)..];
    }

    private static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = HorizontalWhitespace().Replace(text, " ");
        text = ExcessBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)*")]
    private static partial Regex ExcessBlankLines();
}
