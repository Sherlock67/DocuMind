using System.Security.Claims;
using System.Text;
using DocuMind.Domain.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocuMind.Infrastructure.Identity;

/// <summary>Claim names used in access tokens. Inbound claim mapping is disabled, so these arrive as-is.</summary>
public static class DocuMindClaims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Name = "name";
    public const string Role = "role";
    public const string SessionId = JwtRegisteredClaimNames.Sid;
}

public sealed class JwtTokenIssuer(IOptions<AuthOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public static SymmetricSecurityKey KeyFrom(AuthOptions o) => new(Encoding.UTF8.GetBytes(o.SigningKey));

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, string email, string displayName, UserRole role, Guid sessionId)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(o.AccessTokenMinutes);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(KeyFrom(o), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(DocuMindClaims.UserId, userId.ToString()),
                new Claim(DocuMindClaims.Email, email),
                new Claim(DocuMindClaims.Name, displayName),
                new Claim(DocuMindClaims.Role, role.ToString()),
                new Claim(DocuMindClaims.SessionId, sessionId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
        });

        return (token, expires);
    }

    public static TokenValidationParameters ValidationParameters(AuthOptions o) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = o.Issuer,
        ValidateAudience = true,
        ValidAudience = o.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = KeyFrom(o),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = DocuMindClaims.Name,
        RoleClaimType = DocuMindClaims.Role,
    };
}
