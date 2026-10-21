using DocuMind.Application.Abstractions.Ingestion;
using Microsoft.Extensions.Logging;

namespace DocuMind.Application.Common;

/// <summary>
/// Deletes stored files after their rows are committed as deleted. Best-effort by design:
/// an orphaned blob is harmless and can be swept later, while failing the request after the
/// database commit would report an error for an operation that actually succeeded.
/// </summary>
internal sealed class DocumentFileCleanup(IFileStorage storage, ILogger<DocumentFileCleanup> logger)
{
    public async Task DeleteFilesAsync(IEnumerable<string> storagePaths, CancellationToken ct)
    {
        foreach (var path in storagePaths)
        {
            try
            {
                await storage.DeleteAsync(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not delete stored file {Path}; it is now orphaned", path);
            }
        }
    }
}
