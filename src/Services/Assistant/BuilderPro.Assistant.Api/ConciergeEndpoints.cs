using System.Text.Json;
using BuilderPro.Security;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace BuilderPro.Assistant.Api;

/// <summary>
/// The page sends the conversation, the ZIP from the customer's account (if any) and the current
/// draft (the last guided request the concierge priced), so the server keeps no chat state.
/// </summary>
public sealed record ConciergeRequest(ChatTurn[]? Messages, string? AccountZip = null, JsonElement? Draft = null, Guid? ProjectId = null,
    CatalogContext? Catalog = null);

/// <summary>Draft + Quote are set when the concierge priced a draft this turn; the page keeps the last ones.</summary>
public sealed record ConciergeReply(string Reply, bool Guarded, IReadOnlyList<string> ToolsUsed, JsonElement? Draft, JsonElement? Quote,
    bool ProjectChanged = false, CatalogFilterDto? Filter = null);

/// <summary>
/// The concierge (3b): walks the customer through the guided quote (ZIP, project type, product,
/// measurements, options, protection) in conversation and prices a DRAFT with the same pricing
/// engine as the quote builder. It never saves, accepts, pays or books: the page's "Save quote"
/// button does that, pressed by the customer. Prices in its replies pass the same price guard as 3a.
/// </summary>
public static class ConciergeEndpoints
{
    public const int MaxDraftChars = 8000;

    public const string Instructions = """
        You are the concierge of a home-improvement store on the BuilderPro platform. You help a
        customer put together a quote for one project, step by step, in a friendly, short way.

        The steps, in order (skip what you already know):
        1. ZIP: if the customer's account ZIP is given, ask "Is this project at ZIP <zip>?". Call
           check_zip. If it isn't served, say so and ask for the ZIP where the work will happen.
        2. Project type: call list_project_types and offer the types by name.
        3. Product: call find_products; suggest at most 3 by name with their price per unit.
        4. Measurements: call get_project_steps for the chosen product. Its "measure" says what to ask:
           countertop = each countertop section's width (feet and inches) and depth (inches, standard 25),
           any island and backsplash, and the layout; roof = footprint sq ft and pitch; area = each
           room's length and width in feet; count = how many. Ask in plain words, one thing at a time.
        5. Options, then protection plan: ask each required step from get_project_steps, offering the
           options by name and price, and its none choice when it has one.
        6. Call price_draft as soon as you have product + measurements, and again after every answer.
           Tell the customer the measured quantities and the total exactly as price_draft returns them,
           and which steps are still missing.

        Rules:
        - NEVER calculate money, areas, lengths or quantities yourself. Pass feet and inches to
          price_draft as given; it converts and measures. Repeat amounts exactly as tools return them.
        - Prices per unit are before tax; only price_draft returns tax and totals.
        - You cannot save, accept, pay, book a visit, or promise dates or discounts. When nothing is
          missing, tell the customer to review the draft beside the chat and press "Save quote".
        - Only products and options returned by your tools exist.
        - Plain text for a chat bubble: short sentences, "- " lists. No tables, headings or bold.
        """;

    /// <summary>Help chat (1h): for stores whose package has no concierge. Answers only.</summary>
    public const string HelpInstructions = """
        You are the help chat of a home-improvement store on the BuilderPro platform. The customer is working
        through their project page step by step; the current project is given as context.
        - Answer their questions about the products, the steps (measuring, options, protection) and the
          quote, using find_products and get_project_steps. Repeat prices exactly as tools return them;
          prices per unit are before tax.
        - You can't change anything: you don't fill in steps, get quotes, accept or pay. Tell the customer
          which step on the page to use instead.
        - Plain text for a chat bubble: short sentences, "- " lists. No tables, headings or bold.
        """;

    private static IResult NotIncluded(string detail) =>
        AssistantEndpoints.Problem(StatusCodes.Status403Forbidden, "feature_not_included", detail);

