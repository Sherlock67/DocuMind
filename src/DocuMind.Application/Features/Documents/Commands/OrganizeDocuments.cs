using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Common;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;

namespace DocuMind.Application.Features.Documents.Commands;

/// <summary>Renames a document. The extension is kept, so "Q3 summary" on a PDF becomes "Q3 summary.pdf".</summary>
public sealed record RenameDocumentCommand(Guid DocumentId, string FileName) : ICommand<DocumentDto>;

/// <summary>Moves documents into a folder, or to their project's root when <see cref="FolderId"/> is null.</summary>
public sealed record MoveDocumentsCommand(IReadOnlyList<Guid> DocumentIds, Guid? FolderId) : ICommand<Unit>;

internal sealed class RenameDocumentCommandValidator : AbstractValidator<RenameDocumentCommand>
{
    public RenameDocumentCommandValidator() => RuleFor(x => x.FileName).NotEmpty().MaximumLength(Document.MaxFileNameLength);
}

internal sealed class MoveDocumentsCommandValidator : AbstractValidator<MoveDocumentsCommand>
{
    public MoveDocumentsCommandValidator() =>
        RuleFor(x => x.DocumentIds).NotEmpty().Must(ids => ids.Count <= 500).WithMessage("At most 500 documents per request.");
}

internal sealed class RenameDocumentCommandHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<RenameDocumentCommand, DocumentDto>
{
    public async Task<DocumentDto> Handle(RenameDocumentCommand command, CancellationToken ct)
    {
        await access.RequireDocumentAsync(command.DocumentId, DocumentPermission.Write, ct);

        var document = await documents.GetByIdAsync(command.DocumentId, ct)
            ?? throw new NotFoundException(nameof(Document), command.DocumentId);

        var previous = document.FileName;
        document.Rename(command.FileName, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.DocumentRenamed, "document", document.Id, document.ProjectId,
            new Dictionary<string, string> { ["from"] = previous, ["to"] = document.FileName });
        return DocumentDto.FromEntity(document);
    }
}

internal sealed class MoveDocumentsCommandHandler(
    IDocumentRepository documents,
    IFolderRepository folders,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<MoveDocumentsCommand, Unit>
{
    public async Task<Unit> Handle(MoveDocumentsCommand command, CancellationToken ct)
    {
        var ids = command.DocumentIds.Distinct().ToList();
        var found = await documents.GetByIdsAsync(ids, ct);
        if (found.Count != ids.Count)
            throw new NotFoundException(nameof(Document), ids.Except(found.Select(d => d.Id)).First());

        foreach (var projectId in found.Select(d => d.ProjectId).Distinct())
            await access.RequireProjectAsync(projectId, ProjectRole.Editor, ct);

        if (command.FolderId is { } folderId)
        {
            await access.RequireFolderAsync(folderId, ProjectRole.Editor, ct);
            var folder = await folders.GetByIdAsync(folderId, ct)
                ?? throw new NotFoundException(nameof(Folder), folderId);
            if (found.Any(d => d.ProjectId != folder.ProjectId))
                throw new DomainException("Documents can only be moved to a folder in their own project.");
        }

        var now = clock.GetUtcNow();
        var previous = found.ToDictionary(d => d.Id, d => d.FolderId);
        foreach (var document in found)
            document.MoveToFolder(command.FolderId, now);

        await unitOfWork.SaveChangesAsync(ct);

        foreach (var document in found)
            audit.Record(AuditActions.DocumentMoved, "document", document.Id, document.ProjectId, new Dictionary<string, string>
            {
                ["fromFolder"] = previous[document.Id]?.ToString() ?? "root",
                ["toFolder"] = command.FolderId?.ToString() ?? "root",
            });
        return Unit.Value;
    }
}
