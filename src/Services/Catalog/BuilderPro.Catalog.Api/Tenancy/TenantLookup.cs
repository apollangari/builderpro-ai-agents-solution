using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BuilderPro.Security;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Catalog.Api.Tenancy;

/// <summary>Either the caller's tenant, or the HTTP problem to return instead.</summary>
public sealed record TenantLookup(string? TenantId, int Status, string Code)
{
    public bool Resolved => TenantId is not null;

    public static TenantLookup Ok(string tenantId) => new(tenantId, StatusCodes.Status200OK, "resolved");
    public static TenantLookup Fail(int status, string code) => new(null, status, code);
}

/// <summary>"Which tenant is this request for?" Catalog never answers that itself.</summary>
public interface ITenantLookup
{
    Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct);
}

/// <summary>
/// Asks the Identity service (GET /api/identity/me) with the CALLER'S OWN bearer token.
///
/// Why not read the tenant claim from the token? Because memberships are the source of
/// truth (LEARN 1d): a token claim is only a hint, and access must be revocable instantly.
/// Identity applies the one TenantResolver rule; every other service reuses its answer.
///
/// Identity's refusals (403 no_membership, 409 multiple_active_memberships, ...) are passed
/// through unchanged so the browser sees the same codes everywhere. Anything unexpected
/// fails CLOSED as 503: we never fall back to "show all products" or "trust the claim".
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

        try
        {
            using var response = await http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var me = await response.Content.ReadFromJsonAsync<IdentityMe>(ct);
                return ClaimsNormalizer.IsIdentifier(me?.TenantId)
                    ? TenantLookup.Ok(me!.TenantId!)
                    : Unavailable("identity returned no valid tenantId");
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

    // Only the fields Catalog needs. Identity owns the full contract.
    private sealed record IdentityMe([property: JsonPropertyName("tenantId")] string? TenantId);
    private sealed record IdentityProblem([property: JsonPropertyName("code")] string? Code);
}
