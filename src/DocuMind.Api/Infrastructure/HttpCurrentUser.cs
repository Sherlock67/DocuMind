using System.Security.Claims;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Domain.Security;
using DocuMind.Infrastructure.Identity;

namespace DocuMind.Api.Infrastructure;

/// <summary>
/// <see cref="ICurrentUser"/> backed by the validated JWT of the current HTTP request.
/// Outside a request (the background ingestion worker) there is no HttpContext, which is what
/// identifies the caller as the system itself.
/// </summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private HttpContext? Context => accessor.HttpContext;
    private ClaimsPrincipal? Principal => Context?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public bool IsSystem => Context is null;

    public Guid Id => IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(DocuMindClaims.UserId), out var id)
        ? id
        : throw new AuthenticationFailedException();

    public string? Email => Principal?.FindFirstValue(DocuMindClaims.Email);

    public UserRole Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(DocuMindClaims.Role), out var role) ? role : UserRole.User;

    public Guid? SessionId => Guid.TryParse(Principal?.FindFirstValue(DocuMindClaims.SessionId), out var sid) ? sid : null;

    public string? IpAddress => Context?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Context?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
}
