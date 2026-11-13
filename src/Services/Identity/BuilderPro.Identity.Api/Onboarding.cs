using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BuilderPro.Identity.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Identity.Api;

/// <summary>A retailer's storefront as far as sign-up is concerned (Identity:Stores in config).</summary>
public sealed class StoreOptions
{
    public string Id { get; set; } = "";           // the public slug in the sign-up link: /signup?store=acme
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool SignupEnabled { get; set; }
}

public sealed record JoinStoreRequest(string? Store);

public sealed record JoinStoreResponse(string Status, string StoreName, string TenantId, string CustomerId);

/// <summary>
/// Self-service onboarding through a store's sign-up link (decision 1e).
///
/// The link is PUBLIC, so the store id isn't a secret and nothing here relies on it being one.
/// What makes it safe:
///   - only stores with SignupEnabled accept it;
///   - it only ever grants the configured CUSTOMER scopes (never staff or admin);
///   - an existing membership is never changed: active -> "already_member", revoked/expired -> 403,
///     so a removed customer can't re-admit themselves with the link;
///   - the customer id is derived from (tenant, user), so retries and double clicks give the same id.
/// </summary>
public static class Onboarding
{
    public static async Task<IResult> JoinAsync(
        JoinStoreRequest request, ClaimsPrincipal principal, AuthOptions auth, IMembershipStore memberships,
        IConfiguration config, TimeProvider clock, ITenantCacheInvalidator tenantCache, IConsentStore consents, CancellationToken ct)
    {
        if (!ClaimsNormalizer.IsIdentifier(request.Store))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_store", "store is not a valid identifier.");
        }

        var store = (config.GetSection("Identity:Stores").Get<StoreOptions[]>() ?? [])
            .FirstOrDefault(s => string.Equals(s.Id, request.Store, StringComparison.Ordinal));
        if (store is null || !store.SignupEnabled)
        {
            return Problem(StatusCodes.Status404NotFound, "store_not_found", "This sign-up link isn't valid. Ask your retailer for a new one.");
        }

        var user = ClaimsNormalizer.FromPrincipal(principal, auth);
        var now = clock.GetUtcNow();

        // First record of what they agreed to at sign-up (no-op if already recorded and unchanged).
        await ConsentEndpoints.RecordFromTokenAsync(principal, auth, consents, config, clock, "signup", ct);
        var existing = (await memberships.FindByUserAsync(user.UserKey, ct)).FirstOrDefault(m => m.TenantId == store.TenantId);
        if (existing is not null)
        {
            return existing.IsActive(now)
                ? Results.Ok(new JoinStoreResponse("already_member", store.Name, store.TenantId, existing.CustomerId ?? ""))
                : Problem(StatusCodes.Status403Forbidden, "membership_inactive", "Your access to this store was removed. Contact the retailer.");
        }

        var scopes = CustomerScopes(config);
        var customerId = CustomerIdFor(store.TenantId, user.UserKey);
        var membership = await memberships.UpsertAsync(store.TenantId, user.UserKey, customerId, scopes, expiresAt: null, ct);
        await tenantCache.InvalidateAsync(membership.UserKey, membership.TenantId, ct);

        return Results.Created("/api/identity/me", new JoinStoreResponse("joined", store.Name, store.TenantId, customerId));
    }

    /// <summary>Identity:CustomerScopes, each of which must also be in the Identity:Scopes allow-list.</summary>
    internal static string[] CustomerScopes(IConfiguration config)
    {
        var allowed = config.GetSection("Identity:Scopes").Get<string[]>() ?? [];
        var scopes = config.GetSection("Identity:CustomerScopes").Get<string[]>() ?? ["quotes.read", "quotes.write"];
        var unknown = scopes.Where(s => !allowed.Contains(s, StringComparer.Ordinal)).ToArray();
        return unknown.Length == 0
            ? scopes
            : throw new InvalidOperationException($"Identity:CustomerScopes contains scopes not in Identity:Scopes: {string.Join(", ", unknown)}");
    }

    /// <summary>Stable per (tenant, user): "c-" + 12 hex chars of SHA-256.</summary>
    internal static string CustomerIdFor(string tenantId, string userKey) =>
        "c-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{userKey}")))[..12];

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
