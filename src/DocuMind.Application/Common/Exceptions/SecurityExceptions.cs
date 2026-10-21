namespace DocuMind.Application.Common.Exceptions;

/// <summary>
/// Missing, invalid or expired credentials (HTTP 401). <see cref="Exception.Message"/> is safe to show;
/// <see cref="AuditReason"/> is the precise cause, recorded in the audit log but never returned,
/// so responses can't be used to discover which accounts exist or are locked.
/// </summary>
public sealed class AuthenticationFailedException(
    string message = "Authentication is required.",
    string? auditReason = null,
    Guid? userId = null) : Exception(message)
{
    public string? AuditReason { get; } = auditReason;

    /// <summary>The account involved, when known (e.g. wrong password for an existing user).</summary>
    public Guid? UserId { get; } = userId;
}

/// <summary>Authenticated, but not allowed to do this (HTTP 403).</summary>
public sealed class ForbiddenException(string message = "You do not have permission to perform this action.") : Exception(message);
