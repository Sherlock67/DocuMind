namespace DocuMind.Application.Abstractions.Ingestion;

public sealed record StoredFile(string Path, string Sha256, long SizeBytes);

public interface IFileStorage
{
    /// <summary>Persists the stream and returns its location and content hash.</summary>
    Task<StoredFile> SaveAsync(Stream content, string fileName, CancellationToken ct);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>Text of one page (or the whole file for formats without pages).</summary>
public sealed record ExtractedPage(int? PageNumber, string Text);

public sealed record ExtractedDocument(IReadOnlyList<ExtractedPage> Pages)
{
    public int PageCount => Pages.Count(p => p.PageNumber is not null);
}

public interface ITextExtractor
{
    bool CanExtract(string extension);

    Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken ct);
}

public sealed record TextChunk(int Index, string Content, int? PageNumber);

public interface ITextChunker
{
    IReadOnlyList<TextChunk> Chunk(ExtractedDocument document);
}

/// <summary>Hand-off between the upload request and the background ingestion worker.</summary>
public interface IIngestionQueue
{
    ValueTask EnqueueAsync(Guid documentId, CancellationToken ct);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
}

public static class SupportedFileTypes
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
    };

    public static IReadOnlyCollection<string> Extensions => ContentTypes.Keys;

    public static bool IsSupported(string fileName) => ContentTypes.ContainsKey(Path.GetExtension(fileName));

    /// <summary>
    /// Content type derived from the extension, never from the client's upload header: serving a
    /// client-chosen type inline (e.g. text/html) would turn the preview endpoint into an XSS vector.
    /// </summary>
    public static string ContentTypeFor(string fileName) =>
        ContentTypes.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");

    /// <summary>Formats a browser can render natively in a preview pane.</summary>
    public static bool CanPreviewInline(string fileName) => !Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);
}
