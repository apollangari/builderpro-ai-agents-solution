using System.Text.Json;
using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

// ---- Projects contract (2n-f). Requests carry answers, never prices. ----
/// <summary>CustomerId: a salesperson (1i) starts the project on that customer's behalf; customers leave it out.</summary>
public sealed record CreateProjectRequest(string? Zip, string? Title = null, string? CustomerId = null);
public sealed record ProjectAnswersRequest(string? Sku, MeasurementInput? Measurements, GuidedChoiceDto[]? Choices, string? Title = null, string? Zip = null);

public sealed record IssuedQuoteDto(
    Guid Id, int Number, string Status, DateTimeOffset ValidUntil, DateTimeOffset? AcceptedAt, bool Current, bool Expired,
    string Currency, IReadOnlyList<QuoteLineDto> Lines, decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total,
    bool Held = false);   // 6a: a paid deposit holds this quote's price

public sealed record ProjectDto(
    Guid Id, string Title, string Zip, string Stage, string? Sku, JsonElement Answers,
    GuidedQuoteDto? Preview, IReadOnlyList<QuoteErrorDto> PreviewErrors, IssuedQuoteDto? Quote, DateTimeOffset UpdatedAt,
    DepositDto? Deposit = null, string? PreparedBy = null,   // 1i: the salesperson who prepared it
    VisitDto? Visit = null,                                   // 6b: the measure visit
    InstallDto? Install = null);                              // 6c: installation steps

