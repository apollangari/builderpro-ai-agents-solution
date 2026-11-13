using System.Security.Claims;
using BuilderPro.Identity.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Identity.Api;

public sealed record IdentityMeResponse(
    string UserKey, string? UserName, string? Email, IReadOnlyList<string> Roles,
    string TenantId, string? CustomerId, IReadOnlyList<string> Scopes, string Source);

public sealed record MembershipDto(
    Guid Id, string TenantId, string UserKey, string? CustomerId, IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, string? RevokedReason, bool Active);

public sealed record UpsertMembershipRequest(string TenantId, string UserKey, string? CustomerId, string[] Scopes, DateTimeOffset? ExpiresAt);

public sealed record RevokeRequest(string? Reason);

public static class IdentityEndpoints
{
    public const string AdminPolicy = "IdentityAdmin";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/identity").RequireAuthorization();

        api.MapGet("/me", GetMeAsync)
            .WithSummary("The caller as the PLATFORM sees them: tenant resolved from memberships, not just the token.");

        var admin = api.MapGroup("/memberships").RequireAuthorization(AdminPolicy);
        admin.MapGet("/", ListAsync);
        admin.MapPost("/", UpsertAsync);
        admin.MapPost("/{id:guid}/revoke", RevokeAsync);

        return app;
    }

    internal static async Task<IResult> GetMeAsync(
        ClaimsPrincipal principal, AuthOptions options, IMembershipStore store, TimeProvider clock, CancellationToken ct)
    {
        var user = ClaimsNormalizer.FromPrincipal(principal, options);
        var result = TenantResolver.Resolve(user, await store.FindByUserAsync(user.UserKey, ct), clock.GetUtcNow());

        return result.Outcome switch
        {
            TenantResolution.Resolved => Results.Ok(new IdentityMeResponse(
                user.UserKey, user.UserName, user.Email, user.Roles,
                result.Membership!.TenantId, result.Membership.CustomerId, result.Membership.Scopes, Source: "membership")),
            TenantResolution.Ambiguous => Problem(StatusCodes.Status409Conflict, result.Code,
                "You belong to several tenants and the token doesn't say which one."),
            _ => Problem(StatusCodes.Status403Forbidden, result.Code,
                result.Outcome == TenantResolution.HintMismatch
                    ? "Your token names a tenant you have no active membership in."
                    : "You are signed in but not a member of any tenant yet."),
        };
    }

    internal static async Task<IResult> ListAsync(string tenantId, IMembershipStore store, TimeProvider clock, CancellationToken ct)
    {
        if (!ClaimsNormalizer.IsIdentifier(tenantId)) return Problem(400, "invalid_tenant", "tenantId is not a valid identifier.");
        var now = clock.GetUtcNow();
        return Results.Ok((await store.ListByTenantAsync(tenantId, ct)).Select(m => ToDto(m, now)));
    }

    internal static async Task<IResult> UpsertAsync(UpsertMembershipRequest request, IMembershipStore store, IConfiguration config, TimeProvider clock, ITenantCacheInvalidator tenantCache, CancellationToken ct)
    {
        if (!ClaimsNormalizer.IsIdentifier(request.TenantId)) return Problem(400, "invalid_tenant", "tenantId is not a valid identifier.");
        if (string.IsNullOrWhiteSpace(request.UserKey) || request.UserKey.Length > 200) return Problem(400, "invalid_user", "userKey is required.");
        if (request.CustomerId is not null && !ClaimsNormalizer.IsIdentifier(request.CustomerId)) return Problem(400, "invalid_customer", "customerId is not a valid identifier.");

        // Allow-list: a typo'd or invented scope never makes it into the store.
        var allowed = config.GetSection("Identity:Scopes").Get<string[]>() ?? [];
        var unknown = request.Scopes.Where(s => !allowed.Contains(s, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0) return Problem(400, "unknown_scope", $"Not configured: {string.Join(", ", unknown)}");

        var membership = await store.UpsertAsync(request.TenantId, request.UserKey, request.CustomerId, request.Scopes, request.ExpiresAt, ct);
        await tenantCache.InvalidateAsync(membership.UserKey, membership.TenantId, ct);   // other services see the change now
        return Results.Ok(ToDto(membership, clock.GetUtcNow()));
    }

    internal static async Task<IResult> RevokeAsync(Guid id, RevokeRequest request, IMembershipStore store, TimeProvider clock, ITenantCacheInvalidator tenantCache, CancellationToken ct)
    {
        var revoked = await store.RevokeAsync(id, request.Reason ?? "revoked", ct);
        // Revocation must be immediate everywhere, not "within the cache TTL".
        if (revoked is not null) await tenantCache.InvalidateAsync(revoked.UserKey, revoked.TenantId, ct);
        return revoked is null ? Results.NotFound() : Results.Ok(ToDto(revoked, clock.GetUtcNow()));
    }

    private static MembershipDto ToDto(Membership m, DateTimeOffset now) =>
        new(m.Id, m.TenantId, m.UserKey, m.CustomerId, m.Scopes, m.ExpiresAt, m.RevokedAt, m.RevokedReason, m.IsActive(now));

    // RFC 9457 problem+json with a stable machine-readable "code" clients can branch on.
    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
