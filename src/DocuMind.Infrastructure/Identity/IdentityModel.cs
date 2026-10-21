using System.Security.Cryptography;
using System.Text;
using DocuMind.Domain.Security;
using Microsoft.AspNetCore.Identity;

namespace DocuMind.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = null!;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "documind";
    public string Audience { get; set; } = "documind-api";

    /// <summary>
    /// HMAC-SHA256 key, at least 32 bytes. Supply it through user-secrets or an environment variable
    /// (Auth__SigningKey), never appsettings. Development generates a throwaway key when it is empty.
    /// </summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>When false, only Admins can create accounts (the very first account can always register).</summary>
    public bool AllowSelfRegistration { get; set; } = true;

    /// <summary>How long a session/role check is cached before the database is consulted again.</summary>
    public int SessionCacheSeconds { get; set; } = 30;
}

/// <summary>
/// A login session, backing one refresh token. The refresh token rotates on every use; the
/// previous hash is kept so that replaying an already-used token is detected as theft and the
/// whole session is revoked.
/// </summary>
public sealed class UserSession
{
    private UserSession() { } // EF Core

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public string? PreviousTokenHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? LastRefreshedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public static (UserSession Session, string RefreshToken) Open(Guid userId, TimeSpan lifetime, DateTimeOffset now, string? ip, string? userAgent)
    {
        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            IpAddress = ip?.Length > 64 ? ip[..64] : ip,
            UserAgent = userAgent?.Length > 512 ? userAgent[..512] : userAgent,
        };
        var (token, hash) = RefreshTokens.Create(session.Id);
        session.TokenHash = hash;
        return (session, token);
    }

    public RotationResult Rotate(string presentedSecretHash, TimeSpan lifetime, DateTimeOffset now)
    {
        if (!IsActive(now))
            return new RotationResult(RotationOutcome.Invalid, null);

        if (RefreshTokens.HashesEqual(presentedSecretHash, TokenHash))
        {
            var (token, hash) = RefreshTokens.Create(Id);
            PreviousTokenHash = TokenHash;
            TokenHash = hash;
            LastRefreshedAt = now;
            ExpiresAt = now + lifetime; // sliding: an active session stays signed in
            return new RotationResult(RotationOutcome.Rotated, token);
        }

        if (PreviousTokenHash is not null && RefreshTokens.HashesEqual(presentedSecretHash, PreviousTokenHash))
        {
            Revoke("refresh_token_reused", now);
            return new RotationResult(RotationOutcome.Reused, null);
        }

        return new RotationResult(RotationOutcome.Invalid, null);
    }

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = now;
        RevokedReason = reason;
    }
}

public enum RotationOutcome
{
    Rotated,
    Invalid,
    Reused,
}

public sealed record RotationResult(RotationOutcome Outcome, string? NewRefreshToken);

/// <summary>
/// Refresh token format: <c>{sessionId:N}.{32 random bytes, base64url}</c>. The session id lets the
/// server find the row directly; only a SHA-256 of the secret part is stored, so a database leak
/// does not leak usable tokens.
/// </summary>
public static class RefreshTokens
{
    public static (string Token, string Hash) Create(Guid sessionId)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        return ($"{sessionId:N}.{secret}", Hash(secret));
    }

    public static bool TryParse(string token, out Guid sessionId, out string secretHash)
    {
        sessionId = Guid.Empty;
        secretHash = "";
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1 || !Guid.TryParseExact(token[..dot], "N", out sessionId))
            return false;

        secretHash = Hash(token[(dot + 1)..]);
        return true;
    }

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool HashesEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
