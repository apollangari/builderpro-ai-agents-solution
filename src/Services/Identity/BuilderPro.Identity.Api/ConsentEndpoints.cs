using System.Security.Claims;
using BuilderPro.Identity.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Identity.Api;

public sealed record ConsentEventDto(string Consent, string Hours, string WordingVersion, string Source, DateTimeOffset At);
public sealed record ConsentSyncResponse(bool Recorded, ConsentEventDto? Current);

/// <summary>
/// Contact-consent history (1f). The customer's current choice lives in Keycloak (they set it at
/// sign-up and can change it in their account); it reaches us as the access-token claims
/// contact_consent / contact_hours. We keep the AUDIT TRAIL: an append-only row each time the
/// choice (or the consent wording) changes.
/// </summary>
public static class ConsentEndpoints
{
    public const string ConsentClaim = "contact_consent";
    public const string HoursClaim = "contact_hours";
    public const int HistoryLimit = 100;

    public static RouteGroupBuilder MapConsentEndpoints(this RouteGroupBuilder api)
    {
        var consent = api.MapGroup("/consent");
        consent.MapPost("/sync", SyncAsync)
            .WithSummary("Record the consent in the caller's token if it differs from the last recorded one. Idempotent.");
        consent.MapGet("/history", HistoryAsync)
            .WithSummary("The caller's own consent history, newest first.");
        return api;
    }

    /// <summary>Identity:ConsentWording:Version: bump it whenever the sign-up consent text changes.</summary>
    public static string WordingVersion(IConfiguration config) => config["Identity:ConsentWording:Version"] ?? "unversioned";

    public static Task<ConsentEvent?> RecordFromTokenAsync(
        ClaimsPrincipal principal, AuthOptions auth, IConsentStore store, IConfiguration config, TimeProvider clock, string source, CancellationToken ct)
    {
        var user = ClaimsNormalizer.FromPrincipal(principal, auth);
        return ConsentPolicy.RecordIfChangedAsync(store, user.UserKey,
            principal.FindFirst(ConsentClaim)?.Value, principal.FindFirst(HoursClaim)?.Value,
            WordingVersion(config), source, clock.GetUtcNow(), ct);
    }

    internal static async Task<IResult> SyncAsync(
        ClaimsPrincipal principal, AuthOptions auth, IConsentStore store, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var recorded = await RecordFromTokenAsync(principal, auth, store, config, clock, "sync", ct);
        var current = recorded ?? await store.LatestAsync(ClaimsNormalizer.FromPrincipal(principal, auth).UserKey, ct);
        return Results.Ok(new ConsentSyncResponse(recorded is not null, current is null ? null : ToDto(current)));
    }

    internal static async Task<IResult> HistoryAsync(ClaimsPrincipal principal, AuthOptions auth, IConsentStore store, CancellationToken ct)
    {
        var user = ClaimsNormalizer.FromPrincipal(principal, auth);
        return Results.Ok((await store.HistoryAsync(user.UserKey, HistoryLimit, ct)).Select(ToDto));
    }

    private static ConsentEventDto ToDto(ConsentEvent e) => new(e.Consent, e.Hours, e.WordingVersion, e.Source, e.At);
}
