using BuilderPro.Quotes.Domain;
using BuilderPro.Security;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// Final gaps: the balance. When the installation is done, the customer pays what's left: the final
/// (measured, accepted) quote total minus the deposit already paid. Same payment machinery as the deposit
/// (provider checkout, payment state machine, webhook / reconciliation, audit trail); customer only, never
/// an agent; one balance payment at a time; a paid balance settles the job.
/// </summary>
public static class BalanceEndpoints
{
    public static RouteGroupBuilder MapBalanceEndpoints(this RouteGroupBuilder projects)
    {
        projects.MapPost("/{id:guid}/balance", PayAsync).WithSummary("When the job is done: start (or resume) paying the balance (final total - deposit). Customer only, never an agent.");
        return projects;
    }

    /// <summary>What's due on a done project, or null when it isn't done or has nothing to settle.</summary>
    internal static async Task<BalanceDto?> DueAsync(Project p, Quote? quote, IDepositStore deposits, HttpContext http, CancellationToken ct)
    {
        if (p.Stage != ProjectStages.Done || quote?.Issue is null || quote.Status != "accepted") return null;
        var deposit = await deposits.ForProjectAsync(p.TenantId, p.Id, ct);
        var paidDeposit = deposit?.Paid == true ? deposit.Amount : 0m;
        var due = Math.Max(0m, quote.Priced.Total.Amount - paidDeposit);
        var payment = await deposits.BalanceForProjectAsync(p.TenantId, p.Id, ct);
        return new BalanceDto(quote.Priced.Total.Amount, paidDeposit, payment?.Paid == true ? 0m : due, quote.Priced.Total.Currency,
            payment is null ? null : DepositEndpoints.ToDto(payment, http, quote));
    }

    internal static async Task<IResult> PayAsync(
        Guid id, HttpContext http, ITenantLookup tenants, IProjectStore projects, IQuoteStore quotes, IDepositStore deposits, PaymentFlow flow,
        TimeProvider clock, CancellationToken ct)
    {
        if (AgentCall.IsAgent(http)) return Problem(StatusCodes.Status403Forbidden, "agent_cannot_pay", "Only the customer can pay.");
        var (tenant, project, problem) = await ProjectEndpoints.LoadAsync(id, http, tenants, projects, QuoteEndpoints.WriteScope, ct);
        if (problem is not null) return problem;
        if (tenant!.CustomerId is null || project!.CustomerId != tenant.CustomerId)
            return Problem(StatusCodes.Status403Forbidden, "customer_only", "Only the customer can pay their balance.");
        var quote = await ProjectEndpoints.QuoteOf(project, tenant, quotes, ct);
        var balance = await DueAsync(project, quote, deposits, http, ct);
        if (balance is null) return Problem(StatusCodes.Status409Conflict, "not_done", "The balance is due when the installation is done.");

        var existing = await deposits.BalanceForProjectAsync(tenant.TenantId!, project.Id, ct);
        if (existing is not null && PaymentStates.NeedsAnswer(existing.Status) && (existing.Status == PaymentStates.Unknown || existing.CheckoutExpiresAt <= clock.GetUtcNow()))
            existing = await flow.ReconcileAsync(existing, ct);
        if (existing is not null && existing.Status is PaymentStates.Succeeded or PaymentStates.Processing or PaymentStates.Unknown)
            return Results.Ok(DepositEndpoints.ToDto(existing, http, quote));
        if (balance.Due <= 0) return Problem(StatusCodes.Status409Conflict, "nothing_due", "Nothing is left to pay.");

        var now = clock.GetUtcNow();
        var payment = existing is { Status: PaymentStates.Created } ? existing
            : new Deposit(Guid.NewGuid(), tenant.TenantId!, project.Id, quote!.Id, tenant.CustomerId, balance.Due, balance.Currency, 0m,
                PaymentStates.Created, flow.ProviderName, now, null, Kind: PaymentKinds.Balance);
        if (payment != existing) await deposits.CreateAsync(payment, ct);
        payment = await flow.OpenCheckoutAsync(payment, quote!.Issue!.Number, ct);
        return payment.Status switch
        {
            PaymentStates.Failed => Problem(StatusCodes.Status502BadGateway, "payment_refused", "The payment provider refused this payment. Try again, or contact the store."),
            PaymentStates.Unknown => Results.Accepted($"/api/deposits/{payment.Id}", DepositEndpoints.ToDto(payment, http, quote)),
            _ => Results.Created($"/api/deposits/{payment.Id}", DepositEndpoints.ToDto(payment, http, quote)),
        };
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