    /// <summary>Added in a project workspace (2n-f): the project replaces the page-held draft.</summary>
    public const string ProjectInstructions = """
        The customer is in their project workspace; the project already has its ZIP. The current project
        (answers and its priced preview) is given as context.
        - Use save_answers instead of price_draft: send the product sku, ALL measurements and ALL answers
          so far every time; it saves them and returns the project re-priced. Report totals exactly as returned.
        - When nothing is missing, you may call issue_quote: it gives the quote a number and a validity date.
        - You can NEVER accept the quote. Tell the customer to review it and press "Accept quote" themselves.
        """;

    /// <summary>Catalog mode (owner request): help pick a product of one type in one ZIP by filtering what's shown.</summary>
    public const string CatalogInstructions = """
        You are the concierge of a home-improvement store on the BuilderPro platform. The customer is looking at
        one project type's products for their ZIP, shown beside this chat, and wants help choosing.
        - When they describe what they want (material, colour, finish, size, budget...), call filter_catalog with
          matching attribute keys from the category (given as context) and/or a price range, then say briefly what
          you found: how many, and up to 3 names with their price per unit exactly as returned.
        - To show everything again, call filter_catalog with no arguments.
        - Answer questions about the products from what filter_catalog returns. Never invent products or prices.
          Prices per unit are before tax.
        - You don't start projects or quotes: the customer presses "Choose" on the product they want.
        - Plain text for a chat bubble: short sentences, "- " lists. No tables, headings or bold.
        """;

    private static async Task<IResult> CatalogChatAsync(
        CatalogContext catalog, ChatTurn[] turns, IPlatformApi api, IChatClient client, PriceLedger ledger, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(catalog.Zip) || catalog.Zip.Length > 12 || !BuilderPro.Security.ClaimsNormalizer.IsIdentifier(catalog.Category))
            return AssistantEndpoints.Problem(StatusCodes.Status400BadRequest, "invalid_chat", "Send the ZIP and project type being browsed.");

        var tools = new CatalogFilterTools(api, ledger, catalog);
        var agent = new ChatClientAgent(client, CatalogInstructions, "builderpro-concierge", "Catalog helper", tools.All());

        // Context: the category's attribute keys and choices, so the model filters with real keys.
        var categories = await api.GetAsync("/api/catalog/categories", ct);
        var category = categories.ValueKind == JsonValueKind.Array
            ? categories.EnumerateArray().FirstOrDefault(c => c.GetProperty("id").GetString() == catalog.Category)
            : default;
        var context = $"Browsing: category {catalog.Category} in ZIP {catalog.Zip}. Category attributes: "
            + (category.ValueKind == JsonValueKind.Object && category.TryGetProperty("attributes", out var attrs) ? attrs.GetRawText() : "unknown");
        var messages = new List<ChatMessage> { new(ChatRole.System, context) };
        messages.AddRange(turns.Select(t => new ChatMessage(t.Role == "user" ? ChatRole.User : ChatRole.Assistant, t.Content)));

