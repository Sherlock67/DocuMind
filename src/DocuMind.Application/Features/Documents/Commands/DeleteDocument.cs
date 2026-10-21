using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Security;

namespace DocuMind.Application.Features.Documents.Commands;

public sealed record DeleteDocumentCommand(Guid DocumentId) : ICommand<Unit>;

internal sealed class DeleteDocumentCommandHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    DocumentFileCleanup cleanup) : ICommandHandler<DeleteDocumentCommand, Unit>
{
    public async Task<Unit> Handle(DeleteDocumentCommand command, CancellationToken ct)
    {
        await access.RequireDocumentAsync(command.DocumentId, DocumentPermission.Write, ct);

        var document = await documents.GetByIdAsync(command.DocumentId, ct)
            ?? throw new NotFoundException(nameof(Document), command.DocumentId);

        documents.Remove(document); // chunks, pages and collection memberships cascade
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.DocumentDeleted, "document", document.Id, document.ProjectId,
            new Dictionary<string, string> { ["fileName"] = document.FileName, ["sha256"] = document.ContentHash });

        // Delete the blob only after the row is gone; an orphaned file is harmless, a dangling row is not.
        await cleanup.DeleteFilesAsync([document.StoragePath], ct);
        return Unit.Value;
    }
}
