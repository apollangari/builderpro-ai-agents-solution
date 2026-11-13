using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

public sealed record DepositDto(
    Guid Id, Guid ProjectId, int QuoteNumber, decimal QuoteTotal, decimal Amount, string Currency, decimal Percent,
    string Status, DateTimeOffset? PaidAt, string CheckoutUrl);

/// <summary>
/// The 5 % deposit (6a, owner decision: the price is held only once a deposit is paid).
///
///  - Start (POST /api/projects/{id}/deposit): the customer, for their ACCEPTED quote that is still current
///    and not expired. The amount comes from DepositPolicy on the stored quote total. Repeat calls return
///    the same pending deposit.
///  - Pay: through the payment provider. "simulated" (development and tests) is our own test-mode page,
///    POST /api/deposits/{id}/pay. A real provider (Stripe Checkout, later) confirms by webhook instead.
///  - Paid: the quote's price is held, the project moves to the measure visit, and its answers lock.
///
/// Customer only: staff can't pay for a customer, and an agent can't either (X-BuilderPro-Agent refused).
/// </summary>
public static class DepositEndpoints
{
    public const string Simulated = "simulated";

    public static IEndpointRouteBuilder MapDepositEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/deposits").RequireAuthorization();
        api.MapGet("/{id:guid}", GetAsync).WithSummary("My deposit: amount, quote and status (the payment page reads this).");
        api.MapPost("/{id:guid}/pay", PayAsync).WithSummary("Test-mode payment (Payments:Provider = simulated only). Customer only, never an agent.");
        return app;
    }

    internal static async Task<IResult> StartAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, IDepositStore deposits,
        ICatalogPrices catalog, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return Problem(StatusCodes.Status403Forbidden, "agent_cannot_pay", "Only the customer can pay a deposit.");
        var (tenant, problem) = await Gate(http, tenants, ct);
        if (problem is not null) return problem;

        var project = await projects.FindAsync(tenant!.TenantId!, tenant.CustomerId, id, ct);
        if (project is null) return Results.NotFound();
        if (tenant.CustomerId is null || project.CustomerId != tenant.CustomerId)
            return Problem(StatusCodes.Status403Forbidden, "customer_only", "Only the customer can pay their deposit.");
        if (project.Stage != ProjectStages.Deposit || project.QuoteId is not Guid quoteId)
            return Problem(StatusCodes.Status409Conflict, "not_accepted", "Accept your quote first; the deposit holds its price.");

        var quote = await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, quoteId, ct);
        var now = clock.GetUtcNow();
        if (quote?.Issue is null || quote.Status != "accepted") return Problem(StatusCodes.Status409Conflict, "not_accepted", "Accept your quote first.");
        if (quote.Issue.ValidUntil <= now) return Problem(StatusCodes.Status409Conflict, "quote_expired", "This quote expired before the deposit. Get an updated quote.");
        var today = await GuidedQuoteEndpoints.PriceAsync(ProjectEndpoints.RequestOf(project), tenant.TenantId!, http, catalog, config, ct);
        if (today.Result?.Quote is null || !ProjectEndpoints.SameQuote(quote.Priced, today.Result.Quote))
            return Problem(StatusCodes.Status409Conflict, "quote_outdated", "Prices changed since you accepted. Get an updated quote; the price is held only once the deposit is paid.");

        var existing = await deposits.ForQuoteAsync(tenant.TenantId!, quote.Id, ct);
        if (existing is not null) return Results.Ok(ToDto(existing, http, quote));

        var percent = config.GetValue<decimal?>($"Quotes:DepositPercent:{tenant.TenantId}") ?? config.GetValue<decimal?>("Quotes:DepositPercent:Default") ?? DepositPolicy.DefaultPercent;
        var amount = DepositPolicy.AmountFor(quote.Priced.Total, percent);
        var deposit = new Deposit(Guid.NewGuid(), tenant.TenantId!, project.Id, quote.Id, tenant.CustomerId, amount.Amount, amount.Currency, percent,
            DepositStatus.Pending, Provider(config), now, null);
        await deposits.CreateAsync(deposit, ct);
        return Results.Created($"/api/deposits/{deposit.Id}", ToDto(deposit, http, quote));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ITenantLookup tenants, IDepositStore deposits, IQuoteStore quotes, CancellationToken ct)
    {
        var (tenant, problem) = await Gate(http, tenants, ct);
        if (problem is not null) return problem;
        var deposit = await deposits.FindAsync(tenant!.TenantId!, tenant.CustomerId, id, ct);
        if (deposit is null) return Results.NotFound();
        var quote = await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, deposit.QuoteId, ct);
        return Results.Ok(ToDto(deposit, http, quote));
    }

    internal static async Task<IResult> PayAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IDepositStore deposits, IQuoteStore quotes, IProjectStore projects,
        IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return Problem(StatusCodes.Status403Forbidden, "agent_cannot_pay", "Only the customer can pay a deposit.");
        if (Provider(config) != Simulated)
            return Problem(StatusCodes.Status404NotFound, "not_found", "Payments are confirmed by the payment provider.");
        var (tenant, problem) = await Gate(http, tenants, ct);
        if (problem is not null) return problem;
        if (tenant!.CustomerId is null) return Problem(StatusCodes.Status403Forbidden, "customer_only", "Only the customer can pay their deposit.");

        var deposit = await deposits.FindAsync(tenant.TenantId!, tenant.CustomerId, id, ct);
        if (deposit is null) return Results.NotFound();
        if (deposit.Paid) return Results.Ok(ToDto(deposit, http, await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, deposit.QuoteId, ct)));

        var quote = await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, deposit.QuoteId, ct);
        var now = clock.GetUtcNow();
        if (quote?.Issue is null || quote.Issue.ValidUntil <= now)
            return Problem(StatusCodes.Status409Conflict, "quote_expired", "This quote expired before payment. Get an updated quote.");

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        if (!await deposits.MarkPaidAsync(tenant.TenantId!, deposit.Id, now, user.UserKey, ct))
            return Problem(StatusCodes.Status409Conflict, "not_payable", "This deposit can't be paid.");

        // Paid: the price is held and the project moves on to the measure visit (6b).
        var project = await projects.FindAsync(tenant.TenantId!, tenant.CustomerId, deposit.ProjectId, ct);
        if (project is not null && project.QuoteId == deposit.QuoteId)
            await projects.UpdateAsync(project with { Stage = ProjectStages.Measure, UpdatedAt = now }, ct);

        var paid = await deposits.FindAsync(tenant.TenantId!, tenant.CustomerId, id, ct);
        return Results.Ok(ToDto(paid!, http, quote));
    }

    internal static DepositDto ToDto(Deposit d, HttpContext http, Quote? quote = null) =>
        new(d.Id, d.ProjectId, quote?.Issue?.Number ?? 0, quote?.Priced.Total.Amount ?? 0, d.Amount, d.Currency, d.Percent, d.Status, d.PaidAt,
            CheckoutUrl: $"/pay/{d.Id}");   // a real provider would return its hosted checkout URL here

    private static string Provider(IConfiguration config) => config["Payments:Provider"] ?? Simulated;

    private static async Task<(TenantLookup? Tenant, IResult? Problem)> Gate(HttpContext http, ITenantLookup tenants, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return (null, Problem(tenant.Status, tenant.Code, "Could not determine your tenant."));
        if (!tenant.HasScope(QuoteEndpoints.WriteScope)) return (null, Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {QuoteEndpoints.WriteScope} scope."));
        return (tenant, null);
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
