using System.Security.Claims;
using BuilderPro.Security;

namespace BuilderPro.Me.Api;

/// <summary>What the UI shows on its "who am I" card. Mirrored by MeResponse in Angular.</summary>
public sealed record MeResponse(
    bool Authenticated,
    string? UserId,
    string? UserName,
    string? TenantId,
    IReadOnlyList<string> Roles,
    string Service,
    DateTimeOffset ServerTimeUtc);

/// <summary>Public OIDC settings for the browser. Never contains secrets.</summary>
public sealed record AuthConfigResponse(string Authority, string ClientId, string Scope);

public static class MeEndpoints
{
    public const string ServiceName = "me-api";

    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous on purpose: the UI needs it BEFORE it can sign in.
        app.MapGet("/api/auth-config", GetAuthConfig)
            .AllowAnonymous()
            .WithName("GetAuthConfig")
            .WithSummary("Which identity provider and public client the browser should use.");

        // Protected: no valid token -> 401 before this code ever runs.
        app.MapGet("/api/me", GetMe)
            .RequireAuthorization()
            .WithName("GetMe")
            .WithSummary("Who is calling, and for which tenant.");

        return app;
    }

    internal static AuthConfigResponse GetAuthConfig(AuthOptions options) =>
        new(options.Authority, options.WebClientId, options.WebScope);

    // TimeProvider is .NET's built-in clock abstraction: tests inject a fake one,
    // so "what time is it" is never a source of flaky tests.
    internal static MeResponse GetMe(ClaimsPrincipal user, AuthOptions options, TimeProvider clock)
    {
        var authenticated = user.Identity?.IsAuthenticated == true;
        return new MeResponse(
            Authenticated: authenticated,
            UserId: authenticated ? user.FindFirstValue("sub") : null,
            UserName: authenticated ? user.Identity?.Name : null,
            TenantId: authenticated ? user.FindFirstValue(options.TenantClaim) : null,
            Roles: authenticated ? [.. user.FindAll(options.RoleClaim).Select(c => c.Value).Order()] : [],
            Service: ServiceName,
            ServerTimeUtc: clock.GetUtcNow());
    }
}
