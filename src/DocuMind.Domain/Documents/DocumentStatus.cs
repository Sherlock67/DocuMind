namespace DocuMind.Domain.Documents;

public enum DocumentStatus
{
    /// <summary>Uploaded and waiting in the ingestion queue.</summary>
    Queued = 0,

    /// <summary>Text extraction, chunking and embedding in progress.</summary>
    Processing = 1,

    /// <summary>Chunks are embedded and searchable.</summary>
    Completed = 2,

    /// <summary>Ingestion failed; see <see cref="Document.FailureReason"/>.</summary>
    Failed = 3,
}
