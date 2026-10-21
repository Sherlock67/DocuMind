namespace DocuMind.Application.Common.Options;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Candidates fetched from each retriever (vector and keyword) before fusion.</summary>
    public int CandidatePoolSize { get; set; } = 30;

    /// <summary>Chunks passed to the LLM as context after fusion.</summary>
    public int ContextChunks { get; set; } = 8;

    /// <summary>Previous messages (user + assistant) replayed for follow-up questions.</summary>
    public int HistoryMessages { get; set; } = 10;

    /// <summary>Target chunk size in characters (~4 chars per token).</summary>
    public int ChunkSize { get; set; } = 1200;

    /// <summary>Characters shared between consecutive chunks so facts spanning a boundary survive.</summary>
    public int ChunkOverlap { get; set; } = 200;
}

public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    public long MaxFileBytes { get; set; } = 50 * 1024 * 1024;

    public int MaxFilesPerBatch { get; set; } = 20;

    /// <summary>Upper bound for one multipart request; also used to size Kestrel's body limit.</summary>
    public long MaxBatchBytes { get; set; } = 200 * 1024 * 1024;
}
