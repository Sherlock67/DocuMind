using System.Collections.Concurrent;
using System.Reflection;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Domain.Security;

namespace DocuMind.Application.Common.Security;

/// <summary>Marks a request that anonymous callers may send (register, login, refresh).</summary>
public interface IPublicRequest;

/// <summary>Marks a request that only the application itself may send (background ingestion).</summary>
public interface ISystemRequest;

/// <summary>Minimum system-wide role needed to send the request at all. Resource checks still apply.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequireRoleAttribute(UserRole role) : Attribute
{
    public UserRole Role { get; } = role;
}

/// <summary>
/// Secure by default: every request needs an authenticated user unless it is explicitly marked
/// <see cref="IPublicRequest"/> or <see cref="ISystemRequest"/>. Role requirements are declared on
/// the request type with <see cref="RequireRoleAttribute"/>. Per-resource checks (project roles,
/// collection shares) happen in handlers through <see cref="IAccessControl"/>.
/// </summary>
internal sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser user, IAuditTrail audit)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly ConcurrentDictionary<Type, UserRole?> RequiredRoles = new();

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is IPublicRequest)
            return next();

        if (request is ISystemRequest)
        {
            if (!user.IsSystem)
                throw new ForbiddenException("This operation is internal.");
            return next();
        }

        if (!user.IsAuthenticated)
            throw new AuthenticationFailedException();

        var required = RequiredRoles.GetOrAdd(typeof(TRequest), t => t.GetCustomAttribute<RequireRoleAttribute>()?.Role);
        if (required is { } role && user.Role < role)
        {
            audit.Record(AuditActions.AccessDenied, succeeded: false,
                details: new Dictionary<string, string> { ["request"] = typeof(TRequest).Name, ["requiredRole"] = role.ToString() });
            throw new ForbiddenException($"This action requires the {role} role.");
        }

        return next();
    }
}
