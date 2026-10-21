using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Collections;

public sealed record CollectionShareDto(Guid UserId, string Email, string DisplayName, bool IsProjectMember, DateTimeOffset SharedAt);

public sealed record GetCollectionSharesQuery(Guid CollectionId) : IQuery<IReadOnlyList<CollectionShareDto>>;

/// <summary>
/// Gives a user read access to the collection and every document in it. Sharing with someone who
/// is not a member of the project grants access beyond the project, so it requires being a project Owner.
/// </summary>
public sealed record ShareCollectionCommand(Guid CollectionId, string Email) : ICommand<CollectionShareDto>;

public sealed record UnshareCollectionCommand(Guid CollectionId, Guid UserId) : ICommand<Unit>;

internal sealed class ShareCollectionCommandValidator : AbstractValidator<ShareCollectionCommand>
{
    public ShareCollectionCommandValidator() => RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
}

internal sealed class GetCollectionSharesQueryHandler(IReadDbContext db, IAccessControl access, IUserDirectory users)
    : IQueryHandler<GetCollectionSharesQuery, IReadOnlyList<CollectionShareDto>>
{
    public async Task<IReadOnlyList<CollectionShareDto>> Handle(GetCollectionSharesQuery query, CancellationToken ct)
    {
        var info = await access.RequireCollectionAsync(query.CollectionId, CollectionPermission.Manage, ct);

        var shares = await db.CollectionShares.Where(s => s.CollectionId == query.CollectionId).ToListAsync(ct);
        var ids = shares.Select(s => s.UserId).ToList();
        var members = await db.ProjectMembers.Where(m => m.ProjectId == info.ProjectId && ids.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct);
        var profiles = await users.GetByIdsAsync(ids, ct);

        return shares
            .Select(s => new CollectionShareDto(
                s.UserId,
                profiles.GetValueOrDefault(s.UserId)?.Email ?? "(unknown)",
                profiles.GetValueOrDefault(s.UserId)?.DisplayName ?? "(unknown)",
                members.Contains(s.UserId),
                s.SharedAt))
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal sealed class ShareCollectionCommandHandler(
    ICollectionRepository collections,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IPermissionStore permissions,
    IUserDirectory users,
    ICurrentUser caller,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<ShareCollectionCommand, CollectionShareDto>
{
    public async Task<CollectionShareDto> Handle(ShareCollectionCommand command, CancellationToken ct)
    {
        var info = await access.RequireCollectionAsync(command.CollectionId, CollectionPermission.Manage, ct);

        var target = await users.FindByEmailAsync(command.Email.Trim(), ct);
        if (target is null || !target.IsActive)
            throw new NotFoundException("Active user with email", command.Email.Trim());

        var targetIsMember = await permissions.GetProjectRoleAsync(target.Id, info.ProjectId, ct) is not null;
        if (!targetIsMember && info.ProjectRole != ProjectRole.Owner)
            throw new ForbiddenException("Only a project Owner can share a collection with someone outside the project.");

        var collection = await collections.GetWithSharesAsync(command.CollectionId, ct)
            ?? throw new NotFoundException(nameof(Collection), command.CollectionId);

        var now = clock.GetUtcNow();
        collection.ShareWith(target.Id, caller.Id, now);
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.CollectionShared, "collection", collection.Id, collection.ProjectId, new Dictionary<string, string>
        {
            ["userId"] = target.Id.ToString(),
            ["email"] = target.Email,
            ["outsideProject"] = (!targetIsMember).ToString(),
        });
        return new CollectionShareDto(target.Id, target.Email, target.DisplayName, targetIsMember, now);
    }
}

internal sealed class UnshareCollectionCommandHandler(
    ICollectionRepository collections,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<UnshareCollectionCommand, Unit>
{
    public async Task<Unit> Handle(UnshareCollectionCommand command, CancellationToken ct)
    {
        await access.RequireCollectionAsync(command.CollectionId, CollectionPermission.Manage, ct);

        var collection = await collections.GetWithSharesAsync(command.CollectionId, ct)
            ?? throw new NotFoundException(nameof(Collection), command.CollectionId);

        if (!collection.Unshare(command.UserId, clock.GetUtcNow()))
            throw new NotFoundException("Collection share", command.UserId);

        await unitOfWork.SaveChangesAsync(ct);
        audit.Record(AuditActions.CollectionUnshared, "collection", collection.Id, collection.ProjectId,
            new Dictionary<string, string> { ["userId"] = command.UserId.ToString() });
        return Unit.Value;
    }
}
