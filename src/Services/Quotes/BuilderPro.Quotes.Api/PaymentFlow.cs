using BuilderPro.Quotes.Domain;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// 6d: the ONE place that moves a deposit through the payment state machine (<see cref="PaymentStates"/>).
/// Endpoints, the webhook and the reconciler all go through here, so the rules can't drift apart:
///  - open checkout: created -> processing; the provider refusing -> failed; a timeout/5xx -> unknown;
///  - provider answer (webhook, lookup, or our test page): processing/unknown -> succeeded | failed;
///  - succeeded holds the price and moves the project to the measure visit, once.
/// </summary>
public sealed class PaymentFlow(IDepositStore deposits, IProjectStore projects, IQuoteStore quotes, IPaymentProvider provider, IConfiguration config, TimeProvider clock, ILogger<PaymentFlow> log)
{
    public string ProviderName => provider.Name;

    /// <summary>Opens (or re-opens after an unknown) the provider's checkout. Returns the deposit as it now stands.</summary>
    public async Task<Deposit> OpenCheckoutAsync(Deposit deposit, int quoteNumber, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (deposit.Status == PaymentStates.Created)
        {
            await deposits.TransitionAsync(deposit.TenantId, deposit.Id, PaymentStates.Created, PaymentStates.Processing, now, PaymentSources.Checkout, $"opening {provider.Name} checkout", null, ct);
            deposit = deposit with { Status = PaymentStates.Processing };
        }
        try
        {
            var checkout = await provider.StartAsync(deposit, quoteNumber, config["Payments:ReturnBaseUrl"] ?? "http://localhost:4200", ct);
            await deposits.SetCheckoutAsync(deposit.TenantId, deposit.Id, checkout.ProviderRef, checkout.Url, checkout.ExpiresAt, ct);
            deposit = deposit with { ProviderRef = checkout.ProviderRef, CheckoutUrl = checkout.Url, CheckoutExpiresAt = checkout.ExpiresAt };
            if (deposit.Status == PaymentStates.Unknown)   // reconciled: the session exists after all
                deposit = await MoveAsync(deposit, PaymentStates.Processing, PaymentSources.Lookup, "checkout found on retry", null, ct);
            return deposit;
        }
        catch (PaymentRefusedException ex)
        {
            return await MoveAsync(deposit, PaymentStates.Failed, PaymentSources.Checkout, ex.Message, null, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Opening a {Provider} checkout timed out or failed for deposit {Deposit}", provider.Name, deposit.Id);
            return deposit.Status == PaymentStates.Processing
                ? await MoveAsync(deposit, PaymentStates.Unknown, PaymentSources.Checkout, "no answer from the provider", null, ct)
                : deposit;
        }
    }

    /// <summary>
    /// The provider says it was paid. Never succeeds on a different amount or currency (that needs a person).
    /// Already succeeded: a no-op (webhook replays, a lookup racing a webhook).
    /// </summary>
    public async Task<Deposit> PaidAsync(Deposit deposit, long? amountMinor, string? currency, string paymentRef, string source, CancellationToken ct)
    {
        if (deposit.Paid) return deposit;
        if (!PaymentStates.NeedsAnswer(deposit.Status))
        {
            log.LogError("Deposit {Deposit} was paid ({Ref}) but is {Status}; needs a person (refund or manual fix)", deposit.Id, paymentRef, deposit.Status);
            return deposit;
        }
        if (amountMinor != StripeForm.MinorUnits(deposit.Amount) || !string.Equals(currency, deposit.Currency, StringComparison.OrdinalIgnoreCase))
        {
            log.LogError("Deposit {Deposit}: provider took {Amount} {Currency}, expected {Expected} {DepositCurrency}; not marked paid",
                deposit.Id, amountMinor, currency, StripeForm.MinorUnits(deposit.Amount), deposit.Currency);
            return deposit;
        }
        var paid = await MoveAsync(deposit, PaymentStates.Succeeded, source, paymentRef, paymentRef, ct);
        if (paid.Paid && paid.Kind == PaymentKinds.Deposit) await HoldPriceAsync(paid, ct);   // a paid balance just settles the job
        return paid;
    }

    /// <summary>The provider says it failed (declined, expired).</summary>
    public Task<Deposit> FailedAsync(Deposit deposit, string source, string detail, CancellationToken ct) =>
        PaymentStates.NeedsAnswer(deposit.Status) ? MoveAsync(deposit, PaymentStates.Failed, source, detail, null, ct) : Task.FromResult(deposit);

    /// <summary>Reconciliation: ask the provider about a processing/unknown payment and apply its answer.</summary>
    public async Task<Deposit> ReconcileAsync(Deposit deposit, CancellationToken ct)
    {
        if (!PaymentStates.NeedsAnswer(deposit.Status)) return deposit;
        if (deposit.Status == PaymentStates.Unknown && deposit.ProviderRef is null)
        {
            // Same idempotency key AND the same request (so the quote number too): finds the session if the provider made one.
            var quote = await quotes.FindAsync(deposit.TenantId, null, deposit.QuoteId, ct);
            return await OpenCheckoutAsync(deposit, quote?.Issue?.Number ?? 0, ct);
        }
        ProviderStatus status;
        try
        {
            status = await provider.LookupAsync(deposit, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Status lookup for deposit {Deposit} failed; will retry", deposit.Id);
            return deposit;
        }
        return status.Outcome switch
        {
            ProviderOutcome.Paid => await PaidAsync(deposit, status.AmountMinor, status.Currency, status.PaymentRef ?? deposit.ProviderRef ?? "", PaymentSources.Lookup, ct),
            ProviderOutcome.Failed => await FailedAsync(deposit, PaymentSources.Lookup, status.Detail ?? "failed", ct),
            _ when deposit.Status == PaymentStates.Unknown => await MoveAsync(deposit, PaymentStates.Processing, PaymentSources.Lookup, status.Detail ?? "still open", null, ct),
            _ => deposit,
        };
    }

    private async Task<Deposit> MoveAsync(Deposit d, string to, string source, string? detail, string? paidBy, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!await deposits.TransitionAsync(d.TenantId, d.Id, d.Status, to, now, source, detail, paidBy, ct))
            return await deposits.FindAsync(d.TenantId, null, d.Id, ct) ?? d;   // someone else moved it first: report what it is now
        log.LogInformation("Deposit {Deposit}: {From} -> {To} ({Source})", d.Id, d.Status, to, source);
        return to == PaymentStates.Succeeded ? d with { Status = to, PaidAt = now, PaidBy = paidBy } : d with { Status = to };
    }

    /// <summary>Paid: the price is held and the project moves on to the measure visit (only from the deposit step of the same quote).</summary>
    private async Task HoldPriceAsync(Deposit deposit, CancellationToken ct)
    {
        var project = await projects.FindAsync(deposit.TenantId, null, deposit.ProjectId, ct);
        if (project is not null && project.QuoteId == deposit.QuoteId && project.Stage == ProjectStages.Deposit)
            await projects.UpdateAsync(project with { Stage = ProjectStages.Measure, UpdatedAt = clock.GetUtcNow() }, ct);
        else
            log.LogWarning("Deposit {Deposit} succeeded but project {Project} wasn't waiting for it; check it", deposit.Id, deposit.ProjectId);
    }
}

/// <summary>
/// 6d: reconciliation in the background. Every Payments:ReconcileSeconds (30) it asks the provider about
/// payments that are unknown, or processing past their checkout's expiry (a missed webhook).
/// </summary>
public sealed class PaymentReconciler(IServiceScopeFactory scopes, IConfiguration config, ILogger<PaymentReconciler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromSeconds(config.GetValue<int?>("Payments:ReconcileSeconds") ?? 30);
        using var timer = new PeriodicTimer(every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Payment reconciliation pass failed");
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var deposits = scope.ServiceProvider.GetRequiredService<IDepositStore>();
        var flow = scope.ServiceProvider.GetRequiredService<PaymentFlow>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var waiting = await deposits.AwaitingAnswerAsync(clock.GetUtcNow(), 50, ct);
        foreach (var d in waiting) await flow.ReconcileAsync(d, ct);
        return waiting.Count;
    }
}
