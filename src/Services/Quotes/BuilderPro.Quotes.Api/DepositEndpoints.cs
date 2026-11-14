using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

public sealed record DepositDto(
    Guid Id, Guid ProjectId, int QuoteNumber, decimal QuoteTotal, decimal Amount, string Currency, decimal Percent,
    string Status, DateTimeOffset? PaidAt, string CheckoutUrl, string Provider = DepositEndpoints.Simulated, string Kind = PaymentKinds.Deposit);

/// <summary>Final gaps: what's left to pay when the job is done (final total - deposit paid) and its payment.</summary>
public sealed record BalanceDto(decimal Total, decimal DepositPaid, decimal Due, string Currency, DepositDto? Payment);

public sealed record PaymentEventDto(string From, string To, DateTimeOffset At, string Source, string? Detail);
public sealed record SimulatedPayRequest(string? Outcome = null);   // "decline" fails the test payment

/// <summary>
/// The 5 % deposit (6a, owner decision: the price is held only once a deposit is paid).
///
///  - Start (POST /api/projects/{id}/deposit): the customer, for their ACCEPTED quote that is still current
///    and not expired. The amount comes from DepositPolicy on the stored quote total. Opens the provider's
///    checkout; repeat calls return the same open checkout. A failed payment starts a new deposit attempt.
///  - Pay: through the payment provider (6d). "simulated" (development and tests) is our own test-mode
///    page, POST /api/deposits/{id}/pay. "stripe" is Stripe Checkout, confirmed only by its signed webhook
///    or by our own status lookup (reconciliation), never by the browser.
///  - Status follows the payment state machine (PaymentStates); every move is recorded (GET .../events).
///  - Succeeded: the quote's price is held, the project moves to the measure visit, and its answers lock.
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
        api.MapPost("/{id:guid}/pay", PayAsync).WithSummary("Test-mode payment (Payments:Provider = simulated only): pays, or {outcome: decline} fails it. Customer only, never an agent.");
        api.MapGet("/{id:guid}/events", EventsAsync).WithSummary("The payment's state-machine history: every move, when, and who decided (checkout, webhook, lookup).");
        // 6d: Stripe tells us a payment happened. Anonymous on purpose: the SIGNATURE is the authentication.
        app.MapPost("/api/payments/stripe/webhook", StripeWebhookAsync).AllowAnonymous()
            .WithSummary("Stripe webhook (signed). checkout.session.completed marks the deposit paid; replays and unsigned calls change nothing.");
        return app;
    }

    internal static async Task<IResult> StartAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, IDepositStore deposits,
        ICatalogPrices catalog, PaymentFlow flow, IConfiguration config, TimeProvider clock, CancellationToken ct)
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
        // A payment still waiting for an answer is asked about first: never open a second checkout while one may be paid.
        if (existing is not null && PaymentStates.NeedsAnswer(existing.Status)
            && (existing.Status == PaymentStates.Unknown || existing.CheckoutExpiresAt <= now))
            existing = await flow.ReconcileAsync(existing, ct);
        if (existing is not null && existing.Status is PaymentStates.Succeeded or PaymentStates.Processing or PaymentStates.Unknown)
            return Results.Ok(ToDto(existing, http, quote));

        // Nothing yet, or the last attempt failed: a new attempt. (A "created" one never reached the provider: reuse it.)
        var deposit = existing is { Status: PaymentStates.Created } ? existing : null;
        var isNew = deposit is null;
        if (deposit is null)
        {
            var percent = config.GetValue<decimal?>($"Quotes:DepositPercent:{tenant.TenantId}") ?? config.GetValue<decimal?>("Quotes:DepositPercent:Default") ?? DepositPolicy.DefaultPercent;
            var amount = DepositPolicy.AmountFor(quote.Priced.Total, percent);
            deposit = new Deposit(Guid.NewGuid(), tenant.TenantId!, project.Id, quote.Id, tenant.CustomerId, amount.Amount, amount.Currency, percent,
                PaymentStates.Created, flow.ProviderName, now, null);
            await deposits.CreateAsync(deposit, ct);
            await ContactSnapshot.RefreshAsync(http, tenant.TenantId!, tenant.CustomerId, ct);   // where to send order updates
        }

        deposit = await flow.OpenCheckoutAsync(deposit, quote.Issue.Number, ct);
        return deposit.Status switch
        {
            PaymentStates.Failed => Problem(StatusCodes.Status502BadGateway, "payment_refused", "The payment provider refused this payment. Try again, or contact the store."),
            // Unknown: the provider didn't answer in time; reconciliation finds out. The page shows "checking".
            PaymentStates.Unknown => Results.Accepted($"/api/deposits/{deposit.Id}", ToDto(deposit, http, quote)),
            _ when isNew => Results.Created($"/api/deposits/{deposit.Id}", ToDto(deposit, http, quote)),
            _ => Results.Ok(ToDto(deposit, http, quote)),
        };
    }

    internal static async Task<IResult> GetAsync(
        Guid id, bool? refresh, HttpContext http, ITenantLookup tenants, IDepositStore deposits, IQuoteStore quotes, PaymentFlow flow, CancellationToken ct)
    {
        var (tenant, problem) = await Gate(http, tenants, ct);
        if (problem is not null) return problem;
        var deposit = await deposits.FindAsync(tenant!.TenantId!, tenant.CustomerId, id, ct);
        if (deposit is null) return Results.NotFound();
        // Back from the provider's checkout: if the webhook hasn't arrived yet, ask the provider (reconciliation).
        if (refresh == true && PaymentStates.NeedsAnswer(deposit.Status) && deposit.Provider == flow.ProviderName)
            deposit = await flow.ReconcileAsync(deposit, ct);
        var quote = await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, deposit.QuoteId, ct);
        return Results.Ok(ToDto(deposit, http, quote));
    }

    internal static async Task<IResult> EventsAsync(Guid id, HttpContext http, ITenantLookup tenants, IDepositStore deposits, CancellationToken ct)
    {
        var (tenant, problem) = await Gate(http, tenants, ct, QuoteEndpoints.ReadScope);
        if (problem is not null) return problem;
        var deposit = await deposits.FindAsync(tenant!.TenantId!, tenant.CustomerId, id, ct);
        if (deposit is null) return Results.NotFound();
        return Results.Ok((await deposits.EventsAsync(tenant.TenantId!, id, ct)).Select(e => new PaymentEventDto(e.From, e.To, e.At, e.Source, e.Detail)));
    }

    internal static async Task<IResult> PayAsync(
        Guid id, SimulatedPayRequest? request, HttpContext http, ITenantLookup tenants, IDepositStore deposits, IQuoteStore quotes, PaymentFlow flow,
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
        var quote = await quotes.FindAsync(tenant.TenantId!, tenant.CustomerId, deposit.QuoteId, ct);
        if (deposit.Paid) return Results.Ok(ToDto(deposit, http, quote));
        if (deposit.Status != PaymentStates.Processing)
            return Problem(StatusCodes.Status409Conflict, "not_payable", $"This payment is {deposit.Status}. Start the deposit again from your project.");
        if (quote?.Issue is null || quote.Issue.ValidUntil <= clock.GetUtcNow())
            return Problem(StatusCodes.Status409Conflict, "quote_expired", "This quote expired before payment. Get an updated quote.");

        var user = ClaimsNormalizer.FromPrincipal(http.User, auth);
        deposit = string.Equals(request?.Outcome, "decline", StringComparison.OrdinalIgnoreCase)
            ? await flow.FailedAsync(deposit, PaymentSources.Simulated, "card declined (test)", ct)
            : await flow.PaidAsync(deposit, StripeForm.MinorUnits(deposit.Amount), deposit.Currency, $"sim:{user.UserKey}", PaymentSources.Simulated, ct);
        return Results.Ok(ToDto(deposit, http, quote));
    }

    /// <summary>
    /// 6d: Stripe's webhook. Only a correctly SIGNED, recent event counts. Completed + paid -> succeeded
    /// (amount and currency must match); async failure or expiry -> failed. Replays are no-ops (the state
    /// machine only moves from processing/unknown). Unknown events are acknowledged and ignored.
    /// </summary>
    internal static async Task<IResult> StripeWebhookAsync(
        HttpContext http, IDepositStore deposits, PaymentFlow flow, IConfiguration config, IServiceProvider services, TimeProvider clock,
        ILoggerFactory logs, CancellationToken ct)
    {
        var log = logs.CreateLogger("StripeWebhook");
        var stripe = services.GetService<StripeOptions>();
        if (Provider(config) != "stripe" || stripe is null) return Results.NotFound();
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (!StripeSignature.IsValid(http.Request.Headers["Stripe-Signature"], body, stripe.WebhookSecret, clock.GetUtcNow()))
        {
            log.LogWarning("Stripe webhook with a missing or wrong signature refused");
            return Results.BadRequest();
        }
        var evt = StripeEvent.Parse(body);
        if (evt is null) return Results.BadRequest();
        var s = evt.Data.Object;
        if (s.Metadata is null || !s.Metadata.TryGetValue("deposit_id", out var rawId) || !Guid.TryParse(rawId, out var depositId)
            || !s.Metadata.TryGetValue("tenant_id", out var tenantId) || !ClaimsNormalizer.IsIdentifier(tenantId))
            return Results.Ok();   // not one of our checkouts
        var deposit = await deposits.FindAsync(tenantId, null, depositId, ct);
        if (deposit is null)
        {
            log.LogWarning("Stripe event {Event} names an unknown deposit", evt.Id);
            return Results.Ok();
        }

        switch (evt.Type)
        {
            case "checkout.session.completed" or "checkout.session.async_payment_succeeded":
                var outcome = StripeOutcome.Of(s with { Status = "complete" });
                if (outcome.Outcome == ProviderOutcome.Paid)
                    await flow.PaidAsync(deposit, outcome.AmountMinor, outcome.Currency, outcome.PaymentRef ?? s.Id ?? "", PaymentSources.Webhook, ct);
                break;   // completed but not paid yet (bank debits): wait for async_payment_succeeded / _failed
            case "checkout.session.async_payment_failed":
                await flow.FailedAsync(deposit, PaymentSources.Webhook, "payment failed", ct);
                break;
            case "checkout.session.expired":
                await flow.FailedAsync(deposit, PaymentSources.Webhook, "checkout expired", ct);
                break;
        }
        return Results.Ok();
    }

    internal static DepositDto ToDto(Deposit d, HttpContext http, Quote? quote = null) =>
        new(d.Id, d.ProjectId, quote?.Issue?.Number ?? 0, quote?.Priced.Total.Amount ?? 0, d.Amount, d.Currency, d.Percent, d.Status, d.PaidAt,
            CheckoutUrl: d.CheckoutUrl ?? $"/pay/{d.Id}", Provider: d.Provider, Kind: d.Kind);

    internal static string Provider(IConfiguration config) => config["Payments:Provider"] ?? Simulated;

    private static async Task<(TenantLookup? Tenant, IResult? Problem)> Gate(HttpContext http, ITenantLookup tenants, CancellationToken ct, string scope = QuoteEndpoints.WriteScope)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return (null, Problem(tenant.Status, tenant.Code, "Could not determine your tenant."));
        if (!tenant.HasScope(scope)) return (null, Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {scope} scope."));
        return (tenant, null);
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
