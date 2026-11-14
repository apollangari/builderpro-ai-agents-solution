using System.Text.Json;
using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

public sealed record VisitDto(Guid Id, DateTimeOffset Start, string Status, DateTimeOffset? MeasuredAt);
/// <summary>FitsHours: inside the customer's contact hours (null when unknown, e.g. for staff).</summary>
public sealed record VisitSlotDto(DateTimeOffset Start, bool Available, bool? FitsHours = null);
public sealed record BookVisitRequest(DateTimeOffset? Start);
public sealed record MeasuredRequest(MeasurementInput? Measurements);

/// <summary>
/// 6b: the measure visit, after the deposit is paid.
///
///  1. Book: the customer (or store staff with scheduling.write) picks one of the store's visit times
///     (Visits:Times in Visits:TimeZone, next Visits:Days days, Visits:PerSlot bookings per time).
///     Booking again reschedules.
///  2. Measured: staff with scheduling.write record the measured sizes. The job is re-priced with the
///     measured quantities at the HELD unit prices (HeldPrices) and a new numbered quote is issued.
///     Same total: it is accepted on the customer's behalf (nothing changed for them) and the project
///     moves to install. Different total: the project waits in "confirm".
///  3. Confirm: the customer accepts the measured quote (never an agent, never staff) -> install.
/// </summary>
public static class VisitEndpoints
{
    public const string SchedulingScope = "scheduling.write";

    public static RouteGroupBuilder MapVisitEndpoints(this RouteGroupBuilder projects)
    {
        projects.MapGet("/{id:guid}/visit/slots", SlotsAsync).WithSummary("Measure-visit times for this project (after the deposit), with whether each still has room.");
        projects.MapPost("/{id:guid}/visit", BookAsync).WithSummary("Book (or move) the measure visit. The customer, or store staff with scheduling.write.");
        projects.MapPost("/{id:guid}/visit/measured", MeasuredAsync).WithSummary("Staff: record the measured sizes; re-priced at the held prices. A changed total waits for the customer to confirm.");
        projects.MapPost("/{id:guid}/confirm", ConfirmAsync).WithSummary("The customer accepts the measured quote; installation is next. Never an agent.");
        return projects;
    }

