using System.Text.Json;
using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

// ---- Projects contract (2n-f). Requests carry answers, never prices. ----
public sealed record CreateProjectRequest(string? Zip, string? Title = null);
public sealed record ProjectAnswersRequest(string? Sku, MeasurementInput? Measurements, GuidedChoiceDto[]? Choices, string? Title = null, string? Zip = null);

public sealed record IssuedQuoteDto(
    Guid Id, int Number, string Status, DateTimeOffset ValidUntil, DateTimeOffset? AcceptedAt, bool Current, bool Expired,
    string Currency, IReadOnlyList<QuoteLineDto> Lines, decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total);

public sealed record ProjectDto(
    Guid Id, string Title, string Zip, string Stage, string? Sku, JsonElement Answers,
    GuidedQuoteDto? Preview, IReadOnlyList<QuoteErrorDto> PreviewErrors, IssuedQuoteDto? Quote, DateTimeOffset UpdatedAt);

public sealed record ProjectSummaryDto(Guid Id, string Title, string Zip, string Stage, int? QuoteNumber, decimal? QuoteTotal, string? Currency, DateTimeOffset UpdatedAt);

/// <summary>
/// The project workspace's API (2n-f). A project holds the customer's answers; every read re-prices
/// them through the same guided pricing as /api/quotes/guided. "Get my quote" issues a numbered quote
/// (Q-1001...) valid for Quotes:ValidityDays (14). Accepting it is the CUSTOMER's act only:
/// staff can't, and an agent can't (requests marked with X-BuilderPro-Agent are refused).
/// </summary>
public static class ProjectEndpoints
{
    public const int ListLimit = 50;
    public const int MaxTitle = 80;

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/projects").RequireAuthorization();
        api.MapPost("/", CreateAsync).WithSummary("Start a project at a ZIP the store serves.");
        api.MapGet("/", ListAsync).WithSummary("My projects (staff: the store's), newest activity first.");
        api.MapGet("/{id:guid}", GetAsync).WithSummary("A project with its answers re-priced now, and its latest issued quote.");
        api.MapPut("/{id:guid}/answers", AnswersAsync).WithSummary("Save the product, measurements and option answers (the workspace and the concierge both use this).");
        api.MapPost("/{id:guid}/quote", IssueAsync).WithSummary("Issue a numbered quote from the current answers (all required steps answered).");
        api.MapPost("/{id:guid}/accept", AcceptAsync).WithSummary("The customer accepts the issued quote: the price is held until it expires. Never an agent.");
        return app;
    }

    internal static async Task<IResult> CreateAsync(
        CreateProjectRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog, IProjectStore projects,
        TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var (tenant, problem) = await GateAsync(http, tenants, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (string.IsNullOrWhiteSpace(request.Zip)) return Problem(StatusCodes.Status400BadRequest, "invalid_zip", "Send the project's zip.");

        var zipCheck = await catalog.GetAsync(http, request.Zip.Trim(), ct);
        if (zipCheck.ZipProblem is "invalid_zip") return Problem(StatusCodes.Status400BadRequest, "invalid_zip", "zip must be a US ZIP (12345) or a Canadian postal code (A1A 1A1).");
        if (zipCheck.ZipProblem is "zip_not_served") return Problem(StatusCodes.Status422UnprocessableEntity, "zip_not_served", "Your store doesn't serve this ZIP yet.");
        if (zipCheck.Products is null) return Problem(StatusCodes.Status503ServiceUnavailable, "catalog_unavailable", "The catalog couldn't be reached.");

        var now = clock.GetUtcNow();
        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var project = new Project(Guid.NewGuid(), tenant!.TenantId!, tenant.CustomerId, user.UserKey, Title(request.Title) ?? "New project",
            request.Zip.Trim(), null, "{}", ProjectStages.Discover, null, now, now);
        await projects.CreateAsync(project, ct);
        return Results.Created($"/api/projects/{project.Id}", await ToDtoAsync(project, http, catalog, null, config: null, ct));
    }

    internal static async Task<IResult> ListAsync(HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, CancellationToken ct)
    {
        var (tenant, problem) = await GateAsync(http, tenants, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        var list = await projects.ListAsync(tenant!.TenantId!, tenant.CustomerId, ListLimit, ct);
        var result = new List<ProjectSummaryDto>();
        foreach (var p in list)
        {
            var q = p.QuoteId is Guid qid ? await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, qid, ct) : null;
            result.Add(new(p.Id, p.Title, p.Zip, p.Stage, q?.Issue?.Number, q?.Priced.Total.Amount, q?.Priced.Total.Currency, p.UpdatedAt));
        }
        return Results.Ok(result);
    }

    internal static async Task<IResult> GetAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, ICatalogPrices catalog,
        IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, project, problem) = await LoadAsync(id, http, tenants, projects, QuoteEndpoints.ReadScope, ct);
        if (problem is not null) return problem;
        return Results.Ok(await ToDtoAsync(project!, http, catalog, await QuoteOf(project!, tenant!, quotes, ct), config, ct, clock));
    }

    internal static async Task<IResult> AnswersAsync(
        Guid id, ProjectAnswersRequest request, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes,
        ICatalogPrices catalog, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, project, problem) = await LoadAsync(id, http, tenants, projects, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (project!.Stage == ProjectStages.Deposit)
            return Problem(StatusCodes.Status409Conflict, "project_locked", "This quote was accepted; its answers can't change any more.");
        if (request.Sku is not null && !ClaimsNormalizer.IsIdentifier(request.Sku))
            return Problem(StatusCodes.Status400BadRequest, "invalid_sku", "sku is not a valid identifier.");

        var answers = JsonSerializer.Serialize(new { measurements = request.Measurements, choices = request.Choices ?? [] }, JsonSerializerOptions.Web);
        var updated = project with
        {
            Title = Title(request.Title) ?? project.Title,
            Zip = string.IsNullOrWhiteSpace(request.Zip) ? project.Zip : request.Zip.Trim(),
            Sku = request.Sku,
            AnswersJson = answers,
            Stage = project.Stage == ProjectStages.Quote ? ProjectStages.Quote : request.Sku is null ? ProjectStages.Discover : ProjectStages.Configure,
            UpdatedAt = clock.GetUtcNow(),
        };
        await projects.UpdateAsync(updated, ct);
        return Results.Ok(await ToDtoAsync(updated, http, catalog, await QuoteOf(updated, tenant!, quotes, ct), config, ct, clock));
    }

    internal static async Task<IResult> IssueAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, ICatalogPrices catalog,
        IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var (tenant, project, problem) = await LoadAsync(id, http, tenants, projects, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (project!.Stage == ProjectStages.Deposit)
            return Problem(StatusCodes.Status409Conflict, "project_locked", "This quote was already accepted.");
        if (project.Sku is null) return Problem(StatusCodes.Status400BadRequest, "incomplete_quote", "Choose a product first.");

        var request = RequestOf(project);
        var outcome = await GuidedQuoteEndpoints.PriceAsync(request, tenant!.TenantId!, http, catalog, config, ct);
        if (outcome.Result is null) return outcome.ToProblem();
        if (!outcome.Result.Complete)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "incomplete_quote", detail: "Answer every required step first.",
                extensions: new Dictionary<string, object?> { ["code"] = "incomplete_quote", ["missing"] = outcome.Result.Missing });
        }

        var now = clock.GetUtcNow();
        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var quote = await quotes.IssueAsync(new Quote(Guid.NewGuid(), tenant.TenantId!, project.CustomerId, user.UserKey, "issued", outcome.Result.Quote!, now,
            new GuidedInfo(project.Zip, RequestJson(request), outcome.Result.LineGroups),
            new QuoteIssue(0, now.AddDays(ValidityDays(config)), project.Id)), ct);

        var updated = project with { QuoteId = quote.Id, Stage = ProjectStages.Quote, UpdatedAt = now };
        await projects.UpdateAsync(updated, ct);
        return Results.Ok(await ToDtoAsync(updated, http, catalog, quote, config, ct, clock));
    }

    internal static async Task<IResult> AcceptAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, ICatalogPrices catalog,
        IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        // An agent calls with the customer's token, so the token can't tell them apart; the marker can.
        if (AgentCall.IsAgent(http))
            return Problem(StatusCodes.Status403Forbidden, "agent_cannot_accept", "Only the customer can accept a quote.");

        var (tenant, project, problem) = await LoadAsync(id, http, tenants, projects, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (tenant!.CustomerId is null || project!.CustomerId != tenant.CustomerId)
            return Problem(StatusCodes.Status403Forbidden, "customer_only", "Only the customer can accept their quote.");

        var quote = await QuoteOf(project, tenant, quotes, ct);
        var now = clock.GetUtcNow();
        if (quote?.Issue is null) return Problem(StatusCodes.Status409Conflict, "no_quote", "Get your quote first.");
        if (quote.Status == "accepted") return Problem(StatusCodes.Status409Conflict, "already_accepted", "This quote is already accepted.");
        if (quote.Guided?.RequestJson != RequestJson(RequestOf(project)))
            return Problem(StatusCodes.Status409Conflict, "quote_outdated", "Your answers changed since this quote. Get an updated quote first.");
        if (quote.Issue.ValidUntil <= now) return Problem(StatusCodes.Status409Conflict, "quote_expired", "This quote expired. Get an updated quote.");
        if (!await quotes.AcceptAsync(tenant.TenantId!, tenant.CustomerId, quote.Id, now, ct))
            return Problem(StatusCodes.Status409Conflict, "quote_not_acceptable", "This quote can't be accepted.");

        var updated = project with { Stage = ProjectStages.Deposit, UpdatedAt = now };
        await projects.UpdateAsync(updated, ct);
        var accepted = await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, quote.Id, ct);
        return Results.Ok(await ToDtoAsync(updated, http, catalog, accepted, config, ct, clock));
    }

    // ---- helpers ----

    private static async Task<(TenantLookup? Tenant, IResult? Problem)> GateAsync(HttpContext http, ITenantLookup tenants, string scope, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return (null, Problem(tenant.Status, tenant.Code, "Could not determine your tenant."));
        if (!tenant.HasScope(scope)) return (null, Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {scope} scope."));
        return (tenant, null);
    }

    private static async Task<(TenantLookup? Tenant, Project? Project, IResult? Problem)> LoadAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, string scope, CancellationToken ct)
    {
        var (tenant, problem) = await GateAsync(http, tenants, scope, ct);
        if (problem is not null) return (null, null, problem);
        var project = await projects.FindAsync(tenant!.TenantId!, tenant.CustomerId, id, ct);
        return project is null ? (null, null, Results.NotFound()) : (tenant, project, null);
    }

    private static Task<Quote?> QuoteOf(Project p, TenantLookup tenant, IQuoteStore quotes, CancellationToken ct) =>
        p.QuoteId is Guid qid ? quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, qid, ct) : Task.FromResult<Quote?>(null);

    private sealed record Answers(MeasurementInput? Measurements, GuidedChoiceDto[]? Choices);

    internal static GuidedQuoteRequest RequestOf(Project p)
    {
        var a = JsonSerializer.Deserialize<Answers>(p.AnswersJson, JsonSerializerOptions.Web) ?? new Answers(null, null);
        return new GuidedQuoteRequest(p.Zip, p.Sku, a.Measurements, a.Choices ?? []);
    }

    /// <summary>The canonical form stored with an issued quote; equal strings = the quote matches the current answers.</summary>
    internal static string RequestJson(GuidedQuoteRequest r) => JsonSerializer.Serialize(r, JsonSerializerOptions.Web);

    private static int ValidityDays(IConfiguration config) => config.GetValue<int?>("Quotes:ValidityDays") ?? 14;

    private static string? Title(string? t) =>
        string.IsNullOrWhiteSpace(t) ? null : t.Trim().Length > MaxTitle ? t.Trim()[..MaxTitle] : t.Trim();

    private static async Task<ProjectDto> ToDtoAsync(
        Project p, HttpContext http, ICatalogPrices catalog, Quote? quote, IConfiguration? config, CancellationToken ct, TimeProvider? clock = null)
    {
        GuidedQuoteDto? preview = null;
        IReadOnlyList<QuoteErrorDto> errors = [];
        var request = RequestOf(p);
        if (p.Sku is not null && config is not null)
        {
            var outcome = await GuidedQuoteEndpoints.PriceAsync(request, p.TenantId, http, catalog, config, ct);
            if (outcome.Result is not null) preview = GuidedQuoteEndpoints.ToDto(outcome.Measurement!, outcome.Result);
            else errors = outcome.Errors;
        }

        IssuedQuoteDto? issued = null;
        if (quote?.Issue is { } issue)
        {
            var dto = QuoteEndpoints.ToDto(quote);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            issued = new IssuedQuoteDto(quote.Id, issue.Number, quote.Status, issue.ValidUntil, issue.AcceptedAt,
                Current: quote.Guided?.RequestJson == RequestJson(request), Expired: quote.Status == "issued" && issue.ValidUntil <= now,
                dto.Currency, dto.Lines, dto.Subtotal, dto.TaxRate, dto.Tax, dto.Total);
        }

        using var answers = JsonDocument.Parse(p.AnswersJson);
        return new ProjectDto(p.Id, p.Title, p.Zip, p.Stage, p.Sku, answers.RootElement.Clone(), preview, errors, issued, p.UpdatedAt);
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
