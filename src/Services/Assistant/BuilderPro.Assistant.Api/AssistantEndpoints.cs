using System.ClientModel;
using BuilderPro.Security;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace BuilderPro.Assistant.Api;

public sealed record ChatTurn(string Role, string Content);
public sealed record ChatRequest(ChatTurn[]? Messages);
public sealed record ChatReply(string Reply, bool Guarded, IReadOnlyList<string> ToolsUsed);

public static class AssistantEndpoints
{
    public const int MaxMessages = 20;
    public const int MaxChars = 2000;

    /// <summary>
    /// The agent's standing instructions. The price rule is repeated here AND enforced by PriceGuard:
    /// the instruction makes good answers likely, the guard makes bad ones impossible to deliver.
    /// </summary>
    public const string Instructions = """
        You are the product assistant of a home-improvement store on the BuilderPro platform.
        You help customers choose products the store sells: countertops, roofing, sheds, flooring,
        decking, cabinets, paint, windows and doors, fencing, and installation labour.

        Rules:
        - Only talk about products returned by your tools. If the store doesn't sell something, say so.
        - NEVER calculate, estimate, round or invent money. For any price, total or cost, call
          price_products and repeat the amounts exactly as returned.
        - Catalog prices are per unit and BEFORE tax. Only price_products returns tax and totals.
          Never say that a unit price includes tax.
        - For roofs, call estimate_roof for quantities first, then price_products with those quantities.
        - You are read-only: you cannot create or change quotes, place orders, book visits or promise
          delivery dates or discounts. Never offer to. To save a quote, tell the customer to use
          "Add to quote" in the catalog.
        - Write plain text for a chat bubble: short sentences and "- " bullet lists only. No tables,
          headings, bold or other Markdown.
        - Keep answers short and practical. Use SKUs only when helpful.
        """;

    public const string GuardedReply =
        "I can't confirm that price right now. Please check the product's price in the catalog, or ask me to price specific products and quantities.";

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/assistant/chat", ChatAsync)
            .RequireAuthorization()
            .WithSummary("Ask the read-only product assistant. Prices in replies always come from platform tools (3a).");
        return app;
    }

    internal static async Task<IResult> ChatAsync(
        ChatRequest request, HttpContext http, ITenantLookup tenants, IPlatformApi api, IAssistantChatClient chat,
        ILogger<ChatRequest> log, CancellationToken ct)
    {
        var turns = request.Messages ?? [];
        if (turns.Length is 0 or > MaxMessages || turns[^1].Role != "user"
            || turns.Any(t => t.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(t.Content) || t.Content.Length > MaxChars))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_chat", $"Send 1-{MaxMessages} messages (user/assistant, up to {MaxChars} characters), ending with the user's.");
        }

        // Same gate as every other service: a member of a store, or the platform's refusal.
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your store.");

        if (chat.Client is null) return Problem(StatusCodes.Status503ServiceUnavailable, "assistant_unavailable", "The assistant isn't configured.");

        var ledger = new PriceLedger();
        var agent = new ChatClientAgent(chat.Client, Instructions, "builderpro-assistant", "Read-only product assistant", new PlatformTools(api, ledger).All());
        var messages = turns.Select(t => new ChatMessage(t.Role == "user" ? ChatRole.User : ChatRole.Assistant, t.Content)).ToList();

        string reply;
        try
        {
            var response = await agent.RunAsync(messages, cancellationToken: ct);
            reply = (response.Text ?? "").Trim();
        }
        catch (ClientResultException ex) when (ex.Status == StatusCodes.Status429TooManyRequests)
        {
            // The model's quota (tokens or requests per minute) is used up: say so instead of a 500.
            log.LogWarning("The AI model is rate limiting the assistant (HTTP 429).");
            return Problem(StatusCodes.Status503ServiceUnavailable, "assistant_busy", "The AI model is busy right now. Try again in a minute.");
        }
        catch (ClientResultException ex)
        {
            log.LogError(ex, "The AI model call failed with HTTP {Status}.", ex.Status);
            return Problem(StatusCodes.Status502BadGateway, "assistant_model_error", "The AI model couldn't answer. Try again later.");
        }

        var unbacked = PriceGuard.Unbacked(reply, ledger.Amounts);
        if (unbacked.Count > 0)
        {
            log.LogWarning("Price guard blocked a reply with amounts no tool returned: {Amounts}", unbacked);
            return Results.Ok(new ChatReply(GuardedReply, Guarded: true, ledger.ToolsUsed));
        }
        return Results.Ok(new ChatReply(reply, Guarded: false, ledger.ToolsUsed));
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>The model connection, or null when Foundry isn't configured (the endpoint then answers 503).</summary>
public interface IAssistantChatClient
{
    IChatClient? Client { get; }
}

public sealed record AssistantChatClient(IChatClient? Client) : IAssistantChatClient;
