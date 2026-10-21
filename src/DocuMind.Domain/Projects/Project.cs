using DocuMind.Domain.Common;
using DocuMind.Domain.Security;

namespace DocuMind.Domain.Projects;

/// <summary>
/// Top-level workspace and the security boundary: every document, folder and collection belongs to
/// exactly one project, and access to a project's documents is granted through its members.
/// </summary>
public sealed class Project : Entity, IAggregateRoot
{
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 1000;

    private readonly List<ProjectMember> _members = [];

    private Project() { } // EF Core

    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<ProjectMember> Members => _members.AsReadOnly();

    /// <summary>Creates a project with <paramref name="ownerId"/> as its first Owner.</summary>
    public static Project Create(string name, string? description, Guid ownerId, DateTimeOffset now)
    {
        var project = new Project
        {
            Name = Names.Require(name, MaxNameLength, "Project name"),
            Description = Names.Optional(description, MaxDescriptionLength),
            CreatedBy = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        project._members.Add(new ProjectMember(project.Id, ownerId, ProjectRole.Owner, ownerId, now));
        return project;
    }

    public void Update(string name, string? description, DateTimeOffset now)
    {
        Name = Names.Require(name, MaxNameLength, "Project name");
        Description = Names.Optional(description, MaxDescriptionLength);
        UpdatedAt = now;
    }

    public ProjectRole? RoleOf(Guid userId) => _members.FirstOrDefault(m => m.UserId == userId)?.Role;

    public ProjectMember AddMember(Guid userId, ProjectRole role, Guid addedBy, DateTimeOffset now)
    {
        if (_members.Any(m => m.UserId == userId))
            throw new DomainException("That user is already a member of this project.");

        var member = new ProjectMember(Id, userId, role, addedBy, now);
        _members.Add(member);
        UpdatedAt = now;
        return member;
    }

    public void ChangeMemberRole(Guid userId, ProjectRole role, DateTimeOffset now)
    {
        var member = _members.FirstOrDefault(m => m.UserId == userId)
            ?? throw new DomainException("That user is not a member of this project.");

        if (member.Role == ProjectRole.Owner && role != ProjectRole.Owner)
            EnsureAnotherOwner(userId);

        member.Role = role;
        UpdatedAt = now;
    }

    public void RemoveMember(Guid userId, DateTimeOffset now)
    {
        var member = _members.FirstOrDefault(m => m.UserId == userId)
            ?? throw new DomainException("That user is not a member of this project.");

        if (member.Role == ProjectRole.Owner)
            EnsureAnotherOwner(userId);

        _members.Remove(member);
        UpdatedAt = now;
    }

    private void EnsureAnotherOwner(Guid leavingOwnerId)
    {
        if (!_members.Any(m => m.Role == ProjectRole.Owner && m.UserId != leavingOwnerId))
            throw new DomainException("A project must keep at least one Owner. Promote someone else first.");
    }
}

/// <summary>Grants a user a <see cref="ProjectRole"/> on a project.</summary>
public sealed class ProjectMember
{
    private ProjectMember() { } // EF Core

    internal ProjectMember(Guid projectId, Guid userId, ProjectRole role, Guid addedBy, DateTimeOffset addedAt)
    {
        ProjectId = projectId;
        UserId = userId;
        Role = role;
        AddedBy = addedBy;
        AddedAt = addedAt;
    }

    public Guid ProjectId { get; private set; }
    public Guid UserId { get; private set; }
    public ProjectRole Role { get; internal set; }
    public Guid AddedBy { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
}

internal static class Names
{
    public static string Require(string? value, int maxLength, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new DomainException($"{what} is required.");
        if (trimmed.Length > maxLength) throw new DomainException($"{what} must be at most {maxLength} characters.");
        return trimmed;
    }

    public static string? Optional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > maxLength ? throw new DomainException($"Text must be at most {maxLength} characters.") : trimmed;
    }
}
