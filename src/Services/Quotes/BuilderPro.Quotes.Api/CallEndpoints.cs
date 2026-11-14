using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuilderPro.Quotes.Domain;
using BuilderPro.Security;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Quotes.Api;

public sealed record AutomatedCallsRequest(bool Consent, string? WordingVersion);
public sealed record CallDto(Guid Id, string Status, DateTimeOffset DueAt, int Attempts, IReadOnlyList<DateTimeOffset> Proposals, DateTimeOffset? ChosenStart, string? Note);
public sealed record AutomatedCallsDto(bool Available, bool Consented, string WordingVersion, string Wording, string? Phone, string Hours, IReadOnlyList<CallDto> Calls);
public sealed record CallEventRequest(string? Event, string? Digits);

/// <summary>The customer's profile as Identity has it (phone, contact hours, contact consent).</summary>
public sealed record CustomerProfile(string? Phone, string ContactHours, string ContactConsent);

/// <summary>Reads the caller's own profile from Identity with their token (only while they're the caller).</summary>
public interface IProfileReader
{
    Task<CustomerProfile?> MineAsync(HttpContext http, CancellationToken ct);
}

public sealed class IdentityProfileReader(HttpClient client, ILogger<IdentityProfileReader> log) : IProfileReader
{
    private sealed record Profile(string? Phone, string? ContactHours, string? ContactConsent);