        var (reply, failed) = await AssistantEndpoints.RunAgentAsync(agent, messages, log, ct);
        if (failed is not null) return failed;
        var unbacked = PriceGuard.Unbacked(reply, ledger.Amounts);
        if (unbacked.Count > 0)
        {
            log.LogWarning("Price guard blocked a catalog reply with amounts no tool returned: {Amounts}", unbacked);
            return Results.Ok(new ConciergeReply(AssistantEndpoints.GuardedReply, true, ledger.ToolsUsed, null, null, false, tools.Filter));
        }
        return Results.Ok(new ConciergeReply(reply, false, ledger.ToolsUsed, null, null, false, tools.Filter));
    }

    public static IEndpointRouteBuilder MapConciergeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/assistant/concierge", ChatAsync)
            .RequireAuthorization()
            .WithSummary("Talk to the concierge: it builds and prices a draft guided quote; the customer saves it (3b).");
        return app;
    }

    internal static async Task<IResult> ChatAsync(
        ConciergeRequest request, HttpContext http, ITenantLookup tenants, IPlatformApi api, IAssistantChatClient chat,
        ILogger<ConciergeRequest> log, CancellationToken ct)
    {
        var turns = request.Messages ?? [];
        if (AssistantEndpoints.InvalidTurns(turns) is { } invalid) return invalid;
        if (request.AccountZip is { Length: > 12 } || (request.Draft is { } d && d.GetRawText().Length > MaxDraftChars))
            return AssistantEndpoints.Problem(StatusCodes.Status400BadRequest, "invalid_chat", "The account ZIP or draft is too large.");

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return AssistantEndpoints.Problem(tenant.Status, tenant.Code, "Could not determine your store.");
        if (chat.Client is null) return AssistantEndpoints.Problem(StatusCodes.Status503ServiceUnavailable, "assistant_unavailable", "The assistant isn't configured.");

        // The store's package (1h): the concierge acts only where it's included; a help chat only answers.
        var concierge = tenant.HasFeature(StoreFeatures.Concierge);
        var help = tenant.HasFeature(StoreFeatures.HelpChat);
        if (!concierge && !help) return NotIncluded("Chat isn't part of your store's package.");
        if (request.Catalog is null && request.ProjectId is null && !concierge) return NotIncluded("The concierge isn't part of your store's package.");

        var ledger = new PriceLedger();
        if (request.Catalog is { } catalog) return await CatalogChatAsync(catalog, turns, api, chat.Client, ledger, log, ct);
        var drafts = new DraftLedger();
        var helpOnly = !concierge;
        var tools = new ConciergeTools(api, ledger, drafts, request.ProjectId, helpOnly, http.RequestServices.GetService<ISchedulingAgent>());
        var instructions = helpOnly ? HelpInstructions
            : request.ProjectId is null ? Instructions : Instructions + "\n" + ProjectInstructions;
        var agent = new ChatClientAgent(chat.Client, instructions, "builderpro-concierge", "Guided quote concierge", tools.All());

        // What the page knows, as context (not as instructions from the customer).
        string context;
        if (request.ProjectId is Guid projectId)
        {
            // The project as the customer may see it (their token); its prices count as tool output for the guard.
            var project = await api.GetAsync($"/api/projects/{projectId}", ct);
            if (project.ValueKind != JsonValueKind.Object || !project.TryGetProperty("id", out _))
                return AssistantEndpoints.Problem(StatusCodes.Status404NotFound, "project_not_found", "That project isn't yours or doesn't exist.");
            ledger.RecordContext(project);
            context = $"Current project: {project.GetRawText()}";
        }
        else
        {
            context = $"Customer's account ZIP: {(string.IsNullOrWhiteSpace(request.AccountZip) ? "unknown" : request.AccountZip)}.\n"
                + $"Current draft (answers so far, priced by price_draft): {(request.Draft is { } draft ? draft.GetRawText() : "none yet")}";
        }
        var messages = new List<ChatMessage> { new(ChatRole.System, context) };
        messages.AddRange(turns.Select(t => new ChatMessage(t.Role == "user" ? ChatRole.User : ChatRole.Assistant, t.Content)));

        var (reply, failed) = await AssistantEndpoints.RunAgentAsync(agent, messages, log, ct);
        if (failed is not null) return failed;

        var unbacked = PriceGuard.Unbacked(reply, ledger.Amounts);
        if (unbacked.Count > 0)
        {
            log.LogWarning("Price guard blocked a concierge reply with amounts no tool returned: {Amounts}", unbacked);
            return Results.Ok(new ConciergeReply(AssistantEndpoints.GuardedReply, true, ledger.ToolsUsed, drafts.Request, drafts.Quote, tools.ProjectChanged));
        }
        return Results.Ok(new ConciergeReply(reply, false, ledger.ToolsUsed, drafts.Request, drafts.Quote, tools.ProjectChanged));
    }
}
