using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DocuMind.Web.Api;
using DocuMind.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;

namespace DocuMind.Web.Auth;

/// <summary>The API's tokens for one signed-in user, kept inside this app's encrypted auth cookie.</summary>
public sealed record ApiTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public static class TokenNames
{
    public const string Access = "api_access_token";
    public const string AccessExpires = "api_access_expires";
    public const string Refresh = "api_refresh_token";
    public const string RefreshExpires = "api_refresh_expires";
}

public static class DocuMindClaims
{
    public const string UserId = ClaimTypes.NameIdentifier;
}

/// <summary>
/// Talks to the API's /api/auth endpoints on behalf of the browser user.
/// The API hands its refresh token out only as an HttpOnly cookie (it never appears in JSON), so
/// this server-side client reads it from Set-Cookie and keeps it in our own cookie's encrypted
/// authentication properties. The browser never sees either API token.
/// </summary>
public sealed class ApiSession(IHttpClientFactory httpFactory, JsonSerializerOptions json, IMemoryCache cache, ILogger<ApiSession> logger)
{
    public const string ApiRefreshCookie = "documind_refresh";

    // One refresh at a time per session, and the result remembered briefly: two requests from the same
    // browser that both notice an expiring token must not both rotate it, or the second would present an
    // already-used refresh token, which the API treats as theft and answers by revoking the session.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly TimeSpan ReuseResultFor = TimeSpan.FromSeconds(60);

    public async Task<(AuthResponse Auth, ApiTokens Tokens)> LoginAsync(string email, string password, HttpContext caller)
    {
        using var request = ApiRequest(HttpMethod.Post, "api/auth/login", caller);
        request.Content = JsonContent.Create(new { email, password }, options: json);
        using var response = await Client.SendAsync(request);
        return await ReadAsync(response);
    }

    public async Task<UserDto> RegisterAsync(string email, string password, string displayName, HttpContext caller)
    {
        using var request = ApiRequest(HttpMethod.Post, "api/auth/register", caller);
        request.Content = JsonContent.Create(new { email, password, displayName }, options: json);
        using var response = await Client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw await ApiException.FromResponseAsync(response);
        return (await response.Content.ReadFromJsonAsync<UserDto>(json))!;
    }

    /// <summary>Exchanges a refresh token for new tokens; null when the API session is gone.</summary>
    public async Task<(AuthResponse Auth, ApiTokens Tokens)?> RefreshAsync(string refreshToken, HttpContext caller)
    {
        var cacheKey = "refresh:" + refreshToken;
        if (cache.TryGetValue(cacheKey, out (AuthResponse, ApiTokens) earlier))
            return earlier;

        var gate = Gates.GetOrAdd(refreshToken, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (cache.TryGetValue(cacheKey, out earlier))
                return earlier;

            using var request = ApiRequest(HttpMethod.Post, "api/auth/refresh", caller);
            request.Headers.Add(HeaderNames.Cookie, $"{ApiRefreshCookie}={refreshToken}");
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("API refresh rejected ({Status}); signing the user out", (int)response.StatusCode);
                return null;
            }

            var result = await ReadAsync(response);
            cache.Set(cacheKey, result, ReuseResultFor);
            return result;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "API unreachable during token refresh");
            throw;
        }
        finally
        {
            gate.Release();
            Gates.TryRemove(refreshToken, out _);
        }
    }

    /// <summary>Revokes the API session (or all of the user's sessions). Best effort: local sign-out happens regardless.</summary>
    public async Task LogoutAsync(string accessToken, bool everywhere, HttpContext caller)
    {
        try
        {
            using var request = ApiRequest(HttpMethod.Post, everywhere ? "api/auth/logout-all" : "api/auth/logout", caller);
            request.Headers.Authorization = new("Bearer", accessToken);
            using var _ = await Client.SendAsync(request);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not revoke the API session during logout");
        }
    }

    /// <summary>Builds this app's cookie identity from the API's view of the user.</summary>
    public static ClaimsPrincipal ToPrincipal(UserDto user) => new(new ClaimsIdentity(
    [
        new Claim(DocuMindClaims.UserId, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.DisplayName),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    public static void Store(AuthenticationProperties properties, ApiTokens tokens)
    {
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = TokenNames.Access, Value = tokens.AccessToken },
            new AuthenticationToken { Name = TokenNames.AccessExpires, Value = tokens.AccessTokenExpiresAt.ToString("o") },
            new AuthenticationToken { Name = TokenNames.Refresh, Value = tokens.RefreshToken },
            new AuthenticationToken { Name = TokenNames.RefreshExpires, Value = tokens.RefreshTokenExpiresAt.ToString("o") },
        ]);
        properties.ExpiresUtc = tokens.RefreshTokenExpiresAt;
    }

    /// <summary>Copies the user's client details so the API rate-limits and audits the real user, not this server.</summary>
    public static void Forward(HttpRequestMessage request, HttpContext caller)
    {
        if (caller.Connection.RemoteIpAddress is { } ip)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip.ToString());
        if (caller.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua)
            request.Headers.TryAddWithoutValidation(HeaderNames.UserAgent, ua);
    }

    private HttpClient Client => httpFactory.CreateClient("documind-auth");

    private static HttpRequestMessage ApiRequest(HttpMethod method, string path, HttpContext caller)
    {
        var request = new HttpRequestMessage(method, path);
        Forward(request, caller);
        return request;
    }

    private async Task<(AuthResponse, ApiTokens)> ReadAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw await ApiException.FromResponseAsync(response);

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(json))!;
        var refresh = ReadRefreshCookie(response)
            ?? throw new InvalidOperationException("The API did not return a refresh-token cookie.");
        return (auth, new ApiTokens(auth.AccessToken, auth.AccessTokenExpiresAt, refresh.Value, refresh.Expires));
    }

    public static (string Value, DateTimeOffset Expires)? ReadRefreshCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(HeaderNames.SetCookie, out var headers)) return null;
        if (!SetCookieHeaderValue.TryParseList(headers.ToList(), out var cookies)) return null;

        var cookie = cookies.FirstOrDefault(c => c.Name == ApiRefreshCookie && !string.IsNullOrEmpty(c.Value.Value));
        return cookie is null ? null : (cookie.Value.Value!, cookie.Expires ?? DateTimeOffset.UtcNow.AddDays(14));
    }
}

/// <summary>
/// Runs on every request with our auth cookie: refreshes the API access token shortly before it
/// expires and signs the user out when the API session is gone (logout elsewhere, deactivation,
/// password change, refresh-token theft detection).
/// </summary>
public sealed class ApiCookieEvents(ApiSession session) : CookieAuthenticationEvents
{
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(2);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var props = context.Properties;
        var refresh = props.GetTokenValue(TokenNames.Refresh);
        if (string.IsNullOrEmpty(refresh) ||
            !DateTimeOffset.TryParse(props.GetTokenValue(TokenNames.AccessExpires), out var expires))
        {
            await RejectAsync(context);
            return;
        }

        if (DateTimeOffset.UtcNow < expires - RefreshAhead)
            return;

        var result = await session.RefreshAsync(refresh, context.HttpContext);
        if (result is not { } r)
        {
            await RejectAsync(context);
            return;
        }

        ApiSession.Store(props, r.Tokens);
        context.ReplacePrincipal(ApiSession.ToPrincipal(r.Auth.User)); // role changes made by an Admin show up here
        context.ShouldRenew = true;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
