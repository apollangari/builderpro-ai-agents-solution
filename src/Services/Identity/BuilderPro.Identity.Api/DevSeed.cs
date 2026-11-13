using BuilderPro.Identity.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Identity.Api;

/// <summary>One membership to create at startup in local development.</summary>
public sealed class SeedMembership
{
    public string Subject { get; set; } = "";       // the Keycloak user id (fixed in the realm file)
    public string TenantId { get; set; } = "";
    public string? CustomerId { get; set; }
    public string[] Scopes { get; set; } = [];
}

/// <summary>
/// LOCAL DEVELOPMENT ONLY: makes the Keycloak test users members of their tenants, so the app
/// works on first run. Keys are computed exactly like at runtime (issuer + subject), so seeding
/// and login agree. Refuses to run in Production.
/// </summary>
public static class DevSeed
{
    public static async Task RunAsync(WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Identity:SeedDevMemberships")) return;
        if (app.Environment.IsProduction())
        {
            throw new InvalidOperationException("Identity:SeedDevMemberships must never be enabled in Production.");
        }

        var issuer = app.Configuration["Auth:Authority"]!.TrimEnd('/');
        var seeds = app.Configuration.GetSection("Identity:DevMemberships").Get<SeedMembership[]>() ?? [];
        var store = app.Services.GetRequiredService<IMembershipStore>();
        var tenantCache = app.Services.GetRequiredService<ITenantCacheInvalidator>();

        foreach (var seed in seeds)
        {
            var key = ExternalIdentityKey.ForIssuerAndSubject(issuer, seed.Subject);
            await store.UpsertAsync(seed.TenantId, key, seed.CustomerId, seed.Scopes, expiresAt: null, CancellationToken.None);
            await tenantCache.InvalidateAsync(key, seed.TenantId, CancellationToken.None);
        }
        app.Logger.LogInformation("Seeded {Count} development memberships for issuer {Issuer}", seeds.Length, issuer);
    }
}
