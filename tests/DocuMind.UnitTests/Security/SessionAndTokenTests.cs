using DocuMind.Domain.Security;
using DocuMind.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DocuMind.UnitTests.Security;

public class RefreshTokenRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    private static string SecretHashOf(string token)
    {
        Assert.True(RefreshTokens.TryParse(token, out _, out var hash));
        return hash;
    }

    [Fact]
    public void Tokens_carry_the_session_id_and_only_a_hash_is_stored()
    {
        var (session, token) = UserSession.Open(Guid.NewGuid(), Lifetime, Now, "1.2.3.4", "ua");

        Assert.True(RefreshTokens.TryParse(token, out var sessionId, out var hash));
        Assert.Equal(session.Id, sessionId);
        Assert.Equal(session.TokenHash, hash);
        Assert.DoesNotContain(token.Split('.')[1], session.TokenHash);
    }

    [Fact]
    public void Each_refresh_issues_a_new_token_and_retires_the_old_one()
    {
        var (session, first) = UserSession.Open(Guid.NewGuid(), Lifetime, Now, null, null);

        var rotated = session.Rotate(SecretHashOf(first), Lifetime, Now.AddMinutes(10));

        Assert.Equal(RotationOutcome.Rotated, rotated.Outcome);
        Assert.NotEqual(first, rotated.NewRefreshToken);
        Assert.Equal(RotationOutcome.Rotated, session.Rotate(SecretHashOf(rotated.NewRefreshToken!), Lifetime, Now.AddMinutes(20)).Outcome);
    }

    [Fact]
    public void Replaying_a_used_token_revokes_the_session()
    {
        var (session, stolen) = UserSession.Open(Guid.NewGuid(), Lifetime, Now, null, null);
        var legit = session.Rotate(SecretHashOf(stolen), Lifetime, Now).NewRefreshToken!;

        var replay = session.Rotate(SecretHashOf(stolen), Lifetime, Now.AddMinutes(1));

        Assert.Equal(RotationOutcome.Reused, replay.Outcome);
        Assert.Equal("refresh_token_reused", session.RevokedReason);
        Assert.Equal(RotationOutcome.Invalid, session.Rotate(SecretHashOf(legit), Lifetime, Now.AddMinutes(2)).Outcome);
    }

    [Fact]
    public void Expired_and_revoked_sessions_cannot_be_refreshed()
    {
        var (expired, t1) = UserSession.Open(Guid.NewGuid(), TimeSpan.FromMinutes(5), Now, null, null);
        Assert.Equal(RotationOutcome.Invalid, expired.Rotate(SecretHashOf(t1), Lifetime, Now.AddMinutes(6)).Outcome);

        var (revoked, t2) = UserSession.Open(Guid.NewGuid(), Lifetime, Now, null, null);
        revoked.Revoke("logout", Now);
        Assert.Equal(RotationOutcome.Invalid, revoked.Rotate(SecretHashOf(t2), Lifetime, Now).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData("not-a-guid.secret")]
    [InlineData("0123456789abcdef0123456789abcdef.")]
    public void Malformed_tokens_are_rejected(string token)
    {
        Assert.False(RefreshTokens.TryParse(token, out _, out _));
    }
}

public class JwtTokenIssuerTests
{
    private static readonly AuthOptions Options = new() { SigningKey = new string('k', 48), AccessTokenMinutes = 15 };

    [Fact]
    public async Task Issued_tokens_validate_and_carry_identity_session_and_role()
    {
        var issuer = new JwtTokenIssuer(Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var (token, expires) = issuer.Issue(userId, "a@b.test", "Ada", UserRole.Manager, sessionId);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, JwtTokenIssuer.ValidationParameters(Options));

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(userId.ToString(), result.Claims[DocuMindClaims.UserId]);
        Assert.Equal(sessionId.ToString(), result.Claims[DocuMindClaims.SessionId]);
        Assert.Equal("Manager", result.Claims[DocuMindClaims.Role]);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Tokens_signed_with_another_key_are_rejected()
    {
        var issuer = new JwtTokenIssuer(Microsoft.Extensions.Options.Options.Create(new AuthOptions { SigningKey = new string('x', 48) }), TimeProvider.System);
        var (token, _) = issuer.Issue(Guid.NewGuid(), "a@b.test", "Ada", UserRole.Admin, Guid.NewGuid());

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, JwtTokenIssuer.ValidationParameters(Options));

        Assert.False(result.IsValid);
    }
}
