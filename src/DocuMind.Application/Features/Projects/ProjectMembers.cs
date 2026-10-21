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

namespace DocuMind.Application.Features.Projects;

public sealed record ProjectMemberDto(Guid UserId, string Email, string DisplayName, ProjectRole Role, DateTimeOffset AddedAt);

public sealed record GetProjectMembersQuery(Guid ProjectId) : IQuery<IReadOnlyList<ProjectMemberDto>>;

/// <summary>Shares the project with a user, identified by email. Requires the Owner role.</summary>
public sealed record AddProjectMemberCommand(Guid ProjectId, string Email, ProjectRole Role) : ICommand<ProjectMemberDto>;

public sealed record ChangeProjectMemberRoleCommand(Guid ProjectId, Guid UserId, ProjectRole Role) : ICommand<Unit>;

/// <summary>Owners can remove anyone; any member can remove themselves (leave the project).</summary>
public sealed record RemoveProjectMemberCommand(Guid ProjectId, Guid UserId) : ICommand<Unit>;

internal sealed class AddProjectMemberCommandValidator : AbstractValidator<AddProjectMemberCommand>
{
    public AddProjectMemberCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Role).IsInEnum();
    }
}

internal sealed class ChangeProjectMemberRoleCommandValidator : AbstractValidator<ChangeProjectMemberRoleCommand>
{
    public ChangeProjectMemberRoleCommandValidator() => RuleFor(x => x.Role).IsInEnum();
}

internal sealed class GetProjectMembersQueryHandler(IReadDbContext db, IAccessControl access, IUserDirectory users)
    : IQueryHandler<GetProjectMembersQuery, IReadOnlyList<ProjectMemberDto>>
{
    public async Task<IReadOnlyList<ProjectMemberDto>> Handle(GetProjectMembersQuery query, CancellationToken ct)
    {
        await access.RequireProjectAsync(query.ProjectId, ProjectRole.Viewer, ct);

        var members = await db.ProjectMembers.Where(m => m.ProjectId == query.ProjectId).ToListAsync(ct);
        var profiles = await users.GetByIdsAsync(members.Select(m => m.UserId).ToList(), ct);

        return members
            .Select(m => profiles.TryGetValue(m.UserId, out var u)
                ? new ProjectMemberDto(m.UserId, u.Email, u.DisplayName, m.Role, m.AddedAt)
                : new ProjectMemberDto(m.UserId, "(unknown)", "(unknown)", m.Role, m.AddedAt))
            .OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal sealed class AddProjectMemberCommandHandler(
    IProjectRepository projects,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IUserDirectory users,
    ICurrentUser caller,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<AddProjectMemberCommand, ProjectMemberDto>
{
    public async Task<ProjectMemberDto> Handle(AddProjectMemberCommand command, CancellationToken ct)
    {
        await access.RequireProjectAsync(command.ProjectId, ProjectRole.Owner, ct);

        var user = await users.FindByEmailAsync(command.Email.Trim(), ct);
        if (user is null || !user.IsActive)
            throw new NotFoundException("Active user with email", command.Email.Trim());

        var project = await projects.GetWithMembersAsync(command.ProjectId, ct)
            ?? throw new NotFoundException(nameof(Project), command.ProjectId);

        var member = project.AddMember(user.Id, command.Role, caller.Id, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.MemberAdded, "project", project.Id, project.Id, new Dictionary<string, string>
        {
            ["userId"] = user.Id.ToString(),
            ["email"] = user.Email,
            ["role"] = command.Role.ToString(),
        });
        return new ProjectMemberDto(user.Id, user.Email, user.DisplayName, member.Role, member.AddedAt);
    }
}

internal sealed class ChangeProjectMemberRoleCommandHandler(
    IProjectRepository projects,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<ChangeProjectMemberRoleCommand, Unit>
{
    public async Task<Unit> Handle(ChangeProjectMemberRoleCommand command, CancellationToken ct)
    {
        await access.RequireProjectAsync(command.ProjectId, ProjectRole.Owner, ct);

        var project = await projects.GetWithMembersAsync(command.ProjectId, ct)
            ?? throw new NotFoundException(nameof(Project), command.ProjectId);
        var previous = project.RoleOf(command.UserId) ?? throw new NotFoundException("Project member", command.UserId);

        project.ChangeMemberRole(command.UserId, command.Role, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.MemberRoleChanged, "project", project.Id, project.Id, new Dictionary<string, string>
        {
            ["userId"] = command.UserId.ToString(),
            ["from"] = previous.ToString(),
            ["to"] = command.Role.ToString(),
        });
        return Unit.Value;
    }
}

internal sealed class RemoveProjectMemberCommandHandler(
    IProjectRepository projects,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    ICurrentUser caller,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<RemoveProjectMemberCommand, Unit>
{
    public async Task<Unit> Handle(RemoveProjectMemberCommand command, CancellationToken ct)
    {
        var leaving = command.UserId == caller.Id;
        await access.RequireProjectAsync(command.ProjectId, leaving ? ProjectRole.Viewer : ProjectRole.Owner, ct);

        var project = await projects.GetWithMembersAsync(command.ProjectId, ct)
            ?? throw new NotFoundException(nameof(Project), command.ProjectId);
        if (project.RoleOf(command.UserId) is null)
            throw new NotFoundException("Project member", command.UserId);

        project.RemoveMember(command.UserId, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.MemberRemoved, "project", project.Id, project.Id, new Dictionary<string, string>
        {
            ["userId"] = command.UserId.ToString(),
            ["leftVoluntarily"] = leaving.ToString(),
        });
        return Unit.Value;
    }
}
