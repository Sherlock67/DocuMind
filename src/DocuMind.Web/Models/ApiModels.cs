namespace DocuMind.Web.Models;

// Shapes of the DocuMind API's JSON. Enums travel as strings (the API uses JsonStringEnumConverter).

public enum UserRole { User, Manager, Admin }

public enum ProjectRole { Viewer, Editor, Owner }

public enum CollectionVisibility { Private, Project }

public enum DocumentStatus { Queued, Processing, Completed, Failed }

public enum UploadOutcome { Accepted, Duplicate, Rejected }

public enum DocumentSort { UploadedAt, Name, Size, Status }

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

public sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public sealed record StatusCounts(int Queued, int Processing, int Completed, int Failed, int Total);

public sealed record ProjectDto(
    Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ProjectRole? MyRole, StatusCounts Documents, long TotalSizeBytes);

public sealed record ProjectMemberDto(Guid UserId, string Email, string DisplayName, ProjectRole Role, DateTimeOffset AddedAt);

public sealed record FolderTreeNode(Guid Id, Guid? ParentId, string Name, int DocumentCount, int TotalDocumentCount, IReadOnlyList<FolderTreeNode> Children);

public sealed record CollectionDto(
    Guid Id, Guid ProjectId, Guid OwnerId, CollectionVisibility Visibility, string Name, string? Description,
    int DocumentCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CollectionShareDto(Guid UserId, string Email, string DisplayName, bool IsProjectMember, DateTimeOffset SharedAt);

public sealed record DocumentDto(
    Guid Id, Guid ProjectId, Guid? FolderId, string FileName, string ContentType, long SizeBytes,
    DocumentStatus Status, string? FailureReason, int PageCount, int ChunkCount,
    DateTimeOffset UploadedAt, DateTimeOffset? ProcessingStartedAt, DateTimeOffset? ProcessedAt, DateTimeOffset UpdatedAt);

public sealed record DocumentStatusDto(
    Guid Id, string FileName, DocumentStatus Status, string? FailureReason, int ChunkCount,
    DateTimeOffset UploadedAt, DateTimeOffset? ProcessingStartedAt, DateTimeOffset? ProcessedAt);

public sealed record UploadItemResult(string FileName, UploadOutcome Outcome, Guid? DocumentId, DocumentStatus? Status, string? Error);

public sealed record BatchUploadResult(IReadOnlyList<UploadItemResult> Items, int Accepted, int Duplicates, int Rejected);

public sealed record PreviewSection(int SectionIndex, int? PageNumber, string Text);

public sealed record DocumentPreviewDto(Guid DocumentId, string FileName, DocumentStatus Status, int TotalSections, int Page, int PageSize, IReadOnlyList<PreviewSection> Sections);

public sealed record AuditEntryDto(
    Guid Id, DateTimeOffset OccurredAt, Guid? ActorId, string? ActorEmail, string Action, bool Succeeded,
    string? ResourceType, Guid? ResourceId, Guid? ProjectId, IReadOnlyDictionary<string, string> Details, string? IpAddress);

public sealed record DocumentQuery(
    Guid ProjectId,
    Guid? FolderId = null,
    bool IncludeSubfolders = false,
    bool RootOnly = false,
    Guid? CollectionId = null,
    DocumentStatus? Status = null,
    string? Search = null,
    DocumentSort SortBy = DocumentSort.UploadedAt,
    bool Descending = true,
    int Page = 1,
    int PageSize = 20);

public sealed record AuditQuery(
    Guid? ProjectId = null,
    string? Action = null,
    bool? Succeeded = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>The signed-in user's effective rights in one project, used to show or hide actions.</summary>
/// <remarks>Only a UI convenience: the API enforces every rule regardless of what the page shows.</remarks>
public sealed record ProjectAccess(ProjectRole? Role, bool IsAdmin)
{
    public ProjectRole? Effective => IsAdmin ? ProjectRole.Owner : Role;
    public bool CanEdit => Effective >= ProjectRole.Editor;
    public bool IsOwner => Effective == ProjectRole.Owner;

    /// <summary>Mirrors the server's rule: the owner (while a member) or, for project collections, a project Owner.</summary>
    public bool CanManage(CollectionDto c, Guid me) =>
        IsAdmin || (c.OwnerId == me && Role is not null) || (c.Visibility == CollectionVisibility.Project && Role == ProjectRole.Owner);
}
