using DocuMind.Domain.Common;

namespace DocuMind.Domain.Documents;

/// <summary>
/// Aggregate root for an uploaded file. Lives in exactly one project and optionally one folder.
/// Owns its <see cref="DocumentChunk"/>s (for retrieval) and <see cref="DocumentPage"/>s (for preview),
/// and enforces the ingestion lifecycle: Queued -> Processing -> Completed | Failed.
/// </summary>
public sealed class Document : Entity, IAggregateRoot
{
    public const int MaxFileNameLength = 255;

    private readonly List<DocumentChunk> _chunks = [];
    private readonly List<DocumentPage> _pages = [];

    private Document() { } // EF Core

    public Guid ProjectId { get; private set; }
    public Guid? FolderId { get; private set; }

    /// <summary>User who uploaded the file.</summary>
    public Guid UploadedBy { get; private set; }

    public string FileName { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string StoragePath { get; private set; } = null!;

    /// <summary>SHA-256 of the file content, used to detect duplicate uploads within a project.</summary>
    public string ContentHash { get; private set; } = null!;

    public DocumentStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public int PageCount { get; private set; }
    public int ChunkCount { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
    public DateTimeOffset? ProcessingStartedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<DocumentChunk> Chunks => _chunks.AsReadOnly();
    public IReadOnlyCollection<DocumentPage> Pages => _pages.AsReadOnly();

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();

    public static Document Create(
        Guid projectId,
        Guid? folderId,
        string fileName,
        string contentType,
        long sizeBytes,
        string storagePath,
        string contentHash,
        Guid uploadedBy,
        DateTimeOffset uploadedAt)
    {
        if (projectId == Guid.Empty) throw new DomainException("A document must belong to a project.");
        if (sizeBytes <= 0) throw new DomainException("Document must not be empty.");

        return new Document
        {
            ProjectId = projectId,
            FolderId = folderId,
            FileName = ValidateFileName(fileName),
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StoragePath = storagePath,
            ContentHash = contentHash,
            UploadedBy = uploadedBy,
            Status = DocumentStatus.Queued,
            UploadedAt = uploadedAt,
            UpdatedAt = uploadedAt,
        };
    }

    /// <summary>
    /// Renames the document. The extension is part of the file's identity (it picks the extractor
    /// and the served content type), so it is kept: "report" on a PDF becomes "report.pdf".
    /// </summary>
    public void Rename(string newName, DateTimeOffset now)
    {
        var name = newName.Trim();
        if (!Path.GetExtension(name).Equals(Extension, StringComparison.OrdinalIgnoreCase))
            name += Extension;

        FileName = ValidateFileName(name);
        UpdatedAt = now;
    }

    /// <summary>Moves the document to a folder of the same project, or to the project root when null.</summary>
    public void MoveToFolder(Guid? folderId, DateTimeOffset now)
    {
        FolderId = folderId;
        UpdatedAt = now;
    }

    public void MarkProcessing(DateTimeOffset now)
    {
        if (Status == DocumentStatus.Processing)
            return; // resuming after a crash is allowed

        if (Status != DocumentStatus.Queued)
            throw new DomainException($"Cannot start processing a document in status {Status}.");

        Status = DocumentStatus.Processing;
        FailureReason = null;
        ProcessingStartedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Replaces all chunks and pages with a fresh set and marks the document searchable.</summary>
    public void CompleteIngestion(IEnumerable<DocumentChunk> chunks, IEnumerable<DocumentPage> pages, int pageCount, DateTimeOffset processedAt)
    {
        if (Status != DocumentStatus.Processing)
            throw new DomainException($"Cannot complete ingestion of a document in status {Status}.");

        _chunks.Clear();
        _chunks.AddRange(chunks);
        _pages.Clear();
        _pages.AddRange(pages);

        if (_chunks.Count == 0)
            throw new DomainException("A completed document must have at least one chunk.");

        PageCount = pageCount;
        ChunkCount = _chunks.Count;
        Status = DocumentStatus.Completed;
        ProcessedAt = processedAt;
        UpdatedAt = processedAt;
    }

    public void MarkFailed(string reason, DateTimeOffset failedAt)
    {
        _chunks.Clear();
        _pages.Clear();
        ChunkCount = 0;
        Status = DocumentStatus.Failed;
        FailureReason = reason.Length > 2000 ? reason[..2000] : reason;
        ProcessedAt = failedAt;
        UpdatedAt = failedAt;
    }

    /// <summary>Puts a Completed or Failed document back in the queue (e.g. after changing the embedding model).</summary>
    public void Requeue(DateTimeOffset now)
    {
        if (Status is DocumentStatus.Queued or DocumentStatus.Processing)
            throw new DomainException("Document is already queued for processing.");

        Status = DocumentStatus.Queued;
        FailureReason = null;
        ProcessingStartedAt = null;
        UpdatedAt = now;
    }

    private static string ValidateFileName(string fileName)
    {
        // No Path.GetFileName here: "a/b" must be rejected, not silently shortened to "b".
        var name = fileName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)))
            throw new DomainException("File name is required.");
        if (name.Length > MaxFileNameLength)
            throw new DomainException($"File name must be at most {MaxFileNameLength} characters.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new DomainException("File name contains invalid characters.");
        return name;
    }
}
