using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace DocuMind.Application.Common.Auditing;

public static class AuditActions
{
    public const string UserRegistered = "user.registered";
    public const string LoginSucceeded = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string TokenRefreshed = "auth.token_refreshed";
    public const string RefreshTokenReused = "auth.refresh_token_reused";
    public const string LoggedOut = "auth.logout";
    public const string LoggedOutEverywhere = "auth.logout_all";
    public const string PasswordChanged = "auth.password_changed";
    public const string UserRoleChanged = "user.role_changed";
    public const string UserActivated = "user.activated";
    public const string UserDeactivated = "user.deactivated";

    public const string AccessDenied = "access.denied";

    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectDeleted = "project.deleted";
    public const string MemberAdded = "project.member_added";
    public const string MemberRoleChanged = "project.member_role_changed";
    public const string MemberRemoved = "project.member_removed";

    public const string FolderDeleted = "folder.deleted";

    public const string CollectionCreated = "collection.created";
    public const string CollectionDeleted = "collection.deleted";
    public const string CollectionShared = "collection.shared";
    public const string CollectionUnshared = "collection.unshared";

    public const string DocumentUploaded = "document.uploaded";
    public const string DocumentDeleted = "document.deleted";
    public const string DocumentRenamed = "document.renamed";
    public const string DocumentMoved = "document.moved";
    public const string DocumentReprocessed = "document.reprocessed";
    public const string DocumentDownloaded = "document.downloaded";
    public const string DocumentViewed = "document.viewed";
    public const string DocumentPreviewed = "document.previewed";
    public const string DocumentProcessed = "document.processed";
    public const string DocumentProcessingFailed = "document.processing_failed";

    /// <summary>Documents whose content was returned by a search or used to ground a chat answer.</summary>
    public const string DocumentsRetrieved = "documents.retrieved";
}

/// <summary>
/// Collects audit entries during a request. Handlers record an event only after it has actually
/// happened (after the commit), plus failed attempts such as denied access or a bad password.
/// <see cref="AuditBehavior{TRequest,TResponse}"/> persists them when the request ends, whether it
/// succeeded or not.
/// </summary>
public interface IAuditTrail
{
    void Record(
        string action,
        string? resourceType = null,
        Guid? resourceId = null,
        Guid? projectId = null,
        IReadOnlyDictionary<string, string>? details = null,
        bool succeeded = true,
        Guid? actorId = null,
        string? actorEmail = null);
}

internal sealed class AuditTrail(ICurrentUser user, TimeProvider clock) : IAuditTrail
{
    private readonly List<AuditEntry> _pending = [];

    public void Record(
        string action,
        string? resourceType = null,
        Guid? resourceId = null,
        Guid? projectId = null,
        IReadOnlyDictionary<string, string>? details = null,
        bool succeeded = true,
        Guid? actorId = null,
        string? actorEmail = null)
    {
        var authenticated = user.IsAuthenticated;
        _pending.Add(AuditEntry.Create(
            clock.GetUtcNow(),
            actorId ?? (authenticated ? user.Id : null),
            actorEmail ?? (authenticated ? user.Email : user.IsSystem ? "system" : null),
            action,
            succeeded,
            resourceType,
            resourceId,
            projectId,
            details,
            user.IpAddress,
            user.UserAgent));
    }

    public IReadOnlyList<AuditEntry> TakePending()
    {
        if (_pending.Count == 0) return [];
        var entries = _pending.ToList();
        _pending.Clear();
        return entries;
    }
}

/// <summary>Writes whatever the request recorded, including on failure (denied access, bad login).</summary>
internal sealed class AuditBehavior<TRequest, TResponse>(
    AuditTrail trail,
    IAuditLogWriter writer,
    ILogger<AuditBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        finally
        {
            var entries = trail.TakePending();
            if (entries.Count > 0)
            {
                try
                {
                    // Not the request's token: a client disconnect must not lose the record of what it did.
                    await writer.WriteAsync(entries, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Failed to write {Count} audit entries for {Request}: {Actions}",
                        entries.Count, typeof(TRequest).Name, string.Join(", ", entries.Select(e => e.Action)));
                }
            }
        }
    }
}
