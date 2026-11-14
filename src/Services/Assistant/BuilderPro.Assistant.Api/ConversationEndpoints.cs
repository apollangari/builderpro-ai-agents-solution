using System.Security.Claims;
using BuilderPro.Security;

namespace BuilderPro.Assistant.Api;

public sealed record ConversationSummaryDto(Guid Id, string CustomerId, Guid? ProjectId, string Mode, string? StaffName, DateTimeOffset UpdatedAt, string? LastMessage, string? LastRole);
public sealed record MessageDto(long Seq, string Role, string Text, string? Author, DateTimeOffset At);
public sealed record ThreadDto(Guid Id, string Mode, string? StaffName, IReadOnlyList<MessageDto> Messages);
public sealed record StaffReplyRequest(string? Text);

/// <summary>
/// 3e: a person can take over. Store staff with the "support.chat" scope see their store's recent
/// conversations, take one over (the concierge pauses), reply in the same thread under their name, and hand
/// it back. The customer's page polls GET .../messages?after= and always sees who is answering.
///
/// Rules: staff only ever see their own store's conversations; only one person holds a conversation at a
/// time (a conditional switch); an agent's call can never act as staff (X-BuilderPro-Agent refused).
/// </summary>
public static class ConversationEndpoints
{
    public const string SupportScope = "support.chat";
    public const int MaxText = 2000;
    public static readonly TimeSpan Recent = TimeSpan.FromDays(2);

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/assistant/conversations").RequireAuthorization();
        api.MapGet("/", ListAsync).WithSummary("Staff: this store's conversations from the last two days, newest first.");
        api.MapGet("/{id:guid}/messages", MessagesAsync).WithSummary("The thread (after= for only new messages): the customer's own, or staff of the store.");
        api.MapPost("/{id:guid}/takeover", TakeOverAsync).WithSummary("Staff: take the conversation over; the concierge pauses.");
        api.MapPost("/{id:guid}/messages", ReplyAsync).WithSummary("Staff: reply in the thread under your name (after taking it over).");
        api.MapPost("/{id:guid}/release", ReleaseAsync).WithSummary("Staff: hand the conversation back to the concierge.");
        return app;
    }

    internal static async Task<IResult> ListAsync(HttpContext http, ITenantLookup tenants, IConversationStore store, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, problem) = await StaffAsync(http, tenants, ct);
        if (problem is not null) return problem;
        var list = await store.ListAsync(tenant!.TenantId!, clock.GetUtcNow() - Recent, 100, ct);
        var result = new List<ConversationSummaryDto>();
        foreach (var c in list)
        {
            var last = (await store.MessagesAsync(c.TenantId, c.Id, 0, 500, ct)).LastOrDefault();
            result.Add(new(c.Id, c.CustomerId, c.ProjectId, c.Mode, c.StaffName, c.UpdatedAt, last?.Text is { Length: > 140 } t ? t[..140] + "…" : last?.Text, last?.Role));
        }
        return Results.Ok(result);
    }

    internal static async Task<IResult> MessagesAsync(Guid id, long? after, HttpContext http, ITenantLookup tenants, IConversationStore store, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return AssistantEndpoints.Problem(tenant.Status, tenant.Code, "Could not determine your store.");
        // The customer sees their own; staff with support.chat see their store's. Nobody else.
        if (tenant.CustomerId is null && !tenant.HasScope(SupportScope)) return AssistantEndpoints.Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {SupportScope} scope.");
        var c = await store.FindAsync(tenant.TenantId!, tenant.CustomerId, id, ct);
        if (c is null) return Results.NotFound();
        return Results.Ok(await ThreadAsync(store, c, after ?? 0, ct));
    }

    internal static async Task<IResult> TakeOverAsync(Guid id, HttpContext http, ITenantLookup tenants, IConversationStore store, AuthOptions auth, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, problem) = await StaffAsync(http, tenants, ct);
        if (problem is not null) return problem;
        var c = await store.FindAsync(tenant!.TenantId!, null, id, ct);
        if (c is null) return Results.NotFound();
        var (key, name) = Who(http, auth);
        if (c.Mode == ConversationModes.Staff)
            return c.StaffKey == key ? Results.Ok(await ThreadAsync(store, c, 0, ct))
                : AssistantEndpoints.Problem(StatusCodes.Status409Conflict, "taken", $"{c.StaffName} is already answering.");
        var now = clock.GetUtcNow();
        if (!await store.SetModeAsync(c.TenantId, c.Id, ConversationModes.Concierge, ConversationModes.Staff, key, name, now, ct))
            return AssistantEndpoints.Problem(StatusCodes.Status409Conflict, "taken", "Someone else just took this conversation.");
        await store.AppendAsync(c.TenantId, c.Id, MessageRoles.System, $"{name} from the store has joined; the concierge is paused.", name, now, ct);
        return Results.Ok(await ThreadAsync(store, (await store.FindAsync(c.TenantId, null, c.Id, ct))!, 0, ct));
    }

    internal static async Task<IResult> ReplyAsync(Guid id, StaffReplyRequest request, HttpContext http, ITenantLookup tenants, IConversationStore store, AuthOptions auth, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, problem) = await StaffAsync(http, tenants, ct);
        if (problem is not null) return problem;
        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxText) return AssistantEndpoints.Problem(StatusCodes.Status400BadRequest, "invalid_message", $"Send 1-{MaxText} characters.");
        var c = await store.FindAsync(tenant!.TenantId!, null, id, ct);
        if (c is null) return Results.NotFound();
        var (key, name) = Who(http, auth);
        if (c.Mode != ConversationModes.Staff || c.StaffKey != key)
            return AssistantEndpoints.Problem(StatusCodes.Status409Conflict, "not_yours", "Take the conversation over first.");
        await store.AppendAsync(c.TenantId, c.Id, MessageRoles.Staff, text, name, clock.GetUtcNow(), ct);
        return Results.Ok(await ThreadAsync(store, c, 0, ct));
    }

    internal static async Task<IResult> ReleaseAsync(Guid id, HttpContext http, ITenantLookup tenants, IConversationStore store, AuthOptions auth, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, problem) = await StaffAsync(http, tenants, ct);
        if (problem is not null) return problem;
        var c = await store.FindAsync(tenant!.TenantId!, null, id, ct);
        if (c is null) return Results.NotFound();
        var (key, name) = Who(http, auth);
        if (c.Mode != ConversationModes.Staff || c.StaffKey != key)
            return AssistantEndpoints.Problem(StatusCodes.Status409Conflict, "not_yours", "Only the person answering can hand it back.");
        var now = clock.GetUtcNow();
        await store.SetModeAsync(c.TenantId, c.Id, ConversationModes.Staff, ConversationModes.Concierge, null, null, now, ct);
        await store.AppendAsync(c.TenantId, c.Id, MessageRoles.System, $"{name} handed you back to the concierge.", name, now, ct);
        return Results.Ok(await ThreadAsync(store, (await store.FindAsync(c.TenantId, null, c.Id, ct))!, 0, ct));
    }

    internal static async Task<ThreadDto> ThreadAsync(IConversationStore store, Conversation c, long after, CancellationToken ct) =>
        new(c.Id, c.Mode, c.StaffName, [.. (await store.MessagesAsync(c.TenantId, c.Id, after, 200, ct)).Select(m => new MessageDto(m.Seq, m.Role, m.Text, m.Author, m.At))]);

    /// <summary>Staff of the resolved store with the support scope; never an agent acting as staff.</summary>
    private static async Task<(TenantLookup? Tenant, IResult? Problem)> StaffAsync(HttpContext http, ITenantLookup tenants, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return (null, AssistantEndpoints.Problem(StatusCodes.Status403Forbidden, "staff_only", "Only a person can answer as staff."));
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return (null, AssistantEndpoints.Problem(tenant.Status, tenant.Code, "Could not determine your store."));
        if (tenant.CustomerId is not null || !tenant.HasScope(SupportScope))
            return (null, AssistantEndpoints.Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs store staff with the {SupportScope} scope."));
        return (tenant, null);
    }

    /// <summary>The staff member's stable key and the name the customer sees.</summary>
    private static (string Key, string Name) Who(HttpContext http, AuthOptions auth)
    {
        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var p = http.User;
        var name = p.FindFirst("given_name")?.Value ?? p.FindFirst("name")?.Value ?? user.UserName ?? "Store staff";
        return (user.UserKey, name.Length > 40 ? name[..40] : name);
    }
}
