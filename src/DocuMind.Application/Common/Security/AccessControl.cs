using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;

namespace DocuMind.Application.Common.Security;

public enum DocumentPermission
{
    /// <summary>View, preview, download, search, chat.</summary>
    Read,

    /// <summary>Rename, move, delete, reprocess.</summary>
    Write,
}

public enum CollectionPermission
{
    /// <summary>See the collection and read its documents.</summary>
    View,

    /// <summary>Rename, change visibility, add/remove documents, share, delete.</summary>
    Manage,
}

/// <summary>
/// Resource-level authorization policy, used by every handler that touches a project, folder,
/// collection or document.
/// <list type="bullet">
/// <item>No access at all → <see cref="NotFoundException"/>, so ids of other people's resources can't be probed.</item>
/// <item>Visible but not permitted → <see cref="ForbiddenException"/>, recorded in the audit log.</item>
/// <item>Admins pass every check.</item>
/// </list>
/// </summary>
public interface IAccessControl
{
    AccessPrincipal Principal { get; }

    /// <summary>Returns the caller's effective role (Owner for admins).</summary>
    Task<ProjectRole> RequireProjectAsync(Guid projectId, ProjectRole minimum, CancellationToken ct);

    /// <summary>Returns the folder's project id after checking the caller's role on it.</summary>
    Task<Guid> RequireFolderAsync(Guid folderId, ProjectRole minimum, CancellationToken ct);

    Task<DocumentAccessInfo> RequireDocumentAsync(Guid documentId, DocumentPermission permission, CancellationToken ct);

    Task<CollectionAccessInfo> RequireCollectionAsync(Guid collectionId, CollectionPermission permission, CancellationToken ct);
}

internal sealed class AccessControl(ICurrentUser user, IPermissionStore permissions, IAuditTrail audit) : IAccessControl
{
    private bool IsAdmin => user.Role == UserRole.Admin;

    public AccessPrincipal Principal => new(user.Id, IsAdmin);

    public async Task<ProjectRole> RequireProjectAsync(Guid projectId, ProjectRole minimum, CancellationToken ct)
    {
        if (IsAdmin)
            return await permissions.ProjectExistsAsync(projectId, ct)
                ? ProjectRole.Owner
                : throw new NotFoundException(nameof(Project), projectId);

        var role = await permissions.GetProjectRoleAsync(user.Id, projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        if (role < minimum)
            Deny("project", projectId, projectId, $"Requires the {minimum} role on this project; you are {role}.");

        return role;
    }

    public async Task<Guid> RequireFolderAsync(Guid folderId, ProjectRole minimum, CancellationToken ct)
    {
        var projectId = await permissions.GetFolderProjectIdAsync(folderId, ct)
            ?? throw new NotFoundException(nameof(Folder), folderId);

        try
        {
            await RequireProjectAsync(projectId, minimum, ct);
        }
        catch (NotFoundException)
        {
            throw new NotFoundException(nameof(Folder), folderId);
        }

        return projectId;
    }

    public async Task<DocumentAccessInfo> RequireDocumentAsync(Guid documentId, DocumentPermission permission, CancellationToken ct)
    {
        var info = await permissions.GetDocumentAccessAsync(user.Id, documentId, ct)
            ?? throw new NotFoundException(nameof(Document), documentId);

        if (IsAdmin)
            return info with { ProjectRole = ProjectRole.Owner };

        var canRead = info.ProjectRole is not null || info.SharedViaCollection;
        if (!canRead)
            throw new NotFoundException(nameof(Document), documentId);

        if (permission == DocumentPermission.Write && info.ProjectRole is not >= ProjectRole.Editor)
            Deny("document", documentId, info.ProjectId, "Changing documents requires the Editor role on the project.");

        return info;
    }

    public async Task<CollectionAccessInfo> RequireCollectionAsync(Guid collectionId, CollectionPermission permission, CancellationToken ct)
    {
        var info = await permissions.GetCollectionAccessAsync(user.Id, collectionId, ct)
            ?? throw new NotFoundException(nameof(Collection), collectionId);

        if (IsAdmin)
            return info with { ProjectRole = ProjectRole.Owner };

        var isOwner = info.OwnerId == user.Id;
        var canView = isOwner
            || info.SharedWithUser
            || (info.Visibility == CollectionVisibility.Project && info.ProjectRole is not null);

        if (!canView)
            throw new NotFoundException(nameof(Collection), collectionId);

        if (permission == CollectionPermission.Manage)
        {
            // The owner manages their collection only while still in the project; project Owners
            // manage project-wide collections, but never someone else's private one.
            var canManage = (isOwner && info.ProjectRole is not null)
                || (info.Visibility == CollectionVisibility.Project && info.ProjectRole == ProjectRole.Owner);
            if (!canManage)
                Deny("collection", collectionId, info.ProjectId, "Only the collection's owner (or a project Owner, for project collections) can change it.");
        }

        return info;
    }

    private void Deny(string resourceType, Guid resourceId, Guid? projectId, string reason)
    {
        audit.Record(AuditActions.AccessDenied, resourceType, resourceId, projectId, succeeded: false,
            details: new Dictionary<string, string> { ["reason"] = reason });
        throw new ForbiddenException(reason);
    }
}

/// <summary>
/// Read-side counterparts of <see cref="AccessControl"/> for list queries, expressed as LINQ so
/// they compose into the SQL instead of filtering in memory.
/// </summary>
public static class ReadAccessFilters
{
    public static IQueryable<Project> ReadableProjects(this IReadDbContext db, AccessPrincipal p) =>
        p.IsAdmin ? db.Projects : db.Projects.Where(pr => db.ProjectMembers.Any(m => m.ProjectId == pr.Id && m.UserId == p.UserId));

    /// <summary>Documents in projects the user belongs to, plus documents in collections shared with them.</summary>
    public static IQueryable<Document> ReadableDocuments(this IReadDbContext db, AccessPrincipal p) =>
        p.IsAdmin
            ? db.Documents
            : db.Documents.Where(d =>
                db.ProjectMembers.Any(m => m.ProjectId == d.ProjectId && m.UserId == p.UserId) ||
                db.CollectionItems.Any(i => i.DocumentId == d.Id &&
                    db.CollectionShares.Any(s => s.CollectionId == i.CollectionId && s.UserId == p.UserId)));

    public static IQueryable<Collection> VisibleCollections(this IReadDbContext db, AccessPrincipal p) =>
        p.IsAdmin
            ? db.Collections
            : db.Collections.Where(c =>
                c.OwnerId == p.UserId ||
                db.CollectionShares.Any(s => s.CollectionId == c.Id && s.UserId == p.UserId) ||
                (c.Visibility == CollectionVisibility.Project && db.ProjectMembers.Any(m => m.ProjectId == c.ProjectId && m.UserId == p.UserId)));
}
