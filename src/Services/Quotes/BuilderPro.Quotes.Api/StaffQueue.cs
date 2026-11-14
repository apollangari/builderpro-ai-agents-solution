using System.Text.Json;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

public sealed record QueueItemDto(
    Guid ProjectId, string Title, string? CustomerId, string Stage, string Kind, string Detail,
    DateTimeOffset? VisitStart, string? InstallStep, Guid? CallId, JsonElement Measurements, DateTimeOffset UpdatedAt);

/// <summary>
/// 3e: the store desk's work queue for staff with scheduling.write. What needs a person now:
///   measure  - a booked visit to measure and record           (POST .../visit/measured)
///   install  - an installation to schedule or move on          (POST .../install)
///   call     - a scheduling call where the customer asked for a person, or none of the times fitted
/// Oldest first within each kind. Read-only: the actions are the existing endpoints, with their own rules.
/// </summary>
public static class StaffQueue
{
    public static IEndpointRouteBuilder MapStaffQueue(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/staff/queue", QueueAsync).RequireAuthorization()
            .WithSummary("Store desk: visits to measure, installations to move on, calls that need a person (scheduling.write).");
        return app;
    }

    internal static async Task<IResult> QueueAsync(
        HttpContext http, ITenantLookup tenants, IProjectStore projects, IVisitStore visits, IInstallStore installs, ICallStore calls, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Results.Problem(statusCode: tenant.Status, title: tenant.Code);
        if (tenant.CustomerId is not null || !tenant.HasScope(VisitEndpoints.SchedulingScope))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "staff_only", extensions: new Dictionary<string, object?> { ["code"] = "staff_only" });

        var items = new List<QueueItemDto>();
        foreach (var p in await projects.ListAsync(tenant.TenantId!, null, 500, ct))
        {
            using var answers = JsonDocument.Parse(p.AnswersJson);
            var measurements = answers.RootElement.TryGetProperty("measurements", out var m) ? m.Clone() : default;
            QueueItemDto Item(string kind, string detail, DateTimeOffset? visit = null, string? step = null, Guid? call = null) =>
                new(p.Id, p.Title, p.CustomerId, p.Stage, kind, detail, visit, step, call, measurements, p.UpdatedAt);

            if (p.Stage == ProjectStages.Measure)
            {
                var visit = await visits.ForProjectAsync(p.TenantId, p.Id, ct);
                if (visit?.Status == VisitStatus.Booked) items.Add(Item("measure", "Measure visit booked: record the measured sizes.", visit.Start));
                foreach (var c in (await calls.ForProjectAsync(p.TenantId, p.Id, ct)).Where(c => c.Status == CallStatus.PersonRequested
                             || (c.Status == CallStatus.Cancelled && c.Note?.Contains("staff to follow up", StringComparison.Ordinal) == true)))
                    items.Add(Item("call", c.Status == CallStatus.PersonRequested ? "Asked for a person on the scheduling call: call them back." : "No open visit time fitted their hours: call them.", call: c.Id));
            }
            else if (p.Stage == ProjectStages.Install)
            {
                var step = (await installs.ForProjectAsync(p.TenantId, p.Id, ct)).LastOrDefault()?.Step;
                items.Add(Item("install", step is null ? "Measured and confirmed: schedule the installation." : $"Installation {step}: move it on when ready.", step: step));
            }
        }
        string[] order = ["call", "measure", "install"];
        return Results.Ok(items.OrderBy(i => Array.IndexOf(order, i.Kind)).ThenBy(i => i.UpdatedAt));
    }
}
