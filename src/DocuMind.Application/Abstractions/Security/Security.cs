using DocuMind.Application.Common.Models;
using DocuMind.Domain.Auditing;
using DocuMind.Domain.Security;

namespace DocuMind.Application.Abstractions.Security;

/// <summary>Who is making the current request. Populated from the validated access token.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>True for work started by the app itself (background ingestion), never for an HTTP request.</summary>
    bool IsSystem { get; }

    /// <summary>The user's id. Throws when <see cref="IsAuthenticated"/> is false.</summary>
    Guid Id { get; }

    string? Email { get; }
    UserRole Role { get; }
    Guid? SessionId { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
}

/// <summary>The identity that retrieval and read-side filters are evaluated for.</summary>
public sealed record AccessPrincipal(Guid UserId, bool IsAdmin);

public sealed record DocumentAccessInfo(Guid DocumentId, Guid ProjectId, ProjectRole? ProjectRole, bool SharedViaCollection);

public sealed record CollectionAccessInfo(
    Guid CollectionId,
    Guid ProjectId,
    Guid OwnerId,
    CollectionVisibility Visibility,
    ProjectRole? ProjectRole,
    bool SharedWithUser);

/// <summary>
/// Membership lookups for authorization decisions. Always reads the primary database, never a
/// replica, so a just-revoked permission can't be honoured from stale data.
/// </summary>
public interface IPermissionStore
{
    Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct);
    Task<ProjectRole?> GetProjectRoleAsync(Guid userId, Guid projectId, CancellationToken ct);
    Task<DocumentAccessInfo?> GetDocumentAccessAsync(Guid userId, Guid documentId, CancellationToken ct);
    Task<CollectionAccessInfo?> GetCollectionAccessAsync(Guid userId, Guid collectionId, CancellationToken ct);
    Task<Guid?> GetFolderProjectIdAsync(Guid folderId, CancellationToken ct);
}

// ----- Identity ---------------------------------------------------------------------------------

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    UserRole Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User);

public sealed record ClientInfo(string? IpAddress, string? UserAgent);

/// <summary>Accounts, credentials and sessions. Implemented on ASP.NET Core Identity.</summary>
public interface IIdentityService
{
    /// <summary>
    /// Self-registration. The very first account becomes Admin (bootstrap); everyone else starts as User.
    /// Refused with <c>ForbiddenException</c> when self-registration is disabled and users already exist.
    /// </summary>
    Task<UserDto> RegisterAsync(string email, string password, string displayName, CancellationToken ct);

    /// <summary>Admin-created account with an explicit role; ignores the self-registration switch.</summary>
    Task<UserDto> CreateUserAsync(string email, string password, string displayName, UserRole role, CancellationToken ct);

    /// <summary>Verifies credentials (with lockout) and opens a new session.</summary>
    Task<AuthTokens> LoginAsync(string email, string password, ClientInfo client, CancellationToken ct);

    /// <summary>Rotates the refresh token. Presenting an already-rotated token revokes the whole session.</summary>
    Task<AuthTokens> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct);

    Task LogoutAsync(Guid userId, Guid sessionId, CancellationToken ct);

    /// <summary>Revokes every session of the user. Returns how many were open.</summary>
    Task<int> LogoutEverywhereAsync(Guid userId, CancellationToken ct);

    /// <summary>Changes the password and revokes every other session.</summary>
    Task ChangePasswordAsync(Guid userId, Guid currentSessionId, string currentPassword, string newPassword, CancellationToken ct);

    Task SetRoleAsync(Guid userId, UserRole role, CancellationToken ct);

    /// <summary>Deactivating also revokes all of the user's sessions.</summary>
    Task SetActiveAsync(Guid userId, bool isActive, CancellationToken ct);
}

public interface IUserDirectory
{
    Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<UserDto?> FindByEmailAsync(string email, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, UserDto>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<PagedResult<UserDto>> ListAsync(int page, int pageSize, string? search, CancellationToken ct);
    Task<int> CountActiveAdminsAsync(CancellationToken ct);
}

// ----- Auditing ---------------------------------------------------------------------------------

/// <summary>Persists audit entries outside the request's unit of work.</summary>
public interface IAuditLogWriter
{
    Task WriteAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct);
}