    internal static async Task<IResult> SlotsAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IVisitStore visits, IProfileReader profiles, IConfiguration config,
        TimeProvider clock, CancellationToken ct)
    {
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        if (project!.Stage != ProjectStages.Measure) return NotMeasureStage();
        var slots = SlotsFor(tenant!.TenantId!, config, clock.GetUtcNow());
        if (slots.Count == 0) return Results.Ok(Array.Empty<VisitSlotDto>());
        var booked = await visits.BookedAsync(tenant.TenantId!, slots[0], slots[^1].AddMinutes(1), ct);
        var mine = await visits.ForProjectAsync(tenant.TenantId!, project.Id, ct);
        var perSlot = PerSlot(config);
        // Gap fix (6b): the customer's contact hours mark the times that suit them (same rule as calls and the 4a agent).
        var hours = tenant.CustomerId is null ? null : (await profiles.MineAsync(http, ct))?.ContactHours;
        var zone = CallEndpoints.StoreZone(config, tenant.TenantId!);
        return Results.Ok(slots.Select(s =>
            new VisitSlotDto(s, mine?.Start == s || booked.GetValueOrDefault(s) < perSlot, hours is null ? null : CallWindows.VisitFits(s, hours, zone))));
    }

    internal static async Task<IResult> BookAsync(
        Guid id, BookVisitRequest request, HttpContext http, ITenantLookup tenants, IProjectStore projects, IVisitStore visits,
        IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        var customer = tenant!.CustomerId is not null;
        if (customer ? !tenant.HasScope(QuoteEndpoints.WriteScope) : !tenant.HasScope(SchedulingScope))
            return Problem(StatusCodes.Status403Forbidden, "missing_scope", "Booking needs the customer or store scheduling.");
        if (project!.Stage != ProjectStages.Measure || project.CustomerId is null) return NotMeasureStage();
        var current = await visits.ForProjectAsync(tenant.TenantId!, project.Id, ct);
        if (current?.Status == VisitStatus.Measured) return Problem(StatusCodes.Status409Conflict, "already_measured", "The visit already took place.");

        var now = clock.GetUtcNow();
        if (request.Start is not DateTimeOffset start || !SlotsFor(tenant.TenantId!, config, now).Contains(start.ToUniversalTime()))
            return Problem(StatusCodes.Status400BadRequest, "invalid_slot", "Pick one of the offered visit times.");

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var visit = new Visit(Guid.NewGuid(), tenant.TenantId!, project.Id, project.CustomerId, start.ToUniversalTime(), VisitStatus.Booked, user.UserKey, now);
        if (!await visits.BookAsync(visit, PerSlot(config), ct))
            return Problem(StatusCodes.Status409Conflict, "slot_full", "That time was just taken. Pick another one.");
        await ContactSnapshot.RefreshAsync(http, tenant.TenantId!, tenant.CustomerId, ct);   // where to send visit and install updates
        await projects.UpdateAsync(project with { UpdatedAt = now }, ct);
        return Results.Ok(ToDto(visit));
    }

    internal static async Task<IResult> MeasuredAsync(
        Guid id, MeasuredRequest request, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, IVisitStore visits,
        IDepositStore deposits, ICatalogPrices catalog, IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return Problem(StatusCodes.Status403Forbidden, "agent_cannot_measure", "Measurements are recorded by store staff.");
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        if (tenant!.CustomerId is not null || !tenant.HasScope(SchedulingScope))
            return Problem(StatusCodes.Status403Forbidden, "staff_only", "Measurements are recorded by store staff.");
        if (project!.Stage != ProjectStages.Measure || project.CustomerId is null) return NotMeasureStage();
        var visit = await visits.ForProjectAsync(tenant.TenantId!, project.Id, ct);
        if (visit?.Status != VisitStatus.Booked) return Problem(StatusCodes.Status409Conflict, "no_visit", "Book the measure visit first.");
        var held = await ProjectEndpoints.QuoteOf(project, tenant, quotes, ct);
        var deposit = await deposits.ForProjectAsync(tenant.TenantId!, project.Id, ct);
        if (held?.Issue is null || deposit?.Paid != true) return Problem(StatusCodes.Status409Conflict, "not_held", "This project has no paid deposit.");

        var asked = ProjectEndpoints.RequestOf(project);
        var measuredRequest = asked with { Measurements = request.Measurements };
        var outcome = await GuidedQuoteEndpoints.PriceAsync(measuredRequest, tenant.TenantId!, http, catalog, config, ct);
        if (outcome.Result is null) return outcome.ToProblem();
        if (!outcome.Result.Complete)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "incomplete_measurements", detail: "Record every measurement the job needs.",
                extensions: new Dictionary<string, object?> { ["code"] = "incomplete_measurements", ["missing"] = outcome.Result.Missing });

        var priced = HeldPrices.Apply(outcome.Result.Quote!, held.Priced);
        var now = clock.GetUtcNow();
        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var quote = await quotes.IssueAsync(new Quote(Guid.NewGuid(), tenant.TenantId!, project.CustomerId, user.UserKey, "issued", priced, now,
            new GuidedInfo(project.Zip, ProjectEndpoints.RequestJson(measuredRequest), outcome.Result.LineGroups),
            new QuoteIssue(0, now.AddDays(ProjectEndpoints.ValidityDays(config)), project.Id)), ct);

        // Same total: nothing for the customer to decide, so the measured quote is the order.
        var unchanged = priced.Total == held.Priced.Total;
        if (unchanged) await quotes.AcceptAsync(tenant.TenantId!, project.CustomerId, quote.Id, now, ct);
        await visits.MarkMeasuredAsync(tenant.TenantId!, visit.Id, user.UserKey, now, ct);

        var answers = JsonSerializer.Serialize(new { measurements = request.Measurements, choices = asked.Choices ?? [] }, JsonSerializerOptions.Web);
        var updated = project with
        {
            AnswersJson = answers, QuoteId = quote.Id, UpdatedAt = now,
            Stage = unchanged ? ProjectStages.Install : ProjectStages.Confirm,
        };
        await projects.UpdateAsync(updated, ct);
        return Results.Ok(await ProjectEndpoints.ToDtoAsync(updated, http, catalog, await quotes.FindAsync(tenant.TenantId!, null, quote.Id, ct), config, ct, clock));
    }

    internal static async Task<IResult> ConfirmAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, ICatalogPrices catalog,
        IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return Problem(StatusCodes.Status403Forbidden, "agent_cannot_accept", "Only the customer can confirm the measured quote.");
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (tenant!.CustomerId is null || project!.CustomerId != tenant.CustomerId)
            return Problem(StatusCodes.Status403Forbidden, "customer_only", "Only the customer can confirm the measured quote.");
        if (project.Stage != ProjectStages.Confirm) return Problem(StatusCodes.Status409Conflict, "nothing_to_confirm", "There's no measured quote waiting for you.");
        var quote = await ProjectEndpoints.QuoteOf(project, tenant, quotes, ct);
        var now = clock.GetUtcNow();
        if (quote?.Issue is null) return Problem(StatusCodes.Status409Conflict, "no_quote", "There's no measured quote waiting for you.");
        if (quote.Issue.ValidUntil <= now) return Problem(StatusCodes.Status409Conflict, "quote_expired", "The measured quote expired. Ask the store for an updated one.");
        if (!await quotes.AcceptAsync(tenant.TenantId!, tenant.CustomerId, quote.Id, now, ct))
            return Problem(StatusCodes.Status409Conflict, "quote_not_acceptable", "This quote can't be accepted.");

        var updated = project with { Stage = ProjectStages.Install, UpdatedAt = now };
        await projects.UpdateAsync(updated, ct);
        return Results.Ok(await ProjectEndpoints.ToDtoAsync(updated, http, catalog, await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, quote.Id, ct), config, ct, clock));
    }

    internal static VisitDto ToDto(Visit v) => new(v.Id, v.Start, v.Status, v.MeasuredAt);

    /// <summary>The store's visit times from tomorrow on (config per tenant, else defaults: 9:00 and 13:00 Central, 14 days, closed Sunday).</summary>
    internal static IReadOnlyList<DateTimeOffset> SlotsFor(string tenantId, IConfiguration config, DateTimeOffset now)
    {
        string? Get(string key) => config[$"Visits:{tenantId}:{key}"] ?? config[$"Visits:Default:{key}"];
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Get("TimeZone") ?? "America/Chicago");
        var times = (Get("Times") ?? "09:00,13:00").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(TimeOnly.Parse).ToList();
        var closed = (Get("ClosedDays") ?? "Sunday").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<DayOfWeek>).ToList();
        var days = int.TryParse(Get("Days"), out var d) ? d : 14;
        return VisitSlots.Next(now, zone, times, days, closed);
    }

    private static int PerSlot(IConfiguration config) => config.GetValue<int?>("Visits:PerSlot") ?? 2;

    private static IResult NotMeasureStage() =>
        Problem(StatusCodes.Status409Conflict, "not_measure_stage", "The measure visit comes after the deposit is paid.");

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
