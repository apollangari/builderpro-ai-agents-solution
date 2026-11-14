using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace BuilderPro.Assistant.Api;

/// <summary>
/// 3c: BuilderPro's catalog and quote tools over the Model Context Protocol, at /api/assistant/mcp
/// (Streamable HTTP, stateless). Any MCP client (Claude Desktop, VS Code, another agent) can list and
/// call them with the CUSTOMER'S OWN bearer token: every call reaches Catalog and Quotes as that
/// customer, so tenant isolation and scopes are exactly those of the signed-in person.
///
/// Read and price only. There is no tool that accepts a quote, pays, books or changes a project:
/// those stay the customer's own clicks, and the APIs refuse agent calls anyway (X-BuilderPro-Agent).
/// The same code as the concierge's tools (one behaviour, two front doors).
/// </summary>
[McpServerToolType]
public sealed class McpTools(IPlatformApi api)
{
    private ConciergeTools Tools() => new(api, new PriceLedger(), new DraftLedger());

    [McpServerTool(Name = "check_zip", ReadOnly = true), Description("Checks whether the store serves a project ZIP / postal code. Returns {zip, served}.")]
    public Task<JsonElement> CheckZip(string zip, CancellationToken ct) => Tools().CheckZipAsync(zip, ct);

    [McpServerTool(Name = "list_project_types", ReadOnly = true), Description("The project types (categories) the store sells in this ZIP, with how many products each.")]
    public Task<JsonElement> ListProjectTypes(string zip, CancellationToken ct) => Tools().ListProjectTypesAsync(zip, ct);

    [McpServerTool(Name = "find_products", ReadOnly = true), Description("Products of one project type sold in this ZIP, at this ZIP's price per unit (before tax). Optional words narrow by name.")]
    public Task<JsonElement> FindProducts(
        string zip,
        [Description("Category id from list_project_types, e.g. countertops")] string category,
        [Description("Words in the product name, e.g. quartz white. Omit for all.")] string? query,
        CancellationToken ct) => Tools().FindProductsAsync(zip, category, query, ct);

    [McpServerTool(Name = "get_project_steps", ReadOnly = true), Description("What to ask for one product in this ZIP: its measure kind, the store's minimum, and the option steps with their options and prices.")]
    public Task<JsonElement> GetProjectSteps(string zip, string sku, CancellationToken ct) => Tools().GetProjectStepsAsync(zip, sku, ct);

    [McpServerTool(Name = "price_draft", ReadOnly = true, Idempotent = true),
     Description("Prices a draft quote with the store's pricing engine (measured quantities, grouped lines, tax, total, missing steps). Saves nothing.")]
    public Task<JsonElement> PriceDraft(
        string zip, string sku, DraftMeasurements measurements,
        [Description("Answers so far, one per single step (sku or null for its none choice); quantity steps: one per option with its count.")] DraftChoice[]? choices,
        CancellationToken ct) => Tools().PriceDraftAsync(zip, sku, measurements, choices, ct);

    [McpServerTool(Name = "list_my_projects", ReadOnly = true), Description("My projects: title, ZIP, stage, latest quote number and total.")]
    public async Task<JsonElement> ListMyProjects(CancellationToken ct) => await api.GetAsync("/api/projects", ct);

    [McpServerTool(Name = "get_project", ReadOnly = true),
     Description("One of my projects: its answers re-priced now, its latest numbered quote (with whether the price is held by a paid deposit), deposit, visit and installation progress.")]
    public async Task<JsonElement> GetProject([Description("Project id from list_my_projects")] Guid projectId, CancellationToken ct) =>
        await api.GetAsync($"/api/projects/{projectId}", ct);
}