    public async Task<CustomerProfile?> MineAsync(HttpContext http, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(http.Request.Headers[HeaderNames.Authorization], out var auth) || auth.Parameter is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Parameter);
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var p = await response.Content.ReadFromJsonAsync<Profile>(ct);
            return p is null ? null : new CustomerProfile(string.IsNullOrWhiteSpace(p.Phone) ? null : p.Phone.Trim(), p.ContactHours ?? "business", p.ContactConsent ?? "none");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or JsonException)
        {
            log.LogWarning("Profile lookup failed ({Error})", ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>
/// 4b: AI phone scheduling, a store option (package feature "phone-scheduling").
///
///  - Consent first: the customer turns on automated calls on their project after the deposit, agreeing to
///    the exact wording (version recorded). Off again at any time; pressing 9 on a call does the same.
///  - The dispatcher (CallDispatcher) places a call only inside the customer's contact hours (CallWindows),
///    only while the project waits for a measure visit, and offers up to 3 visit times and a person.
///  - The call never books: a keypress for a time sends a link (text if they allow texts, and email); the
///    customer confirms the time themselves on their project page.
///  - The telephony provider reports keypresses to POST /api/calls/{id}/events, signed like Stripe webhooks.
/// </summary>
public static class CallEndpoints
{
    public const string DefaultWordingVersion = "2026-11-14";
    public const string DefaultWording =
        "Our automated assistant may call {phone} within your contact hours to offer measure-visit times for this project. " +
        "It will say it is automated, you can ask for a person, and you can stop these calls at any time (press 9 on a call, or turn this off). " +
        "Nothing is booked on the call: you confirm a time yourself from the link we send.";

    public static RouteGroupBuilder MapCallEndpoints(this RouteGroupBuilder projects)
    {
        projects.MapGet("/{id:guid}/automated-calls", GetAsync).WithSummary("Automated scheduling calls for this project: whether the store offers them, the consent wording, my consent, and the calls.");
        projects.MapPut("/{id:guid}/automated-calls", SetAsync).WithSummary("Turn automated scheduling calls on (agreeing to the shown wording version) or off. Customer only, never an agent.");
        return projects;
    }

    public static IEndpointRouteBuilder MapCallEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/calls/{id:guid}/events", EventAsync).AllowAnonymous()
            .WithSummary("Telephony provider callback (signed): answered, no_answer, or the keypress. 1-3 = a time (a confirm link is sent), 0 = a person, 9 = stop calls.");
        return app;
    }

    internal static (string Version, string Text) Wording(IConfiguration config) =>
        (config["Calls:WordingVersion"] ?? DefaultWordingVersion, config["Calls:Wording"] ?? DefaultWording);

    internal static async Task<IResult> GetAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, ICallStore calls, IConfiguration config, CancellationToken ct)
    {
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        var consent = project!.CustomerId is null ? null : await calls.LatestConsentAsync(tenant!.TenantId!, project.CustomerId, ct);
        var (version, text) = Wording(config);
        return Results.Ok(new AutomatedCallsDto(
            Available: tenant!.HasFeature(StoreFeatures.PhoneScheduling),
            Consented: consent?.Granted == true && consent.WordingVersion == version,
            version, text.Replace("{phone}", consent?.Phone ?? "your phone"), consent?.Granted == true ? consent.Phone : null, consent?.Hours ?? "business",
            [.. (await calls.ForProjectAsync(tenant.TenantId!, project.Id, ct)).Select(ToDto)]));
    }

    internal static async Task<IResult> SetAsync(
        Guid id, AutomatedCallsRequest request, HttpContext http, ITenantLookup tenants, IProjectStore projects, ICallStore calls, IVisitStore visits,
        IProfileReader profiles, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return Problem(StatusCodes.Status403Forbidden, "agent_cannot_consent", "Only the customer can agree to automated calls.");
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (tenant!.CustomerId is null || project!.CustomerId != tenant.CustomerId)
            return Problem(StatusCodes.Status403Forbidden, "customer_only", "Only the customer can choose automated calls.");
        if (!tenant.HasFeature(StoreFeatures.PhoneScheduling))
            return Problem(StatusCodes.Status403Forbidden, "feature_not_included", "Automated scheduling calls aren't part of your store's package.");

        var now = clock.GetUtcNow();
        var (version, _) = Wording(config);
        if (!request.Consent)
        {
            var last = await calls.LatestConsentAsync(tenant.TenantId!, tenant.CustomerId, ct);
            await calls.AppendConsentAsync(new CallConsent(0, tenant.TenantId!, tenant.CustomerId, false, last?.Phone, last?.Hours ?? "business", last?.Channels ?? "email", version, "project", now), ct);
            await calls.CancelOpenForCustomerAsync(tenant.TenantId!, tenant.CustomerId, CallStatus.OptedOut, "turned off by the customer", now, ct);
            return await GetAsync(id, http, tenants, projects, calls, config, ct);
        }
        if (request.WordingVersion != version)
            return Problem(StatusCodes.Status409Conflict, "stale_wording", "The wording changed; read it again and agree to the current version.");
        var profile = await profiles.MineAsync(http, ct);
        if (profile is null) return Problem(StatusCodes.Status503ServiceUnavailable, "profile_unavailable", "Your profile couldn't be read right now.");
        if (profile.Phone is null) return Problem(StatusCodes.Status422UnprocessableEntity, "no_phone", "Add a phone number to your account first.");

        var channels = profile.ContactConsent is "text" or "both" ? "email,sms" : "email";
        await calls.AppendConsentAsync(new CallConsent(0, tenant.TenantId!, tenant.CustomerId, true, profile.Phone, profile.ContactHours, channels, version, "project", now), ct);

        // A call is due now if the project waits for a visit; the dispatcher moves it into the contact hours.
        var visit = await visits.ForProjectAsync(tenant.TenantId!, project.Id, ct);
        var open = (await calls.ForProjectAsync(tenant.TenantId!, project.Id, ct)).Any(c => CallStatus.Open.Contains(c.Status));
        if (project.Stage == ProjectStages.Measure && visit is null && !open)
            await calls.CreateAsync(new CallRequest(Guid.NewGuid(), tenant.TenantId!, project.Id, tenant.CustomerId, CallStatus.Scheduled, now, 0, null, null, null, null, now, now), ct);
        return await GetAsync(id, http, tenants, projects, calls, config, ct);
    }

    /// <summary>The provider's report. Signed (Calls:WebhookSecret, same scheme as Stripe's); anything unsigned changes nothing.</summary>
    internal static async Task<IResult> EventAsync(
        Guid id, HttpContext http, ICallStore calls, IInstallStore outbox, IConfiguration config, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var secret = config["Calls:WebhookSecret"];
        if (string.IsNullOrEmpty(secret)) return Results.NotFound();
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var now = clock.GetUtcNow();
        if (!StripeSignature.IsValid(http.Request.Headers["X-BuilderPro-Signature"], body, secret, now)) return Results.BadRequest();
        CallEventRequest? e;
        try { e = JsonSerializer.Deserialize<CallEventRequest>(body, JsonSerializerOptions.Web); }
        catch (JsonException) { return Results.BadRequest(); }
        var call = await calls.FindAsync(id, ct);
        if (call is null || call.Status != CallStatus.Calling || e is null) return Results.Ok();   // unknown, finished, or a replay

        var log = logs.CreateLogger("Calls");
        var proposals = Proposals(call);
        switch (e.Event, e.Digits)
        {
            case ("no_answer", _):
                var consent = await calls.LatestConsentAsync(call.TenantId, call.CustomerId, ct);
                var again = call.Attempts < CallWindows.MaxAttempts && consent?.Granted == true
                    ? CallWindows.NextAllowed(now.AddHours(20), consent.Hours, StoreZone(config, call.TenantId)) : null;
                await calls.UpdateAsync(again is DateTimeOffset next
                    ? call with { Status = CallStatus.Scheduled, DueAt = next, Note = "no answer; will try again", UpdatedAt = now }
                    : call with { Status = CallStatus.NoAnswer, Note = "no answer", UpdatedAt = now }, ct);
                break;
            case ("digits", "9"):
                var last = await calls.LatestConsentAsync(call.TenantId, call.CustomerId, ct);
                await calls.AppendConsentAsync(new CallConsent(0, call.TenantId, call.CustomerId, false, last?.Phone, last?.Hours ?? "business",
                    last?.Channels ?? "email", last?.WordingVersion ?? "", "call", now), ct);
                await calls.CancelOpenForCustomerAsync(call.TenantId, call.CustomerId, CallStatus.OptedOut, "pressed 9 on a call", now, ct);
                break;
            case ("digits", "0"):
                await calls.UpdateAsync(call with { Status = CallStatus.PersonRequested, Note = "asked for a person", UpdatedAt = now }, ct);
                break;
            case ("digits", { Length: 1 } d) when d[0] is >= '1' and <= '3' && d[0] - '1' < proposals.Count:
                var chosen = proposals[d[0] - '1'];
                await calls.UpdateAsync(call with { Status = CallStatus.SlotChosen, ChosenStart = chosen, Note = "a time was chosen; confirm link sent", UpdatedAt = now }, ct);
                var who = await calls.LatestConsentAsync(call.TenantId, call.CustomerId, ct);
                var link = $"{(config["Payments:ReturnBaseUrl"] ?? "http://localhost:4200").TrimEnd('/')}/projects/{call.ProjectId}?visit={Uri.EscapeDataString(chosen.ToString("O"))}";
                var message = $"Confirm your measure visit for {TimeZoneInfo.ConvertTime(chosen, StoreZone(config, call.TenantId)):dddd MMM d, h:mm tt}: {link} (nothing is booked until you confirm).";
                var channels = (who?.Channels ?? "email").Split(',', StringSplitOptions.RemoveEmptyEntries);
                await NotifyAsync(outbox, call, channels, message, now, ct);
                break;
            default:
                log.LogInformation("Call {Call}: ignored event {Event}", call.Id, e.Event);
                break;
        }
        return Results.Ok();
    }

    private static Task NotifyAsync(IInstallStore outbox, CallRequest call, IEnumerable<string> channels, string message, DateTimeOffset now, CancellationToken ct) =>
        outbox.AddNotificationsAsync([.. channels.Where(c => c is "email" or "sms").Select(c =>
            new Notification(Guid.NewGuid(), call.TenantId, call.ProjectId, call.CustomerId, c, message, now))], ct);

    internal static IReadOnlyList<DateTimeOffset> Proposals(CallRequest call) =>
        string.IsNullOrEmpty(call.ProposalsJson) ? [] : JsonSerializer.Deserialize<DateTimeOffset[]>(call.ProposalsJson) ?? [];

    internal static TimeZoneInfo StoreZone(IConfiguration config, string tenantId) =>
        TimeZoneInfo.FindSystemTimeZoneById(config[$"Visits:{tenantId}:TimeZone"] ?? config["Visits:Default:TimeZone"] ?? "America/Chicago");

    internal static CallDto ToDto(CallRequest c) => new(c.Id, c.Status, c.DueAt, c.Attempts, Proposals(c), c.ChosenStart, c.Note);

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
