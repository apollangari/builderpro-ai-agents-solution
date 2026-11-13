using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

// ---- Contract. Note what the request does NOT have: prices, totals, tax, tenant. ----
public sealed record CreateQuoteLine(string Sku, decimal Quantity);
// Zip (2n-a): price with the store's zone for that ZIP; omitted = list prices (staff quote builder, 2m).
public sealed record CreateQuoteRequest(CreateQuoteLine[]? Lines, string? Zip = null);

public sealed record QuoteLineDto(string Sku, string Name, string Unit, decimal Quantity, decimal UnitPrice, decimal LineTotal, string? Group = null);
public sealed record QuoteDto(
    Guid Id, string Status, string? CustomerId, string Currency,
    IReadOnlyList<QuoteLineDto> Lines, decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total, DateTimeOffset CreatedAt, string? Zip = null);
public sealed record QuoteSummaryDto(Guid Id, string Status, string? CustomerId, string Currency, int LineCount, decimal Total, DateTimeOffset CreatedAt,
    string? Zip = null, string? Title = null);
public sealed record QuoteErrorDto(string Code, string Message, int? Line);

// Estimates (2k): price "this product at that quantity" without storing anything.
public sealed record EstimateItem(string Sku, decimal Quantity);
public sealed record EstimateRequest(EstimateItem[]? Items, string? Zip = null);
public sealed record EstimateDto(
    string Sku, decimal Quantity, string? Unit, decimal? UnitPrice, decimal? Subtotal, decimal? TaxRate, decimal? Tax, decimal? Total,
    string? Currency, IReadOnlyList<QuoteErrorDto> Errors);

public static class QuoteEndpoints
{
    public const string ReadScope = "quotes.read";
    public const string WriteScope = "quotes.write";
    public const int ListLimit = 50;

    public static IEndpointRouteBuilder MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/quotes").RequireAuthorization();

        api.MapPost("/", CreateAsync).WithSummary("Price and store a draft quote. Prices come from the catalog, totals from QuotePricer.");
        api.MapGet("/", ListAsync).WithSummary("Recent quotes: a customer's own, or the whole tenant's for staff.");
        api.MapGet("/{id:guid}", GetAsync).WithSummary("One quote. Another tenant's (or customer's) quote is a plain 404.");
        api.MapPost("/preview", PreviewAsync)
            .WithSummary("Price a whole quote exactly like POST /api/quotes would, without storing it (quotes.read).");
        api.MapPost("/estimators/roof", RoofAsync)
            .WithSummary("Roof take-off (2g): footprint, pitch, waste, edges -> squares, bundles, rolls, drip edge. Quantities only, no prices.");
        api.MapPost("/estimates", EstimatesAsync)
            .WithSummary("Price each item on its own (catalog price + QuotePricer, tax included). Nothing is stored. For compare (2k) and agents.");