public sealed record ProjectSummaryDto(Guid Id, string Title, string Zip, string Stage, int? QuoteNumber, decimal? QuoteTotal, string? Currency, DateTimeOffset UpdatedAt,
    string? PreparedBy = null, string? CustomerId = null);

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
        api.MapPost("/{id:guid}/accept", AcceptAsync).WithSummary("The customer accepts the issued quote; the 5 % deposit is then due. Accepting alone doesn't hold the price (6a). Never an agent.");
        api.MapVisitEndpoints();   // 6b: measure visit, measured quote, confirm
        api.MapInstallEndpoints(); // 6c: installation steps and order updates
        api.MapCallEndpoints();    // 4b: automated scheduling calls (consent, status)
        api.MapPost("/{id:guid}/deposit", DepositEndpoints.StartAsync).WithSummary("Start the 5 % deposit for the accepted quote: returns the amount and where to pay. Customer only, never an agent.");
        return app;
    }

    internal static async Task<IResult> CreateAsync(
        CreateProjectRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog, IProjectStore projects,
        ICustomerDirectory customers, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var (tenant, problem) = await GateAsync(http, tenants, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (string.IsNullOrWhiteSpace(request.Zip)) return Problem(StatusCodes.Status400BadRequest, "invalid_zip", "Send the project's zip.");

        // 1i: a salesperson starts it FOR a customer of this store. Accepting and paying stay the customer's.
        var customerId = tenant!.CustomerId;
        var onBehalf = request.CustomerId is not null;
        if (onBehalf)
        {
            if (tenant.CustomerId is not null || !tenant.HasScope(SalesEndpoints.SalesScope))
                return Problem(StatusCodes.Status403Forbidden, "sales_only", "Only a salesperson can start a project for a customer.");
            if (!ClaimsNormalizer.IsIdentifier(request.CustomerId)) return Problem(StatusCodes.Status400BadRequest, "invalid_customer", "customerId is not a valid identifier.");
            var known = await customers.IsCustomerAsync(http, request.CustomerId!, ct);
            if (known is null) return Problem(StatusCodes.Status503ServiceUnavailable, "identity_unavailable", "Customers couldn't be checked right now.");
            if (known is false) return Problem(StatusCodes.Status404NotFound, "customer_not_found", "No such customer in this store.");
            customerId = request.CustomerId;
        }

        var zipCheck = await catalog.GetAsync(http, request.Zip.Trim(), ct);
        if (zipCheck.ZipProblem is "invalid_zip") return Problem(StatusCodes.Status400BadRequest, "invalid_zip", "zip must be a US ZIP (12345) or a Canadian postal code (A1A 1A1).");
        if (zipCheck.ZipProblem is "zip_not_served") return Problem(StatusCodes.Status422UnprocessableEntity, "zip_not_served", "Your store doesn't serve this ZIP yet.");
        if (zipCheck.Products is null) return Problem(StatusCodes.Status503ServiceUnavailable, "catalog_unavailable", "The catalog couldn't be reached.");

        var now = clock.GetUtcNow();
        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var project = new Project(Guid.NewGuid(), tenant.TenantId!, customerId, user.UserKey, Title(request.Title) ?? "New project",
            request.Zip.Trim(), null, "{}", ProjectStages.Discover, null, now, now,
            SalespersonKey: onBehalf ? user.UserKey : null, SalespersonName: onBehalf ? SalesEndpoints.NameOf(http.User, user) : null);
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
            result.Add(new(p.Id, p.Title, p.Zip, p.Stage, q?.Issue?.Number, q?.Priced.Total.Amount, q?.Priced.Total.Currency, p.UpdatedAt,
                p.SalespersonName, tenant.CustomerId is null ? p.CustomerId : null));
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
        ICatalogPrices catalog, IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var (tenant, project, problem) = await LoadAsync(id, http, tenants, projects, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (StaffCannotWrite(tenant!, project!, http, auth)) return NotYourCustomer();
        if (ProjectStages.IsLocked(project!.Stage))
            return Problem(StatusCodes.Status409Conflict, "project_locked", "Your deposit is paid; the measure visit confirms the details from here.");
        if (request.Sku is not null && !ClaimsNormalizer.IsIdentifier(request.Sku))
            return Problem(StatusCodes.Status400BadRequest, "invalid_sku", "sku is not a valid identifier.");

        var answers = JsonSerializer.Serialize(new { measurements = request.Measurements, choices = request.Choices ?? [] }, JsonSerializerOptions.Web);
        var updated = project with
        {
            Title = Title(request.Title) ?? project.Title,
            Zip = string.IsNullOrWhiteSpace(request.Zip) ? project.Zip : request.Zip.Trim(),
            Sku = request.Sku,
            AnswersJson = answers,
            // Changing answers after Accept (deposit not paid yet) needs an updated quote: back to the quote step.
            Stage = project.Stage is ProjectStages.Quote or ProjectStages.Deposit ? ProjectStages.Quote : request.Sku is null ? ProjectStages.Discover : ProjectStages.Configure,
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
        if (StaffCannotWrite(tenant!, project!, http, auth)) return NotYourCustomer();
        if (ProjectStages.IsLocked(project!.Stage))
            return Problem(StatusCodes.Status409Conflict, "project_locked", "Your deposit is paid; this quote is the order.");
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
        if (ProjectStages.IsLocked(project.Stage))
            return Problem(StatusCodes.Status409Conflict, "project_locked", "Your deposit is paid; a measured quote is confirmed with Confirm.");

        var quote = await QuoteOf(project, tenant, quotes, ct);
        var now = clock.GetUtcNow();
        if (quote?.Issue is null) return Problem(StatusCodes.Status409Conflict, "no_quote", "Get your quote first.");
        if (quote.Status == "accepted") return Problem(StatusCodes.Status409Conflict, "already_accepted", "This quote is already accepted.");
        var today = await GuidedQuoteEndpoints.PriceAsync(RequestOf(project), tenant.TenantId!, http, catalog, config, ct);
        if (today.Result?.Quote is null || !SameQuote(quote.Priced, today.Result.Quote))
            return Problem(StatusCodes.Status409Conflict, "quote_outdated", "Your answers or prices changed since this quote. Get an updated quote first.");
        if (quote.Issue.ValidUntil <= now) return Problem(StatusCodes.Status409Conflict, "quote_expired", "This quote expired. Get an updated quote.");
        if (!await quotes.AcceptAsync(tenant.TenantId!, tenant.CustomerId, quote.Id, now, ct))
            return Problem(StatusCodes.Status409Conflict, "quote_not_acceptable", "This quote can't be accepted.");

        var updated = project with { Stage = ProjectStages.Deposit, UpdatedAt = now };
        await projects.UpdateAsync(updated, ct);
        var accepted = await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, quote.Id, ct);
        return Results.Ok(await ToDtoAsync(updated, http, catalog, accepted, config, ct, clock));
    }

    // ---- helpers ----

    /// <summary>
    /// 1i: on a customer's project, staff may change answers or issue quotes only as the salesperson who
    /// prepared it. Customers are already limited to their own projects by the store query.
    /// </summary>
    internal static bool StaffCannotWrite(TenantLookup tenant, Project project, HttpContext http, AuthOptions auth) =>
        tenant.CustomerId is null && project.CustomerId is not null
        && project.SalespersonKey != ClaimsNormalizer.FromPrincipal(http.User, auth).UserKey;

    private static IResult NotYourCustomer() =>
        Problem(StatusCodes.Status403Forbidden, "not_your_customer", "Only the customer, or the salesperson who prepared this project, can change it.");

    private static async Task<(TenantLookup? Tenant, IResult? Problem)> GateAsync(HttpContext http, ITenantLookup tenants, string scope, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return (null, Problem(tenant.Status, tenant.Code, "Could not determine your tenant."));
        if (!tenant.HasScope(scope)) return (null, Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {scope} scope."));
        return (tenant, null);
    }

    internal static async Task<(TenantLookup? Tenant, Project? Project, IResult? Problem)> LoadAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, string scope, CancellationToken ct)
    {
        var (tenant, problem) = await GateAsync(http, tenants, scope, ct);
        if (problem is not null) return (null, null, problem);
        var project = await projects.FindAsync(tenant!.TenantId!, tenant.CustomerId, id, ct);
        return project is null ? (null, null, Results.NotFound()) : (tenant, project, null);
    }

    internal static Task<Quote?> QuoteOf(Project p, TenantLookup tenant, IQuoteStore quotes, CancellationToken ct) =>
        p.QuoteId is Guid qid ? quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, qid, ct) : Task.FromResult<Quote?>(null);

    private sealed record Answers(MeasurementInput? Measurements, GuidedChoiceDto[]? Choices);

    internal static GuidedQuoteRequest RequestOf(Project p)
    {
        var a = JsonSerializer.Deserialize<Answers>(p.AnswersJson, JsonSerializerOptions.Web) ?? new Answers(null, null);
        return new GuidedQuoteRequest(p.Zip, p.Sku, a.Measurements, a.Choices ?? []);
    }

    /// <summary>
    /// "Is the issued quote still what the customer would get?" Same lines (sku, quantity, unit price,
    /// line total) and same total as pricing the current answers now. Compared on the RESULT, not the
    /// request text: the same answers can be written in more than one JSON shape.
    /// </summary>
    internal static bool SameQuote(PricedQuote issued, PricedQuote now) =>
        issued.Total == now.Total && issued.Lines.Count == now.Lines.Count
        && issued.Lines.Zip(now.Lines).All(x => x.First.Sku == x.Second.Sku && x.First.Quantity == x.Second.Quantity
                                              && x.First.UnitPrice == x.Second.UnitPrice && x.First.LineTotal == x.Second.LineTotal);

    /// <summary>The request stored with an issued quote (what it was built from).</summary>
    internal static string RequestJson(GuidedQuoteRequest r) => JsonSerializer.Serialize(r, JsonSerializerOptions.Web);

    internal static int ValidityDays(IConfiguration config) => config.GetValue<int?>("Quotes:ValidityDays") ?? 14;

    private static string? Title(string? t) =>
        string.IsNullOrWhiteSpace(t) ? null : t.Trim().Length > MaxTitle ? t.Trim()[..MaxTitle] : t.Trim();

    internal static async Task<ProjectDto> ToDtoAsync(
        Project p, HttpContext http, ICatalogPrices catalog, Quote? quote, IConfiguration? config, CancellationToken ct, TimeProvider? clock = null)
    {
        GuidedQuoteDto? preview = null;
        PricedQuote? now = null;
        IReadOnlyList<QuoteErrorDto> errors = [];
        var request = RequestOf(p);
        if (p.Sku is not null && config is not null)
        {
            var outcome = await GuidedQuoteEndpoints.PriceAsync(request, p.TenantId, http, catalog, config, ct);
            if (outcome.Result is not null)
            {
                preview = GuidedQuoteEndpoints.ToDto(outcome.Measurement!, outcome.Result);
                now = outcome.Result.Quote;
            }
            else errors = outcome.Errors;
        }

        IssuedQuoteDto? issued = null;
        Deposit? deposit = null;
        if (quote?.Issue is { } issue)
        {
            var depositStore = http.RequestServices.GetRequiredService<IDepositStore>();
            // The deposit belongs to the quote it was paid on; a measured quote (6b) inherits the project's paid deposit.
            deposit = await depositStore.ForQuoteAsync(p.TenantId, quote.Id, ct) ?? await depositStore.ForProjectAsync(p.TenantId, p.Id, ct);
            var dto = QuoteEndpoints.ToDto(quote);
            var clockNow = (clock ?? TimeProvider.System).GetUtcNow();
            issued = new IssuedQuoteDto(quote.Id, issue.Number, quote.Status, issue.ValidUntil, issue.AcceptedAt,
                // Held by a paid deposit: this is the order. Otherwise it's current only if today's pricing agrees (6a).
                Current: deposit?.Paid == true || (now is not null && SameQuote(quote.Priced, now)),
                Expired: quote.Status == "issued" && issue.ValidUntil <= clockNow,
                dto.Currency, dto.Lines, dto.Subtotal, dto.TaxRate, dto.Tax, dto.Total,
                Held: deposit?.Paid == true && (deposit.QuoteId == quote.Id || quote.Status == "accepted"));
        }

        var visit = ProjectStages.IsLocked(p.Stage) ? await http.RequestServices.GetRequiredService<IVisitStore>().ForProjectAsync(p.TenantId, p.Id, ct) : null;
        var install = p.Stage is ProjectStages.Install or ProjectStages.Done
            ? InstallEndpoints.ToDto(await http.RequestServices.GetRequiredService<IInstallStore>().ForProjectAsync(p.TenantId, p.Id, ct))
            : null;
        using var answers = JsonDocument.Parse(p.AnswersJson);
        return new ProjectDto(p.Id, p.Title, p.Zip, p.Stage, p.Sku, answers.RootElement.Clone(), preview, errors, issued, p.UpdatedAt,
            deposit is null ? null : DepositEndpoints.ToDto(deposit, http), PreparedBy: p.SalespersonName,
            Visit: visit is null ? null : VisitEndpoints.ToDto(visit), Install: install);
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
