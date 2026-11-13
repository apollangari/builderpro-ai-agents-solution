using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Security;

/// <summary>
/// Either the caller's tenant (with the customer and scopes their membership grants), or the HTTP
/// problem to return instead. CustomerId is set for customers and null for the retailer's staff.
/// </summary>
public sealed record TenantLookup(string? TenantId, string? CustomerId, IReadOnlyList<string> Scopes, int Status, string Code,
    IReadOnlyList<string>? Features = null)
{
    public bool Resolved => TenantId is not null;

    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    /// <summary>
    /// Is this part of the store's package (1h)? Identity always reports the package; null only when a
    /// caller built the lookup without one (older callers and tests), which means the full package.
    /// </summary>
    public bool HasFeature(string feature) => Features is null || Features.Contains(feature, StringComparer.Ordinal);

    public static TenantLookup Ok(string tenantId, IReadOnlyList<string>? scopes = null, string? customerId = null, IReadOnlyList<string>? features = null) =>
        new(tenantId, customerId, scopes ?? [], StatusCodes.Status200OK, "resolved", features);

    public static TenantLookup Fail(int status, string code) => new(null, null, [], status, code);
}

/// <summary>"Which tenant is this request for, with which scopes?" Business services never answer that themselves.</summary>
public interface ITenantLookup
{
    Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct);
}

public static class TenantLookupSetup
{
    /// <summary>
    /// Registers <see cref="ITenantLookup"/>: a typed HttpClient to the Identity service, wrapped in
    /// <see cref="CachingTenantLookup"/> when a Redis "cache" connection is configured (LEARN 2f).
    /// "http://identity" is a LOGICAL name resolved by service discovery; retries, timeouts and the
    /// circuit breaker come from ServiceDefaults.
    /// </summary>
    public static IServiceCollection AddTenantLookup(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient<IdentityTenantLookup>(c =>
            c.BaseAddress = new Uri(config["Tenancy:IdentityBaseUrl"] ?? "http://identity"));

        if (TenantCacheSetup.HasCache(config))
        {
            var ttl = TimeSpan.FromSeconds(config.GetValue<int?>("Tenancy:CacheSeconds") ?? 60);
            services.AddTransient<ITenantLookup>(sp => new CachingTenantLookup(
                sp.GetRequiredService<IdentityTenantLookup>(),
                sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
                sp.GetRequiredService<AuthOptions>(),
                ttl,
                sp.GetRequiredService<ILogger<CachingTenantLookup>>()));
        }
        else
        {
            services.AddTransient<ITenantLookup>(sp => sp.GetRequiredService<IdentityTenantLookup>());
        }
        return services;
    }
}

/// <summary>
/// Asks the Identity service (GET /api/identity/me) with the CALLER'S OWN bearer token.
///
/// Why not read the tenant claim from the token? Because memberships are the source of
/// truth (LEARN 1d): a token claim is only a hint, and access must be revocable instantly.
/// Identity applies the one TenantResolver rule; every other service reuses its answer
/// (LEARN 2a).
///
/// Identity's refusals (403 no_membership, 409 multiple_active_memberships, ...) are passed
/// through unchanged so the browser sees the same codes everywhere. Anything unexpected
/// fails CLOSED as 503: never "show everything" or "trust the claim".
/// </summary>
public sealed class IdentityTenantLookup(HttpClient http, ILogger<IdentityTenantLookup> log) : ITenantLookup
{
    public async Task<TenantLookup> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers[HeaderNames.Authorization], out var auth)
            || !string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(auth.Parameter))
        {
            return TenantLookup.Fail(StatusCodes.Status401Unauthorized, "missing_token");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Parameter);
        // Pass the store picker's choice along; Identity validates it against memberships.
        var hint = context.Request.Headers[TenantHint.Header].ToString();
        if (ClaimsNormalizer.IsIdentifier(hint)) request.Headers.Add(TenantHint.Header, hint);

        try
        {
            using var response = await http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var me = await response.Content.ReadFromJsonAsync<IdentityMe>(ct);
                if (!ClaimsNormalizer.IsIdentifier(me?.TenantId)) return Unavailable("identity returned no valid tenantId");
                if (me!.CustomerId is not null && !ClaimsNormalizer.IsIdentifier(me.CustomerId)) return Unavailable("identity returned an invalid customerId");
                return TenantLookup.Ok(me.TenantId!, me.Scopes ?? [], me.CustomerId, me.Features);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
            {
                var problem = await TryReadProblemAsync(response, ct);
                return TenantLookup.Fail((int)response.StatusCode, problem?.Code ?? "tenant_not_resolved");
            }

            return Unavailable($"identity answered {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                       or System.Text.Json.JsonException)
        {
            log.LogWarning(ex, "Tenant lookup against the Identity service failed");
            return TenantLookup.Fail(StatusCodes.Status503ServiceUnavailable, "tenant_lookup_unavailable");
        }
    }

    private TenantLookup Unavailable(string why)
    {
        log.LogWarning("Tenant lookup failed closed: {Reason}", why);
        return TenantLookup.Fail(StatusCodes.Status503ServiceUnavailable, "tenant_lookup_unavailable");
    }

    private static async Task<IdentityProblem?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<IdentityProblem>(ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // Only the fields callers need. Identity owns the full contract.
    private sealed record IdentityMe(
        [property: JsonPropertyName("tenantId")] string? TenantId,
        [property: JsonPropertyName("customerId")] string? CustomerId,
        [property: JsonPropertyName("scopes")] string[]? Scopes,
        [property: JsonPropertyName("features")] string[]? Features);

    private sealed record IdentityProblem([property: JsonPropertyName("code")] string? Code);
}
