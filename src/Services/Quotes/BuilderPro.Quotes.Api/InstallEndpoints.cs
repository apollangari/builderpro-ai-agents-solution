using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

public sealed record InstallStepRequest(string? Step, DateTimeOffset? ScheduledFor = null, string? Note = null);
public sealed record InstallEventDto(string Step, DateTimeOffset At, DateTimeOffset? ScheduledFor, string? Note);
public sealed record InstallDto(string? Step, DateTimeOffset? ScheduledFor, IReadOnlyList<InstallEventDto> Events);
public sealed record NotificationDto(string Channel, string Message, DateTimeOffset CreatedAt);

/// <summary>
/// 6c: installation tracking. Store staff (scheduling.write) move the job through
/// scheduled -> fabricating -> installing -> done, one step at a time ("scheduled" again moves the date
/// until fabrication starts). Done closes the project.
///
/// Each step records an order update for the customer on the channels they consented to (Identity
/// decides from their LATEST consent: email always; text or calls only if they agreed). Updates are
/// written to an outbox in the same transaction as the step; sending them is a notifier's job (later).
/// If Identity can't answer, only email is recorded: fail closed for texts and calls.
/// </summary>
public static class InstallEndpoints
{
    public const int MaxNote = 500;

    public static RouteGroupBuilder MapInstallEndpoints(this RouteGroupBuilder projects)
    {
        projects.MapPost("/{id:guid}/install", AdvanceAsync).WithSummary("Staff: move the installation to its next step (scheduled, fabricating, installing, done).");
        projects.MapGet("/{id:guid}/notifications", NotificationsAsync).WithSummary("Order updates recorded for this project's customer, with their channel.");
        return projects;
    }

    internal static async Task<IResult> AdvanceAsync(
        Guid id, InstallStepRequest request, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes,
        IInstallStore installs, IContactChannels channels, ICatalogPrices catalog, IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return Problem(StatusCodes.Status403Forbidden, "staff_only", "Installation steps are recorded by store staff.");
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        if (tenant!.CustomerId is not null || !tenant.HasScope(VisitEndpoints.SchedulingScope))
            return Problem(StatusCodes.Status403Forbidden, "staff_only", "Installation steps are recorded by store staff.");
        if (project!.Stage != ProjectStages.Install || project.CustomerId is null)
            return Problem(StatusCodes.Status409Conflict, "not_install_stage", "Installation starts once the measured quote is confirmed.");

        var step = request.Step?.Trim().ToLowerInvariant();
        if (step is null || !InstallSteps.All.Contains(step)) return Problem(StatusCodes.Status400BadRequest, "invalid_step", $"step is one of {string.Join(", ", InstallSteps.All)}.");
        var history = await installs.ForProjectAsync(tenant.TenantId!, project.Id, ct);
        var current = history.LastOrDefault()?.Step;
        if (!InstallSteps.CanMove(current, step))
            return Problem(StatusCodes.Status409Conflict, "invalid_transition", $"The installation is at {current ?? "not scheduled"}; the next step is {Next(current)}.");

        var now = clock.GetUtcNow();
        if (step == InstallSteps.Scheduled && (request.ScheduledFor is not DateTimeOffset when || when <= now))
            return Problem(StatusCodes.Status400BadRequest, "invalid_date", "Send the installation date (in the future).");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > MaxNote) return Problem(StatusCodes.Status400BadRequest, "invalid_note", $"A note is at most {MaxNote} characters.");

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var e = new InstallEvent(Guid.NewGuid(), tenant.TenantId!, project.Id, step, now, user.UserKey, step == InstallSteps.Scheduled ? request.ScheduledFor!.Value.ToUniversalTime() : null, note);

        var consented = await channels.ForCustomerAsync(http, project.CustomerId, ct) ?? ["email"];
        var message = MessageFor(project, e, rescheduled: current == InstallSteps.Scheduled);
        var updates = consented.Where(c => c is "email" or "sms" or "call").Distinct()
            .Select(c => new Notification(Guid.NewGuid(), tenant.TenantId!, project.Id, project.CustomerId, c, message, now)).ToList();
        await installs.AddAsync(e, updates, ct);

        var updated = project with { Stage = step == InstallSteps.Done ? ProjectStages.Done : project.Stage, UpdatedAt = now };
        await projects.UpdateAsync(updated, ct);
        return Results.Ok(await ProjectEndpoints.ToDtoAsync(updated, http, catalog, await ProjectEndpoints.QuoteOf(updated, tenant, quotes, ct), config, ct, clock));
    }

    internal static async Task<IResult> NotificationsAsync(Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IInstallStore installs, CancellationToken ct)
    {
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        return Results.Ok((await installs.NotificationsAsync(tenant!.TenantId!, project!.Id, ct)).Select(n => new NotificationDto(n.Channel, n.Message, n.CreatedAt)));
    }

    internal static InstallDto ToDto(IReadOnlyList<InstallEvent> events) => new(
        events.LastOrDefault()?.Step,
        events.LastOrDefault(e => e.Step == InstallSteps.Scheduled)?.ScheduledFor,
        [.. events.Select(e => new InstallEventDto(e.Step, e.At, e.ScheduledFor, e.Note))]);

    private static string Next(string? current) =>
        current is null ? InstallSteps.Scheduled : InstallSteps.All.ElementAtOrDefault(Array.IndexOf(InstallSteps.All, current) + 1) ?? "nothing (done)";

    /// <summary>Plain words, no prices or personal details beyond the project title (it may go out by text).</summary>
    internal static string MessageFor(Project p, InstallEvent e, bool rescheduled) => e.Step switch
    {
        InstallSteps.Scheduled => $"{p.Title}: installation {(rescheduled ? "moved to" : "scheduled for")} {e.ScheduledFor:yyyy-MM-dd}.",
        InstallSteps.Fabricating => $"{p.Title}: your materials are being made.",
        InstallSteps.Installing => $"{p.Title}: installation has started.",
        _ => $"{p.Title}: installation is complete. Thank you!",
    };

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
