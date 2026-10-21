using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Common;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Collections;

public sealed record CollectionDto(
    Guid Id,
    Guid ProjectId,
    Guid OwnerId,
    CollectionVisibility Visibility,
    string Name,
    string? Description,
    int DocumentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ----- Commands ---------------------------------------------------------------------------------

/// <summary>
/// Creates a collection owned by the caller. Any project member can create a Private one;
/// a Project-visible one needs the Editor role.
/// </summary>
public sealed record CreateCollectionCommand(
    Guid ProjectId,
    string Name,
    string? Description = null,
    CollectionVisibility Visibility = CollectionVisibility.Private) : ICommand<CollectionDto>;

public sealed record UpdateCollectionCommand(
    Guid CollectionId,
    string Name,
    string? Description = null,
    CollectionVisibility Visibility = CollectionVisibility.Private) : ICommand<Unit>;

/// <summary>Deletes the collection only; its documents stay where they are.</summary>
public sealed record DeleteCollectionCommand(Guid CollectionId) : ICommand<Unit>;

public sealed record AddDocumentsToCollectionCommand(Guid CollectionId, IReadOnlyList<Guid> DocumentIds) : ICommand<int>;

public sealed record RemoveDocumentFromCollectionCommand(Guid CollectionId, Guid DocumentId) : ICommand<Unit>;

internal sealed class CreateCollectionCommandValidator : AbstractValidator<CreateCollectionCommand>
{
    public CreateCollectionCommandValidator()
    {
        RuleFor(x => x.ProjectId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Collection.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(Collection.MaxDescriptionLength);
        RuleFor(x => x.Visibility).IsInEnum();
    }
}

internal sealed class UpdateCollectionCommandValidator : AbstractValidator<UpdateCollectionCommand>
{
    public UpdateCollectionCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Collection.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(Collection.MaxDescriptionLength);
    }
}

internal sealed class AddDocumentsToCollectionCommandValidator : AbstractValidator<AddDocumentsToCollectionCommand>
{
    public AddDocumentsToCollectionCommandValidator() =>
        RuleFor(x => x.DocumentIds).NotEmpty().Must(ids => ids.Count <= 500).WithMessage("At most 500 documents per request.");
}

internal sealed class CreateCollectionCommandHandler(
    ICollectionRepository collections,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    ICurrentUser user,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<CreateCollectionCommand, CollectionDto>
{
    public async Task<CollectionDto> Handle(CreateCollectionCommand command, CancellationToken ct)
    {
        await access.RequireProjectAsync(command.ProjectId,
            command.Visibility == CollectionVisibility.Project ? ProjectRole.Editor : ProjectRole.Viewer, ct);
        if (await collections.NameExistsAsync(command.ProjectId, command.Name.Trim(), excludeId: null, ct))
            throw new DomainException($"A collection named '{command.Name.Trim()}' already exists in this project.");

        var collection = Collection.Create(command.ProjectId, user.Id, command.Name, command.Description, command.Visibility, clock.GetUtcNow());
        collections.Add(collection);
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.CollectionCreated, "collection", collection.Id, collection.ProjectId,
            new Dictionary<string, string> { ["name"] = collection.Name, ["visibility"] = collection.Visibility.ToString() });
        return new CollectionDto(collection.Id, collection.ProjectId, collection.OwnerId, collection.Visibility,
            collection.Name, collection.Description, 0, collection.CreatedAt, collection.UpdatedAt);
    }
}

internal sealed class UpdateCollectionCommandHandler(ICollectionRepository collections, IUnitOfWork unitOfWork, IAccessControl access, TimeProvider clock)
    : ICommandHandler<UpdateCollectionCommand, Unit>
{
    public async Task<Unit> Handle(UpdateCollectionCommand command, CancellationToken ct)
    {
        var info = await access.RequireCollectionAsync(command.CollectionId, CollectionPermission.Manage, ct);
        if (command.Visibility == CollectionVisibility.Project && info.ProjectRole is not >= ProjectRole.Editor)
            throw new ForbiddenException("Making a collection visible to the whole project requires the Editor role.");

        var collection = await collections.GetByIdAsync(command.CollectionId, ct)
            ?? throw new NotFoundException(nameof(Collection), command.CollectionId);
        if (await collections.NameExistsAsync(collection.ProjectId, command.Name.Trim(), collection.Id, ct))
            throw new DomainException($"A collection named '{command.Name.Trim()}' already exists in this project.");

        collection.Update(command.Name, command.Description, command.Visibility, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

internal sealed class DeleteCollectionCommandHandler(ICollectionRepository collections, IUnitOfWork unitOfWork, IAccessControl access, IAuditTrail audit)
    : ICommandHandler<DeleteCollectionCommand, Unit>
{
    public async Task<Unit> Handle(DeleteCollectionCommand command, CancellationToken ct)
    {
        await access.RequireCollectionAsync(command.CollectionId, CollectionPermission.Manage, ct);

        var collection = await collections.GetByIdAsync(command.CollectionId, ct)
            ?? throw new NotFoundException(nameof(Collection), command.CollectionId);

        collections.Remove(collection);
        await unitOfWork.SaveChangesAsync(ct);
        audit.Record(AuditActions.CollectionDeleted, "collection", collection.Id, collection.ProjectId,
            new Dictionary<string, string> { ["name"] = collection.Name });
        return Unit.Value;
    }
}

internal sealed class AddDocumentsToCollectionCommandHandler(
    ICollectionRepository collections,
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    TimeProvider clock) : ICommandHandler<AddDocumentsToCollectionCommand, int>
{
    public async Task<int> Handle(AddDocumentsToCollectionCommand command, CancellationToken ct)
    {
        // Managing the collection already implies membership of its project, so every
        // document of that project is readable by the caller.
        await access.RequireCollectionAsync(command.CollectionId, CollectionPermission.Manage, ct);

        var collection = await collections.GetWithItemsAsync(command.CollectionId, ct)
            ?? throw new NotFoundException(nameof(Collection), command.CollectionId);

        var requested = command.DocumentIds.Distinct().ToList();
        var valid = await documents.FilterIdsInProjectAsync(collection.ProjectId, requested, ct);
        if (valid.Count != requested.Count)
        {
            var missing = requested.Except(valid).ToList();
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(
                nameof(command.DocumentIds),
                $"Documents not found in this collection's project: {string.Join(", ", missing)}")]);
        }

        var added = collection.AddDocuments(valid, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);
        return added;
    }
}

internal sealed class RemoveDocumentFromCollectionCommandHandler(ICollectionRepository collections, IUnitOfWork unitOfWork, IAccessControl access, TimeProvider clock)
    : ICommandHandler<RemoveDocumentFromCollectionCommand, Unit>
{
    public async Task<Unit> Handle(RemoveDocumentFromCollectionCommand command, CancellationToken ct)
    {
        await access.RequireCollectionAsync(command.CollectionId, CollectionPermission.Manage, ct);

        var collection = await collections.GetWithItemsAsync(command.CollectionId, ct)
            ?? throw new NotFoundException(nameof(Collection), command.CollectionId);

        if (!collection.RemoveDocument(command.DocumentId, clock.GetUtcNow()))
            throw new NotFoundException("Collection membership", command.DocumentId);

        await unitOfWork.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ----- Queries ----------------------------------------------------------------------------------

public sealed record GetCollectionsQuery(Guid ProjectId) : IQuery<IReadOnlyList<CollectionDto>>;

public sealed record GetCollectionByIdQuery(Guid CollectionId) : IQuery<CollectionDto>;

/// <summary>Collections of the project the caller can see: project-wide ones, their own, and ones shared with them.</summary>
internal sealed class GetCollectionsQueryHandler(IReadDbContext db, IAccessControl access) : IQueryHandler<GetCollectionsQuery, IReadOnlyList<CollectionDto>>
{
    public async Task<IReadOnlyList<CollectionDto>> Handle(GetCollectionsQuery query, CancellationToken ct)
    {
        await access.RequireProjectAsync(query.ProjectId, ProjectRole.Viewer, ct);

        return await db.VisibleCollections(access.Principal)
            .Where(c => c.ProjectId == query.ProjectId)
            .OrderBy(c => c.Name)
            .Select(c => new CollectionDto(c.Id, c.ProjectId, c.OwnerId, c.Visibility, c.Name, c.Description,
                db.CollectionItems.Count(i => i.CollectionId == c.Id), c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);
    }
}

internal sealed class GetCollectionByIdQueryHandler(IReadDbContext db, IAccessControl access) : IQueryHandler<GetCollectionByIdQuery, CollectionDto>
{
    public async Task<CollectionDto> Handle(GetCollectionByIdQuery query, CancellationToken ct)
    {
        await access.RequireCollectionAsync(query.CollectionId, CollectionPermission.View, ct);

        return await db.Collections
            .Where(c => c.Id == query.CollectionId)
            .Select(c => new CollectionDto(c.Id, c.ProjectId, c.OwnerId, c.Visibility, c.Name, c.Description,
                db.CollectionItems.Count(i => i.CollectionId == c.Id), c.CreatedAt, c.UpdatedAt))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(nameof(Collection), query.CollectionId);
    }
}
