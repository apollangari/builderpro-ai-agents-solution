using System.Text.Json;
using BuilderPro.Quotes.Domain;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// 4b: places due scheduling calls, one rule at a time, every Calls:DispatchSeconds (60):
///  1. the customer's LATEST automated-call consent must be "granted" (an opt-out cancels the call);
///  2. the project must still wait for a measure visit (booked or moved on: cancelled);
///  3. now must be inside their contact hours (CallWindows); otherwise the call moves to the next allowed time;
///  4. there must be open visit times that suit their hours (none: cancelled, with a note for staff);
///  5. then the call is placed: it says it's automated, offers the times, 0 for a person, 9 to stop.
/// A provider failure leaves the call scheduled for the next allowed time (at most CallWindows.MaxAttempts).
/// </summary>
public sealed class CallDispatcher(IServiceScopeFactory scopes, IConfiguration config, ILogger<CallDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue<int?>("Calls:DispatchSeconds") ?? 60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Call dispatch pass failed"); }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var calls = sp.GetRequiredService<ICallStore>();
        var projects = sp.GetRequiredService<IProjectStore>();
        var visits = sp.GetRequiredService<IVisitStore>();
        var telephony = sp.GetRequiredService<ITelephony>();
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();
        var placed = 0;

        foreach (var call in await calls.DueAsync(now, 50, ct))
        {
            var zone = CallEndpoints.StoreZone(config, call.TenantId);
            var consent = await calls.LatestConsentAsync(call.TenantId, call.CustomerId, ct);
            if (consent is not { Granted: true, Phone: { Length: > 0 } phone })
            {
                await calls.UpdateAsync(call with { Status = CallStatus.Cancelled, Note = "no automated-call consent", UpdatedAt = now }, ct);
                continue;
            }
            var project = await projects.FindAsync(call.TenantId, call.CustomerId, call.ProjectId, ct);
            if (project?.Stage != ProjectStages.Measure || await visits.ForProjectAsync(call.TenantId, call.ProjectId, ct) is not null)
            {
                await calls.UpdateAsync(call with { Status = CallStatus.Cancelled, Note = "the visit is booked or no longer needed", UpdatedAt = now }, ct);
                continue;
            }
            if (!CallWindows.MayCall(now, consent.Hours, zone))
            {
                var next = CallWindows.NextAllowed(now, consent.Hours, zone);
                await calls.UpdateAsync(next is DateTimeOffset t
                    ? call with { DueAt = t, Note = "waiting for your contact hours", UpdatedAt = now }
                    : call with { Status = CallStatus.Cancelled, Note = "no allowed calling time", UpdatedAt = now }, ct);
                continue;
            }

            var slots = VisitEndpoints.SlotsFor(call.TenantId, config, now);
            var booked = slots.Count == 0 ? new Dictionary<DateTimeOffset, int>() : await visits.BookedAsync(call.TenantId, slots[0], slots[^1].AddMinutes(1), ct);
            var perSlot = config.GetValue<int?>("Visits:PerSlot") ?? 2;
            var options = CallWindows.ProposeVisits(slots.Select(s => (s, booked.GetValueOrDefault(s) < perSlot)), consent.Hours, zone);
            if (options.Count == 0)
            {
                await calls.UpdateAsync(call with { Status = CallStatus.Cancelled, Note = "no open visit time fits the contact hours; staff to follow up", UpdatedAt = now }, ct);
                continue;
            }

            var store = config[$"Calls:{call.TenantId}:StoreName"] ?? "your store";
            var script = Script(store, options, zone);
            var callback = $"{(config["Calls:CallbackBaseUrl"] ?? "http://localhost").TrimEnd('/')}/api/calls/{call.Id}/events";
            try
            {
                var reference = await telephony.PlaceCallAsync(new CallPlacement(call.Id, phone, script, options, callback), ct);
                await calls.UpdateAsync(call with
                {
                    Status = CallStatus.Calling, Attempts = call.Attempts + 1, ProposalsJson = JsonSerializer.Serialize(options),
                    ProviderRef = reference, Note = null, UpdatedAt = now,
                }, ct);
                placed++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or InvalidOperationException)
            {
                log.LogWarning("Placing call {Call} failed ({Error})", call.Id, ex.GetType().Name);
                var retry = call.Attempts + 1 < CallWindows.MaxAttempts ? CallWindows.NextAllowed(now.AddMinutes(30), consent.Hours, zone) : null;
                await calls.UpdateAsync(retry is DateTimeOffset r
                    ? call with { Attempts = call.Attempts + 1, DueAt = r, Note = "provider failed; will retry", UpdatedAt = now }
                    : call with { Status = CallStatus.Cancelled, Attempts = call.Attempts + 1, Note = "provider failed", UpdatedAt = now }, ct);
            }
        }
        return placed;
    }

    /// <summary>What the call says. Automated, the options, a person, and how to stop: on every call.</summary>
    internal static string Script(string store, IReadOnlyList<DateTimeOffset> options, TimeZoneInfo zone)
    {
        var lines = options.Select((o, i) => $"Press {i + 1} for {TimeZoneInfo.ConvertTime(o, zone):dddd MMMM d 'at' h:mm tt}.");
        return $"Hello, this is an automated assistant calling for {store} about your measure visit. " +
               string.Join(" ", lines) +
               " We'll send you a link to confirm; nothing is booked until you do. Press 0 to talk to a person. Press 9 to stop these automated calls.";
    }
}