        return app;
    }

    internal static async Task<IResult> CreateAsync(
        CreateQuoteRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog,
        IQuoteStore store, IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var (tenant, priced, problem) = await PriceRequestAsync(request, http, tenants, catalog, config, WriteScope, ct);
        if (problem is not null) return problem;

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var quote = new Quote(Guid.NewGuid(), tenant!.TenantId!, tenant.CustomerId, user.UserKey, "draft", priced!, clock.GetUtcNow());
        await store.CreateAsync(quote, ct);

        return Results.Created($"/api/quotes/{quote.Id}", ToDto(quote));
    }

    /// <summary>Exactly what POST /api/quotes would price, without storing it (2m quote builder).</summary>
    internal static async Task<IResult> PreviewAsync(
        CreateQuoteRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var (tenant, priced, problem) = await PriceRequestAsync(request, http, tenants, catalog, config, ReadScope, ct);
        return problem ?? Results.Ok(ToDto(new Quote(Guid.Empty, tenant!.TenantId!, tenant.CustomerId, "", "preview", priced!, clock.GetUtcNow())));
    }

    /// <summary>
    /// The ONE pricing path for create and preview: tenant + scope, SKU shape, catalog prices
    /// (never the request's), QuotePricer. Returns the priced quote or the problem to send back.
    /// </summary>
    private static async Task<(TenantLookup? Tenant, PricedQuote? Priced, IResult? Problem)> PriceRequestAsync(
        CreateQuoteRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog, IConfiguration config, string scope, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return (null, null, Problem(tenant.Status, tenant.Code, "Could not determine your tenant."));
        if (!tenant.HasScope(scope)) return (null, null, Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {scope} scope."));

        var lines = request.Lines ?? [];
        var shapeErrors = lines
            .Select((l, i) => ClaimsNormalizer.IsIdentifier(l.Sku) ? null : new QuoteErrorDto("invalid_sku", "sku is not a valid identifier.", i))
            .OfType<QuoteErrorDto>()
            .ToArray();
        if (shapeErrors.Length > 0) return (null, null, Invalid(shapeErrors));

        var read = await catalog.GetAsync(http, request.Zip, ct);
        if (CatalogProblem(read) is { } catalogProblem) return (null, null, catalogProblem);
        var products = read.Products!;

        // SKUs the caller's tenant doesn't sell (or that are inactive) are reported, never guessed.
        var unknown = lines
            .Select((l, i) => products.ContainsKey(l.Sku) ? null : new QuoteErrorDto("unknown_sku", $"{l.Sku} is not in your catalog (or not sold in this ZIP).", i))
            .OfType<QuoteErrorDto>()
            .ToArray();
        if (unknown.Length > 0) return (null, null, Invalid(unknown));

        var input = new QuoteInput(
            [.. lines.Select(l =>
            {
                var p = products[l.Sku];
                return new QuoteLineInput(p.Sku, p.Name, p.Unit, l.Quantity, new Money(p.ListPrice, p.Currency));
            })],
            TaxRateFor(tenant.TenantId!, config));

        var result = QuotePricer.Price(input);
        return result.Succeeded
            ? (tenant, result.Quote, null)
            : (null, null, Invalid([.. result.Errors.Select(e => new QuoteErrorDto(e.Code, e.Message, e.Line))]));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ITenantLookup tenants, IQuoteStore store, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");
        if (!tenant.HasScope(ReadScope)) return Problem(StatusCodes.Status403Forbidden, "missing_scope", $"Reading quotes needs the {ReadScope} scope.");

        var quote = await store.FindAsync(tenant.TenantId!, tenant.CustomerId, id, ct);
        return quote is null ? Results.NotFound() : Results.Ok(ToDto(quote));
    }

    internal static async Task<IResult> ListAsync(HttpContext http, ITenantLookup tenants, IQuoteStore store, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");
        if (!tenant.HasScope(ReadScope)) return Problem(StatusCodes.Status403Forbidden, "missing_scope", $"Reading quotes needs the {ReadScope} scope.");

        var quotes = await store.ListAsync(tenant.TenantId!, tenant.CustomerId, ListLimit, ct);
        return Results.Ok(quotes.Select(q =>
            new QuoteSummaryDto(q.Id, q.Status, q.CustomerId, q.Priced.Total.Currency, q.Priced.Lines.Count, q.Priced.Total.Amount, q.CreatedAt,
                q.Guided?.Zip, q.Guided is null ? null : q.Priced.Lines.FirstOrDefault()?.Name)));
    }

    public const int MaxEstimateItems = 6;

    /// <summary>Pure take-off: no tenant or catalog needed, still requires a signed-in caller (group policy).</summary>
    internal static IResult RoofAsync(RoofInput input)
    {
        var estimate = RoofEstimator.Estimate(input);
        return estimate.Succeeded
            ? Results.Ok(estimate.Quantities)
            : Invalid([.. estimate.Errors.Select(e => new QuoteErrorDto(e.Code, e.Message, null))]);
    }

    internal static async Task<IResult> EstimatesAsync(
        EstimateRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog, IConfiguration config, CancellationToken ct)
    {
        var items = request.Items ?? [];
        if (items.Length is 0 or > MaxEstimateItems)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_estimate", $"Send 1 to {MaxEstimateItems} items.");
        }

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");
        if (!tenant.HasScope(ReadScope)) return Problem(StatusCodes.Status403Forbidden, "missing_scope", $"Estimates need the {ReadScope} scope.");

        var read = await catalog.GetAsync(http, request.Zip, ct);
        if (CatalogProblem(read) is { } catalogProblem) return catalogProblem;
        var products = read.Products!;

        var taxRate = TaxRateFor(tenant.TenantId!, config);
        return Results.Ok(items.Select(item => Estimate(item, products, taxRate)));
    }

    /// <summary>One item, priced alone by the same rules as a quote. Problems come back per item, not as a 400.</summary>
    private static EstimateDto Estimate(EstimateItem item, IReadOnlyDictionary<string, CatalogProduct> products, decimal taxRate)
    {
        static EstimateDto Fail(EstimateItem i, string code, string message) =>
            new(i.Sku, i.Quantity, null, null, null, null, null, null, null, [new QuoteErrorDto(code, message, null)]);

        if (!ClaimsNormalizer.IsIdentifier(item.Sku)) return Fail(item, "invalid_sku", "sku is not a valid identifier.");
        if (!products.TryGetValue(item.Sku, out var p)) return Fail(item, "unknown_sku", $"{item.Sku} is not in your catalog (or not sold in this ZIP).");

        var result = QuotePricer.Price(new QuoteInput([new QuoteLineInput(p.Sku, p.Name, p.Unit, item.Quantity, new Money(p.ListPrice, p.Currency))], taxRate));
        if (!result.Succeeded)
        {
            return new(item.Sku, item.Quantity, p.Unit, p.ListPrice, null, null, null, null, p.Currency,
                [.. result.Errors.Select(e => new QuoteErrorDto(e.Code, e.Message, null))]);
        }

        var q = result.Quote!;
        return new(item.Sku, item.Quantity, p.Unit, p.ListPrice, q.Subtotal.Amount, q.TaxRate, q.Tax.Amount, q.Total.Amount, q.Total.Currency, []);
    }

    /// <summary>The catalog couldn't give prices: a refused ZIP keeps its own code, anything else is 503 (fail closed).</summary>
    private static IResult? CatalogProblem(CatalogRead read) => read switch
    {
        { Products: not null } => null,
        { ZipProblem: "invalid_zip" } => Problem(StatusCodes.Status400BadRequest, "invalid_zip", "zip must be a US ZIP (12345) or a Canadian postal code (A1A 1A1)."),
        { ZipProblem: "zip_not_served" } => Problem(StatusCodes.Status422UnprocessableEntity, "zip_not_served", "Your store doesn't serve this ZIP yet."),
        _ => Problem(StatusCodes.Status503ServiceUnavailable, "catalog_unavailable", "Prices could not be read from the catalog, so nothing was priced."),
    };

    /// <summary>Per-tenant tax rate from configuration (Quotes:TaxRates:{tenant}), else Quotes:DefaultTaxRate, else 0.</summary>
    internal static decimal TaxRateFor(string tenantId, IConfiguration config) =>
        config.GetValue<decimal?>($"Quotes:TaxRates:{tenantId}") ?? config.GetValue<decimal?>("Quotes:DefaultTaxRate") ?? 0m;

    internal static QuoteDto ToDto(Quote q) => new(
        q.Id, q.Status, q.CustomerId, q.Priced.Total.Currency,
        [.. q.Priced.Lines.Select((l, i) => new QuoteLineDto(l.Sku, l.Name, l.Unit, l.Quantity, l.UnitPrice.Amount, l.LineTotal.Amount,
            q.Guided?.LineGroups.ElementAtOrDefault(i)))],
        q.Priced.Subtotal.Amount, q.Priced.TaxRate, q.Priced.Tax.Amount, q.Priced.Total.Amount, q.CreatedAt, q.Guided?.Zip);

    private static IResult Invalid(IReadOnlyList<QuoteErrorDto> errors) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_quote", detail: "The quote request has problems; see errors.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_quote", ["errors"] = errors });

    // RFC 9457 problem+json with the same stable "code" extension every service uses.
    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
