using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Options;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Common;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DocuMind.Application.Features.Documents.Commands;

public sealed record UploadFile(string FileName, long Length, Stream Content);

/// <summary>
/// Uploads one or more files into a project, optionally into a folder and/or a collection.
/// Each file succeeds or fails on its own: one bad file never sinks the rest of the batch.
/// </summary>
public sealed record UploadDocumentsCommand(
    Guid ProjectId,
    IReadOnlyList<UploadFile> Files,
    Guid? FolderId = null,
    Guid? CollectionId = null) : ICommand<BatchUploadResult>;

public enum UploadOutcome
{
    /// <summary>Stored and queued for processing.</summary>
    Accepted,

    /// <summary>Identical content already exists in the project; the existing document is returned.</summary>
    Duplicate,

    /// <summary>Not stored; see <see cref="UploadItemResult.Error"/>.</summary>
    Rejected,
}

public sealed record UploadItemResult(string FileName, UploadOutcome Outcome, Guid? DocumentId, DocumentStatus? Status, string? Error);

public sealed record BatchUploadResult(IReadOnlyList<UploadItemResult> Items)
{
    public int Accepted => Items.Count(i => i.Outcome == UploadOutcome.Accepted);
    public int Duplicates => Items.Count(i => i.Outcome == UploadOutcome.Duplicate);
    public int Rejected => Items.Count(i => i.Outcome == UploadOutcome.Rejected);
}

/// <summary>Batch-level rules. Per-file rules are checked by the handler so they can be reported per file.</summary>
internal sealed class UploadDocumentsCommandValidator : AbstractValidator<UploadDocumentsCommand>
{
    public UploadDocumentsCommandValidator(IOptions<DocumentOptions> options)
    {
        var o = options.Value;
        RuleFor(x => x.ProjectId).NotEmpty();
        RuleFor(x => x.Files)
            .NotEmpty().WithMessage("Select at least one file.")
            .Must(f => f.Count <= o.MaxFilesPerBatch).WithMessage($"At most {o.MaxFilesPerBatch} files per upload.")
            .Must(f => f.Sum(x => x.Length) <= o.MaxBatchBytes).WithMessage($"An upload may total at most {o.MaxBatchBytes / (1024 * 1024)} MB.");
    }
}

internal sealed class UploadDocumentsCommandHandler(
    IAccessControl access,
    ICurrentUser user,
    IAuditTrail audit,
    IFolderRepository folders,
    ICollectionRepository collections,
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IFileStorage storage,
    IIngestionQueue queue,
    IOptions<DocumentOptions> options,
    TimeProvider clock) : ICommandHandler<UploadDocumentsCommand, BatchUploadResult>
{
    public async Task<BatchUploadResult> Handle(UploadDocumentsCommand command, CancellationToken ct)
    {
        await access.RequireProjectAsync(command.ProjectId, ProjectRole.Editor, ct);

        if (command.FolderId is { } folderId)
        {
            var folder = await folders.GetByIdAsync(folderId, ct);
            if (folder is null || folder.ProjectId != command.ProjectId)
                throw new NotFoundException(nameof(Folder), folderId);
        }

        Collection? collection = null;
        if (command.CollectionId is { } collectionId)
        {
            await access.RequireCollectionAsync(collectionId, CollectionPermission.Manage, ct);
            collection = await collections.GetWithItemsAsync(collectionId, ct);
            if (collection is null || collection.ProjectId != command.ProjectId)
                throw new NotFoundException(nameof(Collection), collectionId);
        }

        var results = new List<UploadItemResult>(command.Files.Count);
        foreach (var file in command.Files)
            results.Add(await UploadOneAsync(command, file, collection, ct));

        return new BatchUploadResult(results);
    }

    /// <summary>Stores, records and queues one file. Committed per file, so earlier files survive a later failure.</summary>
    private async Task<UploadItemResult> UploadOneAsync(UploadDocumentsCommand command, UploadFile file, Collection? collection, CancellationToken ct)
    {
        var fileName = Path.GetFileName(file.FileName);
        if (CheckFile(fileName, file.Length) is { } error)
            return new UploadItemResult(fileName, UploadOutcome.Rejected, null, null, error);

        var stored = await storage.SaveAsync(file.Content, fileName, ct);
        var now = clock.GetUtcNow();

        var existing = await documents.FindByContentHashAsync(command.ProjectId, stored.Sha256, ct);
        if (existing is not null)
        {
            await storage.DeleteAsync(stored.Path, ct);
            if (collection?.AddDocuments([existing.Id], now) > 0)
                await unitOfWork.SaveChangesAsync(ct);

            return new UploadItemResult(fileName, UploadOutcome.Duplicate, existing.Id, existing.Status, $"Same content as '{existing.FileName}'.");
        }

        Document document;
        try
        {
            document = Document.Create(command.ProjectId, command.FolderId, fileName,
                SupportedFileTypes.ContentTypeFor(fileName), stored.SizeBytes, stored.Path, stored.Sha256, user.Id, now);
        }
        catch (DomainException ex)
        {
            await storage.DeleteAsync(stored.Path, ct);
            return new UploadItemResult(fileName, UploadOutcome.Rejected, null, null, ex.Message);
        }

        documents.Add(document);
        collection?.AddDocuments([document.Id], now);
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.DocumentUploaded, "document", document.Id, document.ProjectId, new Dictionary<string, string>
        {
            ["fileName"] = document.FileName,
            ["sizeBytes"] = document.SizeBytes.ToString(),
            ["sha256"] = document.ContentHash,
        });

        // Committed first, then queued: if the process dies in between, the startup
        // recovery scan re-queues every Queued document, so nothing is lost.
        await queue.EnqueueAsync(document.Id, ct);

        return new UploadItemResult(fileName, UploadOutcome.Accepted, document.Id, document.Status, null);
    }

    private string? CheckFile(string fileName, long length)
    {
        var max = options.Value.MaxFileBytes;
        if (string.IsNullOrWhiteSpace(fileName)) return "File name is required.";
        if (!SupportedFileTypes.IsSupported(fileName)) return $"Unsupported file type. Supported: {string.Join(", ", SupportedFileTypes.Extensions)}.";
        if (length <= 0) return "File is empty.";
        if (length > max) return $"File exceeds the {max / (1024 * 1024)} MB limit.";
        return null;
    }
}
