using DocuMind.Domain.Common;

namespace DocuMind.Domain.Documents;

/// <summary>
/// A retrievable slice of a document: its embedding (for semantic search) and its keyword
/// terms (for the BM25 keyword index).
/// </summary>
public sealed class DocumentChunk : Entity
{
    private readonly List<ChunkTerm> _terms = [];

    private DocumentChunk() { } // EF Core

    public Guid DocumentId { get; private set; }
    public int ChunkIndex { get; private set; }
    public string Content { get; private set; } = null!;

    /// <summary>1-based page the chunk starts on, when the source format has pages.</summary>
    public int? PageNumber { get; private set; }

    public float[] Embedding { get; private set; } = [];

    /// <summary>Number of indexed terms in the chunk (its length, for BM25 normalisation).</summary>
    public int TermCount { get; private set; }

    public IReadOnlyCollection<ChunkTerm> Terms => _terms.AsReadOnly();

    /// <param name="termFrequencies">Each distinct keyword term in the chunk and how often it occurs.</param>
    public static DocumentChunk Create(
        Guid documentId,
        int chunkIndex,
        string content,
        int? pageNumber,
        float[] embedding,
        IReadOnlyDictionary<string, int> termFrequencies)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new DomainException("Chunk content is required.");
        if (embedding.Length == 0) throw new DomainException("Chunk embedding is required.");

        var chunk = new DocumentChunk
        {
            DocumentId = documentId,
            ChunkIndex = chunkIndex,
            Content = content,
            PageNumber = pageNumber,
            Embedding = embedding,
            TermCount = termFrequencies.Values.Sum(),
        };
        chunk._terms.AddRange(termFrequencies.Select(t => new ChunkTerm(chunk.Id, t.Key, t.Value)));
        return chunk;
    }
}

/// <summary>One row of the keyword (inverted) index: term <see cref="Term"/> occurs <see cref="Frequency"/> times in a chunk.</summary>
public sealed class ChunkTerm
{
    public const int MaxTermLength = 64;

    private ChunkTerm() { } // EF Core

    internal ChunkTerm(Guid chunkId, string term, int frequency)
    {
        ChunkId = chunkId;
        Term = term;
        Frequency = frequency;
    }

    public Guid ChunkId { get; private set; }
    public string Term { get; private set; } = null!;
    public int Frequency { get; private set; }
}
