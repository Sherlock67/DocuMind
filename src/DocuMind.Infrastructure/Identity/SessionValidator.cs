using DocuMind.Domain.Security;
using DocuMind.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.Identity;

/// <summary>Live state behind an access token: the session is still open and the user still active.</summary>
public sealed record SessionState(UserRole CurrentRole);

/// <summary>
/// Checked on every authenticated request, so logout, "sign out everywhere", password changes and
/// deactivation take effect immediately instead of when the 15-minute access token expires. Results
/// are cached briefly; revocations made by this instance evict the cache entry at once.
/// Across several instances a revocation is honoured within <see cref="AuthOptions.SessionCacheSeconds"/>.
/// </summary>
public sealed class SessionValidator(IServiceScopeFactory scopes, IMemoryCache cache, IOptions<AuthOptions> options, TimeProvider clock)
{
    private static string Key(Guid sessionId) => $"session:{sessionId:N}";

    public async Task<SessionState?> GetActiveAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(sessionId), out SessionState? cached))
            return cached;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();

        var role = await (
                from s in db.Set<UserSession>().AsNoTracking()
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                where s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now && u.IsActive
                select (UserRole?)u.Role)
            .FirstOrDefaultAsync(ct);

        var state = role is { } r ? new SessionState(r) : null;
        cache.Set(Key(sessionId), state, TimeSpan.FromSeconds(Math.Max(1, options.Value.SessionCacheSeconds)));
        return state;
    }

    public void Invalidate(Guid sessionId) => cache.Remove(Key(sessionId));
}
