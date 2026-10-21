using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Security;

namespace DocuMind.Application.Features.Documents.Commands;

/// <summary>Re-runs ingestion, e.g. after a failure or after switching embedding model.</summary>
public sealed record ReprocessDocumentCommand(Guid DocumentId) : ICommand<Unit>;

internal sealed class ReprocessDocumentCommandHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IIngestionQueue queue,
    IAccessControl access,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<ReprocessDocumentCommand, Unit>
{
    public async Task<Unit> Handle(ReprocessDocumentCommand command, CancellationToken ct)
    {
        await access.RequireDocumentAsync(command.DocumentId, DocumentPermission.Write, ct);

        var document = await documents.GetByIdAsync(command.DocumentId, ct)
            ?? throw new NotFoundException(nameof(Document), command.DocumentId);

        document.Requeue(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);
        audit.Record(AuditActions.DocumentReprocessed, "document", document.Id, document.ProjectId,
            new Dictionary<string, string> { ["fileName"] = document.FileName });
        await queue.EnqueueAsync(document.Id, ct);
        return Unit.Value;
    }
}
