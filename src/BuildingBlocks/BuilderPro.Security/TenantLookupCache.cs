using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuilderPro.Security;

/// <summary>
/// Cache keys for tenant lookups, shared by the services that READ the cache and by Identity,
/// which DELETES entries when a membership changes. One definition, so they can't drift.
///
/// Key = user + tenant hint. Only successful lookups are cached, and a membership (user U,
/// tenant T) can only have produced the answers for hint "" (none) and hint T, so changing it
/// invalidates exactly those two keys.
/// </summary>
public static class TenantCacheKeys
{
    public static string For(string userKey, string? tenantHint) => $"bp:tenant:{userKey}|{tenantHint}";

    public static IEnumerable<string> AffectedBy(string userKey, string tenantId) => [For(userKey, null), For(userKey, tenantId)];
}

/// <summary>
/// Cache-aside in front of <see cref="IdentityTenantLookup"/> (LEARN 2f).
///  - Hit: no call to Identity.
///  - Miss: ask Identity; cache SUCCESSFUL answers for a short TTL. Refusals are never cached,
///    so a user who just got a membership doesn't wait for a TTL.
///  - Redis error: log and ask Identity. The cache makes things faster, never more correct.
/// Revocation stays immediate because Identity deletes the affected keys (<see cref="ITenantCacheInvalidator"/>);
/// the TTL only bounds a rare race (a lookup in flight while the revoke happens).
/// </summary>
public sealed class CachingTenantLookup(
    ITenantLookup inner, IDistributedCache cache, AuthOptions auth, TimeSpan ttl, ILogger<CachingTenantLookup> log) : ITenantLookup
{
    private sealed record Entry(string TenantId, string? CustomerId, string[] Scopes);

    public async Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct)
    {
        if (http.User.Identity?.IsAuthenticated != true) return await inner.ResolveAsync(http, ct);

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var key = TenantCacheKeys.For(user.UserKey, TenantHint.From(http, user));   // picker choice is part of the key

        try
        {
            var bytes = await cache.GetAsync(key, ct);
            if (bytes is not null && JsonSerializer.Deserialize<Entry>(bytes) is { } hit)
            {
                return TenantLookup.Ok(hit.TenantId, hit.Scopes, hit.CustomerId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Tenant cache read failed; asking Identity");
        }

        var result = await inner.ResolveAsync(http, ct);
        if (!result.Resolved) return result;

        try
        {
            var entry = new Entry(result.TenantId!, result.CustomerId, [.. result.Scopes]);
            await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(entry),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Tenant cache write failed");
        }
        return result;
    }
}

/// <summary>Identity calls this whenever a membership is created, changed or revoked.</summary>
public interface ITenantCacheInvalidator
{
    Task InvalidateAsync(string userKey, string tenantId, CancellationToken ct);
}

public sealed class DistributedTenantCacheInvalidator(IDistributedCache cache, ILogger<DistributedTenantCacheInvalidator> log) : ITenantCacheInvalidator
{
    public async Task InvalidateAsync(string userKey, string tenantId, CancellationToken ct)
    {
        foreach (var key in TenantCacheKeys.AffectedBy(userKey, tenantId))
        {
            try
            {
                await cache.RemoveAsync(key, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Can't reach Redis: the entry (if any) still expires with its TTL. Loud, because
                // until then a revoked user may keep access.
                log.LogError(ex, "Could not invalidate tenant cache key {Key}; it expires with its TTL", key);
            }
        }
    }
}

/// <summary>No cache configured, nothing to invalidate.</summary>
public sealed class NoTenantCache : ITenantCacheInvalidator
{
    public Task InvalidateAsync(string userKey, string tenantId, CancellationToken ct) => Task.CompletedTask;
}

public static class TenantCacheSetup
{
    /// <summary>True when the AppHost (or config) provides a Redis connection named "cache".</summary>
    public static bool HasCache(IConfiguration config) => !string.IsNullOrWhiteSpace(config.GetConnectionString("cache"));

    /// <summary>For Identity: registers the invalidator matching whether a cache exists.</summary>
    public static IServiceCollection AddTenantCacheInvalidation(this IServiceCollection services, IConfiguration config)
    {
        if (HasCache(config)) services.AddSingleton<ITenantCacheInvalidator, DistributedTenantCacheInvalidator>();
        else services.AddSingleton<ITenantCacheInvalidator, NoTenantCache>();
        return services;
    }
}
