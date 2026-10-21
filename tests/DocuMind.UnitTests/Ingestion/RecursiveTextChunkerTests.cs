using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Common.Options;
using DocuMind.Infrastructure.Ingestion;
using Microsoft.Extensions.Options;

namespace DocuMind.UnitTests.Ingestion;

public class RecursiveTextChunkerTests
{
    private static RecursiveTextChunker Chunker(int size = 200, int overlap = 40) =>
        new(Options.Create(new RagOptions { ChunkSize = size, ChunkOverlap = overlap }));

    private static string Sentences(int count) =>
        string.Join(" ", Enumerable.Range(1, count).Select(i => $"This is sentence number {i} about the quarterly report."));

    [Fact]
    public void Short_text_becomes_a_single_chunk()
    {
        var chunks = Chunker().Chunk(new ExtractedDocument([new ExtractedPage(1, "Hello world.")]));

        var chunk = Assert.Single(chunks);
        Assert.Equal("Hello world.", chunk.Content);
        Assert.Equal(1, chunk.PageNumber);
    }

    [Fact]
    public void Chunks_never_exceed_the_configured_size()
    {
        var chunks = Chunker(size: 200).Chunk(new ExtractedDocument([new ExtractedPage(null, Sentences(50))]));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Content.Length <= 200, $"chunk was {c.Content.Length} chars"));
    }

    [Fact]
    public void Consecutive_chunks_overlap()
    {
        var chunks = Chunker(size: 200, overlap: 60).Chunk(new ExtractedDocument([new ExtractedPage(null, Sentences(20))]));

        for (var i = 1; i < chunks.Count; i++)
        {
            var previousTail = chunks[i - 1].Content[^20..];
            Assert.Contains(previousTail, chunks[i].Content);
        }
    }

    [Fact]
    public void Chunks_keep_their_page_number_and_are_indexed_across_pages()
    {
        var doc = new ExtractedDocument([
            new ExtractedPage(1, Sentences(10)),
            new ExtractedPage(2, "Second page text."),
            new ExtractedPage(3, "   "),
        ]);

        var chunks = Chunker().Chunk(doc);

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
        Assert.Equal(2, chunks[^1].PageNumber);
        Assert.Equal("Second page text.", chunks[^1].Content);
        Assert.DoesNotContain(chunks, c => c.PageNumber == 3);
    }

    [Fact]
    public void Text_without_separators_is_hard_split()
    {
        var chunks = Chunker(size: 100, overlap: 0).Chunk(new ExtractedDocument([new ExtractedPage(null, new string('x', 250))]));

        Assert.Equal([100, 100, 50], chunks.Select(c => c.Content.Length));
    }
}
