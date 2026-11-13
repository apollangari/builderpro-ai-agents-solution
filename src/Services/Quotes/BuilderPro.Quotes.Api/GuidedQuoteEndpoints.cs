using System.Text.Json;
using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

// ---- Guided quote contract (2n-b..e). The request has answers, never prices. ----
public sealed record GuidedChoiceDto(string Group, string? Sku, decimal? Quantity = null);
public sealed record GuidedQuoteRequest(string? Zip, string? Sku, MeasurementInput? Measurements, GuidedChoiceDto[]? Choices);

public sealed record MeasurementDto(string Kind, IReadOnlyDictionary<string, decimal> Values, string ProductBasis);
public sealed record GuidedQuoteDto(
    MeasurementDto Measurement, IReadOnlyList<QuoteLineDto> Lines, string Currency,
    decimal Subtotal, decimal TaxRate, decimal Tax, decimal Total, IReadOnlyList<string> Missing, bool Complete);

/// <summary>
/// Guided quote (2n-b..e): ZIP + product + measurements + answers -> priced quote.
///  - Product, option steps and every price come from the catalog for that ZIP (the caller's token).
///  - Quantities come from Measurements (domain), lines from GuidedQuoteBuilder, totals from QuotePricer.
///  - Preview answers while steps are still open (Missing lists them); save needs every required step.
///  - Saved quotes keep the ZIP, the request and each line's group, so they can be re-priced and shown grouped.
/// </summary>
public static class GuidedQuoteEndpoints
{
    public static IEndpointRouteBuilder MapGuidedQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/quotes/guided").RequireAuthorization();
        api.MapPost("/preview", PreviewAsync)
            .WithSummary("Price a guided quote (ZIP, product, measurements, options, protection) without storing it. Lists unanswered required steps.");
        api.MapPost("/", CreateAsync)
            .WithSummary("Price and store a guided quote as a draft. Every required step must be answered.");
        return app;
    }

    internal static async Task<IResult> PreviewAsync(
        GuidedQuoteRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog, IConfiguration config, CancellationToken ct)
    {
        var (_, measurement, result, problem) = await BuildAsync(request, http, tenants, catalog, config, QuoteEndpoints.ReadScope, ct);
        return problem ?? Results.Ok(ToDto(measurement!, result!));
    }

    internal static async Task<IResult> CreateAsync(
        GuidedQuoteRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog,
        IQuoteStore store, IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var (tenant, _, result, problem) = await BuildAsync(request, http, tenants, catalog, config, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (!result!.Complete)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "incomplete_quote", detail: "Answer every required step first.",
                extensions: new Dictionary<string, object?> { ["code"] = "incomplete_quote", ["missing"] = result.Missing });
        }

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        var guided = new GuidedInfo(request.Zip!, JsonSerializer.Serialize(request, JsonSerializerOptions.Web), result.LineGroups);
        var quote = new Quote(Guid.NewGuid(), tenant!.TenantId!, tenant.CustomerId, user.UserKey, "draft", result.Quote!, clock.GetUtcNow(), guided);
        await store.CreateAsync(quote, ct);
        return Results.Created($"/api/quotes/{quote.Id}", QuoteEndpoints.ToDto(quote));
    }

    private static async Task<(TenantLookup? Tenant, Measurement? Measurement, GuidedResult? Result, IResult? Problem)> BuildAsync(
        GuidedQuoteRequest request, HttpContext http, ITenantLookup tenants, ICatalogPrices catalog, IConfiguration config, string scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Zip) || !ClaimsNormalizer.IsIdentifier(request.Sku ?? ""))
            return (null, null, null, Invalid("invalid_guided_quote", [new("invalid_request", "Send the project zip and the product sku.", null)]));

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return (null, null, null, Problem(tenant.Status, tenant.Code, "Could not determine your tenant."));
        if (!tenant.HasScope(scope)) return (null, null, null, Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {scope} scope."));

        var outcome = await PriceAsync(request, tenant.TenantId!, http, catalog, config, ct);
        return outcome.Result is null
            ? (null, null, null, outcome.ToProblem())
            : (tenant, outcome.Measurement, outcome.Result, null);
    }

    /// <summary>What pricing a guided request gave: the priced result, or a status + code + errors (used by projects too).</summary>
    internal sealed record GuidedOutcome(Measurement? Measurement, GuidedResult? Result, int Status, string? Code, IReadOnlyList<QuoteErrorDto> Errors)
    {
        public static GuidedOutcome Fail(int status, string code, string message) => new(null, null, status, code, [new(code, message, null)]);

        public IResult ToProblem() => Code is "invalid_measurements" or "invalid_guided_quote"
            ? Invalid(Code, Errors)
            : Problem(Status, Code ?? "error", Errors.FirstOrDefault()?.Message ?? "");
    }

    /// <summary>Catalog for the ZIP -> measurements -> GuidedQuoteBuilder -> QuotePricer. No tenant/scope checks (callers do them).</summary>
    internal static async Task<GuidedOutcome> PriceAsync(
        GuidedQuoteRequest request, string tenantId, HttpContext http, ICatalogPrices catalog, IConfiguration config, CancellationToken ct)
    {
        var read = await catalog.GetOptionsAsync(http, request.Sku!, request.Zip!, ct);
        if (read.Options is null)
        {
            return read.Problem switch
            {
                "invalid_zip" => GuidedOutcome.Fail(StatusCodes.Status400BadRequest, "invalid_zip", "zip must be a US ZIP (12345) or a Canadian postal code (A1A 1A1)."),
                "zip_not_served" => GuidedOutcome.Fail(StatusCodes.Status422UnprocessableEntity, "zip_not_served", "Your store doesn't serve this ZIP yet."),
                "unknown_sku" => new(null, null, StatusCodes.Status400BadRequest, "invalid_guided_quote", [new("unknown_sku", $"{request.Sku} is not sold in this ZIP.", null)]),
                _ => GuidedOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "catalog_unavailable", "Prices could not be read from the catalog, so nothing was priced."),
            };
        }

        var o = read.Options;
        var measured = Measurements.Measure(o.Measure, request.Measurements);
        if (!measured.Succeeded)
            return new(null, null, StatusCodes.Status400BadRequest, "invalid_measurements", [.. measured.Errors.Select(e => new QuoteErrorDto(e.Code, e.Message, e.Line))]);

        Money M(decimal amount) => new(amount, o.Product.Currency);
        var built = GuidedQuoteBuilder.Build(new GuidedRequest(
            new GuidedProduct(o.Product.Sku, o.Product.Name, o.Product.Unit, M(o.Product.ListPrice)),
            measured.Measurement!,
            o.MinimumQuantity,
            [.. o.Groups.Select(g => new GuidedGroup(g.Id, g.Step, g.Label, g.Selection, g.Basis, g.Required, g.NoneLabel,
                [.. g.Options.Select(x => new GuidedOption(x.Sku, x.Name, x.Unit, M(x.Price), x.Included))]))],
            [.. (request.Choices ?? []).Select(c => new GuidedChoice(c.Group, c.Sku, c.Quantity))],
            QuoteEndpoints.TaxRateFor(tenantId, config)));

        return built.Quote is null
            ? new(null, null, StatusCodes.Status400BadRequest, "invalid_guided_quote", [.. built.Errors.Select(e => new QuoteErrorDto(e.Code, e.Message, e.Line))])
            : new(measured.Measurement, built, StatusCodes.Status200OK, null, []);
    }

    internal static GuidedQuoteDto ToDto(Measurement m, GuidedResult r)
    {
        var q = r.Quote!;
        return new GuidedQuoteDto(
            new MeasurementDto(m.Kind, m.Values, m.ProductBasis),
            [.. q.Lines.Select((l, i) => new QuoteLineDto(l.Sku, l.Name, l.Unit, l.Quantity, l.UnitPrice.Amount, l.LineTotal.Amount, r.LineGroups[i]))],
            q.Total.Currency, q.Subtotal.Amount, q.TaxRate, q.Tax.Amount, q.Total.Amount, r.Missing, r.Complete);
    }

    private static IResult Invalid(string code, IReadOnlyList<QuoteErrorDto> errors) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: code, detail: "The request has problems; see errors.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["errors"] = errors });

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
