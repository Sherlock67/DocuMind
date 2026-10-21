using DocuMind.Domain.Common;

namespace DocuMind.Domain.Auditing;

/// <summary>
/// Append-only record of a security-relevant event. Never updated or deleted by the application.
/// Actor fields are denormalised so the record stays meaningful if the user is later renamed or deactivated.
/// </summary>
public sealed class AuditEntry : Entity
{
    private AuditEntry() { } // EF Core

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Null for system actions (e.g. background ingestion) and anonymous attempts (e.g. failed login).</summary>
    public Guid? ActorId { get; private set; }

    public string? ActorEmail { get; private set; }

    /// <summary>One of <c>AuditActions</c>, e.g. "document.uploaded".</summary>
    public string Action { get; private set; } = null!;

    public bool Succeeded { get; private set; }

    public string? ResourceType { get; private set; }
    public Guid? ResourceId { get; private set; }

    /// <summary>Project the event belongs to, so project Owners can read their project's trail.</summary>
    public Guid? ProjectId { get; private set; }

    /// <summary>Small, non-secret context (file name, new role, retrieved document ids, ...).</summary>
    public Dictionary<string, string> Details { get; private set; } = [];

    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public static AuditEntry Create(
        DateTimeOffset occurredAt,
        Guid? actorId,
        string? actorEmail,
        string action,
        bool succeeded,
        string? resourceType,
        Guid? resourceId,
        Guid? projectId,
        IReadOnlyDictionary<string, string>? details,
        string? ipAddress,
        string? userAgent) => new()
    {
        OccurredAt = occurredAt,
        ActorId = actorId,
        ActorEmail = actorEmail,
        Action = action,
        Succeeded = succeeded,
        ResourceType = resourceType,
        ResourceId = resourceId,
        ProjectId = projectId,
        Details = details is null ? [] : new Dictionary<string, string>(details),
        IpAddress = Truncate(ipAddress, 64),
        UserAgent = Truncate(userAgent, 512),
    };

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}
