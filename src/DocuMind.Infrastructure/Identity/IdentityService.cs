using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Models;
using DocuMind.Domain.Security;
using DocuMind.Infrastructure.Persistence;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.Identity;

internal sealed class IdentityService(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    JwtTokenIssuer tokens,
    SessionValidator sessionCache,
    IOptions<AuthOptions> options,
    TimeProvider clock) : IIdentityService
{
    private const string InvalidCredentials = "Invalid email or password.";

    // Verified against when the email is unknown, so both paths cost one hash computation
    // and response timing can't be used to enumerate accounts.
    private static readonly string DummyHash = new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "not-a-real-password-0");

    public async Task<UserDto> RegisterAsync(string email, string password, string displayName, CancellationToken ct)
    {
        var isFirstUser = !await db.Users.AnyAsync(ct);
        if (!isFirstUser && !options.Value.AllowSelfRegistration)
            throw new ForbiddenException("Self-registration is disabled. Ask an administrator for an account.");

        // Bootstrap: the very first account administers the system.
        return await CreateAsync(email, password, displayName, isFirstUser ? UserRole.Admin : UserRole.User);
    }

    public Task<UserDto> CreateUserAsync(string email, string password, string displayName, UserRole role, CancellationToken ct) =>
        CreateAsync(email, password, displayName, role);

    private async Task<UserDto> CreateAsync(string email, string password, string displayName, UserRole role)
    {
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = displayName,
            Role = role,
            IsActive = true,
            CreatedAt = clock.GetUtcNow(),
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            throw ToValidationException(result);

        return ToDto(user);
    }

    public async Task<AuthTokens> LoginAsync(string email, string password, ClientInfo client, CancellationToken ct)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            new PasswordHasher<ApplicationUser>().VerifyHashedPassword(new ApplicationUser(), DummyHash, password);
            throw new AuthenticationFailedException(InvalidCredentials, "unknown_email");
        }

        if (await userManager.IsLockedOutAsync(user))
            throw new AuthenticationFailedException("Too many failed attempts. Try again later.", "locked_out", user.Id);

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            var nowLocked = await userManager.IsLockedOutAsync(user);
            throw new AuthenticationFailedException(InvalidCredentials, nowLocked ? "bad_password_locked_out" : "bad_password", user.Id);
        }

        // Checked after the password so an attacker can't learn which accounts are deactivated.
        if (!user.IsActive)
            throw new AuthenticationFailedException(InvalidCredentials, "inactive", user.Id);

        await userManager.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = clock.GetUtcNow();
        await userManager.UpdateAsync(user);

        var (session, refreshToken) = UserSession.Open(user.Id, RefreshLifetime, clock.GetUtcNow(), client.IpAddress, client.UserAgent);
        db.Set<UserSession>().Add(session);
        await db.SaveChangesAsync(ct);

        return Issue(user, session, refreshToken);
    }

    public async Task<AuthTokens> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct)
    {
        if (!RefreshTokens.TryParse(refreshToken, out var sessionId, out var secretHash))
            throw new AuthenticationFailedException("Invalid refresh token.", "malformed_refresh_token");

        var session = await db.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new AuthenticationFailedException("Invalid refresh token.", "unknown_session");

        var result = session.Rotate(secretHash, RefreshLifetime, clock.GetUtcNow());
        if (result.Outcome == RotationOutcome.Reused)
        {
            await db.SaveChangesAsync(ct);
            sessionCache.Invalidate(session.Id);
            throw new AuthenticationFailedException("Invalid refresh token.", "refresh_token_reused", session.UserId);
        }

        if (result.Outcome == RotationOutcome.Invalid)
            throw new AuthenticationFailedException("Invalid refresh token.", "invalid_refresh_token", session.UserId);

        var user = await userManager.FindByIdAsync(session.UserId.ToString());
        if (user is null || !user.IsActive)
        {
            session.Revoke("user_inactive", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            throw new AuthenticationFailedException("Invalid refresh token.", "inactive", session.UserId);
        }

        await db.SaveChangesAsync(ct);
        return Issue(user, session, result.NewRefreshToken!);
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await db.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (session is null) return;

        session.Revoke("logout", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        sessionCache.Invalidate(session.Id);
    }

    public Task<int> LogoutEverywhereAsync(Guid userId, CancellationToken ct) => RevokeAllAsync(userId, "logout_all", exceptSessionId: null, ct);

    public async Task ChangePasswordAsync(Guid userId, Guid currentSessionId, string currentPassword, string newPassword, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId.ToString()) ?? throw new AuthenticationFailedException();

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
            throw ToValidationException(result);

        // Anyone holding an old session (e.g. on a stolen laptop) is signed out.
        await RevokeAllAsync(userId, "password_changed", currentSessionId, ct);
    }

    public async Task SetRoleAsync(Guid userId, UserRole role, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        user.Role = role;
        await userManager.UpdateAsync(user);
        await InvalidateCachedSessionsAsync(userId, ct); // new role applies within seconds, not at next login
    }

    public async Task SetActiveAsync(Guid userId, bool isActive, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        user.IsActive = isActive;
        await userManager.UpdateAsync(user);

        if (!isActive)
            await RevokeAllAsync(userId, "deactivated", exceptSessionId: null, ct);
    }

    private TimeSpan RefreshLifetime => TimeSpan.FromDays(options.Value.RefreshTokenDays);

    private AuthTokens Issue(ApplicationUser user, UserSession session, string refreshToken)
    {
        var (accessToken, expiresAt) = tokens.Issue(user.Id, user.Email!, user.DisplayName, user.Role, session.Id);
        return new AuthTokens(accessToken, expiresAt, refreshToken, session.ExpiresAt, ToDto(user));
    }

    private async Task<int> RevokeAllAsync(Guid userId, string reason, Guid? exceptSessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sessions = await db.Set<UserSession>()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now && s.Id != exceptSessionId)
            .ToListAsync(ct);

        foreach (var session in sessions)
        {
            session.Revoke(reason, now);
            sessionCache.Invalidate(session.Id);
        }

        await db.SaveChangesAsync(ct);
        return sessions.Count;
    }

    private async Task InvalidateCachedSessionsAsync(Guid userId, CancellationToken ct)
    {
        var ids = await db.Set<UserSession>().Where(s => s.UserId == userId && s.RevokedAt == null).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in ids)
            sessionCache.Invalidate(id);
    }

    internal static UserDto ToDto(ApplicationUser u) =>
        new(u.Id, u.Email!, u.DisplayName, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt);

    private static ValidationException ToValidationException(IdentityResult result) =>
        new(result.Errors.Select(e => new ValidationFailure(
            e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "Password"
            : e.Code.Contains("Email", StringComparison.OrdinalIgnoreCase) || e.Code.Contains("UserName", StringComparison.OrdinalIgnoreCase) ? "Email"
            : e.Code,
            e.Code is "DuplicateUserName" or "DuplicateEmail" ? "That email is already registered." : e.Description)));
}

internal sealed class UserDirectory(AppDbContext db) : IUserDirectory
{
    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == id).Select(Projection).FirstOrDefaultAsync(ct);

    public async Task<UserDto?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return await db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(Projection).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, UserDto>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(Projection).ToDictionaryAsync(u => u.Id, ct);

    public async Task<PagedResult<UserDto>> ListAsync(int page, int pageSize, string? search, CancellationToken ct)
    {
        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToUpperInvariant();
            users = users.Where(u => u.NormalizedEmail!.Contains(s) || u.DisplayName.ToUpper().Contains(s));
        }

        var total = await users.CountAsync(ct);
        var items = await users.OrderBy(u => u.Email).Skip((page - 1) * pageSize).Take(pageSize).Select(Projection).ToListAsync(ct);
        return new PagedResult<UserDto>(items, page, pageSize, total);
    }

    public Task<int> CountActiveAdminsAsync(CancellationToken ct) =>
        db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct);

    private static readonly System.Linq.Expressions.Expression<Func<ApplicationUser, UserDto>> Projection =
        u => new UserDto(u.Id, u.Email!, u.DisplayName, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt);
}
