using DocuMind.Application.Abstractions.Security;
using static DocuMind.Api.Endpoints.SecurityEndpoints;

namespace DocuMind.Api.Infrastructure;

/// <summary>
/// Refresh tokens travel only in this cookie:
/// <list type="bullet">
/// <item><b>HttpOnly</b> — unreadable by page scripts, so an XSS bug cannot steal the long-lived credential.</item>
/// <item><b>SameSite=Strict</b> — never sent on cross-site requests, which rules out CSRF on /refresh.</item>
/// <item><b>Path=/api/auth</b> — not sent with any other request.</item>
/// <item><b>Secure</b> whenever the request is HTTPS (browsers also accept it on http://localhost for development).</item>
/// </list>
/// </summary>
internal static class RefreshCookie
{
    public const string Name = "documind_refresh";
    private const string CookiePath = "/api/auth";

    public static string? Read(HttpContext http) =>
        http.Request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public static AuthResponse Issue(HttpContext http, AuthTokens tokens)
    {
        http.Response.Cookies.Append(Name, tokens.RefreshToken, Options(http, tokens.RefreshTokenExpiresAt));
        http.Response.Headers.CacheControl = "no-store";
        return new AuthResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.User);
    }

    public static void Clear(HttpContext http) => http.Response.Cookies.Delete(Name, Options(http, expires: null));

    private static CookieOptions Options(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
        IsEssential = true,
    };
}
