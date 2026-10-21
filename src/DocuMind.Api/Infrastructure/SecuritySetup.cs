using System.Security.Claims;
using System.Threading.RateLimiting;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace DocuMind.Api.Infrastructure;

internal static class SecuritySetup
{
    public const string AuthRateLimit = "auth";
    public const string AdminPolicy = "Admin";

    public static IServiceCollection AddApiSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Bearer options come from the same AuthOptions the token issuer uses, so the two can't disagree.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((bearer, auth) =>
            {
                bearer.MapInboundClaims = false; // keep "sub", "role", "sid" exactly as issued
                bearer.TokenValidationParameters = JwtTokenIssuer.ValidationParameters(auth.Value);
                bearer.Events = new JwtBearerEvents { OnTokenValidated = ValidateSessionAsync };
            });

        // Secure by default: every endpoint requires a signed-in user unless it opts out with AllowAnonymous.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(nameof(Domain.Security.UserRole.Admin)));

        // Slow down credential stuffing and refresh-token guessing: per client IP.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        return services;
    }

    /// <summary>
    /// A signature check alone would honour a token until it expires. This also requires its session
    /// to be open and its user active, and refreshes the role claim so role changes apply immediately.
    /// </summary>
    private static async Task ValidateSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!Guid.TryParse(principal.FindFirstValue(DocuMindClaims.SessionId), out var sessionId) ||
            !Guid.TryParse(principal.FindFirstValue(DocuMindClaims.UserId), out var userId))
        {
            context.Fail("Token is missing session claims.");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<SessionValidator>();
        var state = await validator.GetActiveAsync(sessionId, userId, context.HttpContext.RequestAborted);
        if (state is null)
        {
            context.Fail("Session has ended.");
            return;
        }

        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var stale in identity.FindAll(DocuMindClaims.Role).ToList())
            identity.RemoveClaim(stale);
        identity.AddClaim(new Claim(DocuMindClaims.Role, state.CurrentRole.ToString()));
    }
}
