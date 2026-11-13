using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

// ---- Contract. Note what the request does NOT have: prices, totals, tax, tenant. ----
public sealed record CreateQuoteLine(string Sku, decimal Quantity);
public sealed record CreateQuoteRequest(CreateQuoteLine[]? Lines);

public sealed record QuoteLineDto(string Sku, string Name, string Unit, decimal Quantity, decimal UnitPrice, decimal LineTotal);
public sealed record QuoteDto(
    Guid Id, string Status, string? CustomerId, string Currency,
    IReadOnlyList<QuoteLineDto> Lines, decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total, DateTimeOffset CreatedAt);
public sealed record QuoteSummaryDto(Guid Id, string Status, string? CustomerId, string Currency, int LineCount, decimal Total, DateTimeOffset CreatedAt);
public sealed record QuoteErrorDto(string Code, string Message, int? Line);

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

        return app;
    }

    internal static async Task<IResult> CreateAsync(
        CreateQuoteRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog,
        IQuoteStore store, IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");
        if (!tenant.HasScope(WriteScope)) return Problem(StatusCodes.Status403Forbidden, "missing_scope", $"Creating quotes needs the {WriteScope} scope.");

        var lines = request.Lines ?? [];
        var shapeErrors = lines
            .Select((l, i) => ClaimsNormalizer.IsIdentifier(l.Sku) ? null : new QuoteErrorDto("invalid_sku", "sku is not a valid identifier.", i))
            .OfType<QuoteErrorDto>()
            .ToArray();
        if (shapeErrors.Length > 0) return Invalid(shapeErrors);

        var products = await catalog.GetAsync(http, ct);
        if (products is null) return Problem(StatusCodes.Status503ServiceUnavailable, "catalog_unavailable", "Prices could not be read from the catalog, so nothing was priced.");

        // SKUs the caller's tenant doesn't sell (or that are inactive) are reported, never guessed.
        var unknown = lines
            .Select((l, i) => products.ContainsKey(l.Sku) ? null : new QuoteErrorDto("unknown_sku", $"{l.Sku} is not in your catalog.", i))
            .OfType<QuoteErrorDto>()
            .ToArray();
        if (unknown.Length > 0) return Invalid(unknown);

        var input = new QuoteInput(
            [.. lines.Select(l =>
            {
                var p = products[l.Sku];
                return new QuoteLineInput(p.Sku, p.Name, p.Unit, l.Quantity, new Money(p.ListPrice, p.Currency));
            })],
            TaxRateFor(tenant.TenantId!, config));

        var result = QuotePricer.Price(input);
        if (!result.Succeeded) return Invalid([.. result.Errors.Select(e => new QuoteErrorDto(e.Code, e.Message, e.Line))]);

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var quote = new Quote(Guid.NewGuid(), tenant.TenantId!, tenant.CustomerId, user.UserKey, "draft", result.Quote!, clock.GetUtcNow());
        await store.CreateAsync(quote, ct);

        return Results.Created($"/api/quotes/{quote.Id}", ToDto(quote));
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
            new QuoteSummaryDto(q.Id, q.Status, q.CustomerId, q.Priced.Total.Currency, q.Priced.Lines.Count, q.Priced.Total.Amount, q.CreatedAt)));
    }

    /// <summary>Per-tenant tax rate from configuration (Quotes:TaxRates:{tenant}), else Quotes:DefaultTaxRate, else 0.</summary>
    internal static decimal TaxRateFor(string tenantId, IConfiguration config) =>
        config.GetValue<decimal?>($"Quotes:TaxRates:{tenantId}") ?? config.GetValue<decimal?>("Quotes:DefaultTaxRate") ?? 0m;

    private static QuoteDto ToDto(Quote q) => new(
        q.Id, q.Status, q.CustomerId, q.Priced.Total.Currency,
        [.. q.Priced.Lines.Select(l => new QuoteLineDto(l.Sku, l.Name, l.Unit, l.Quantity, l.UnitPrice.Amount, l.LineTotal.Amount))],
        q.Priced.Subtotal.Amount, q.Priced.TaxRate, q.Priced.Tax.Amount, q.Priced.Total.Amount, q.CreatedAt);

    private static IResult Invalid(IReadOnlyList<QuoteErrorDto> errors) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_quote", detail: "The quote request has problems; see errors.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_quote", ["errors"] = errors });

    // RFC 9457 problem+json with the same stable "code" extension every service uses.
    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
