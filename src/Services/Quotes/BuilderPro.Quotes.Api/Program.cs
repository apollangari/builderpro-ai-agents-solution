using BuilderPro.Quotes.Api;
using BuilderPro.Quotes.Api.Infrastructure;
using BuilderPro.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Own database only (database-per-service). No connection string -> startup fails.
builder.AddNpgsqlDataSource("quotesdb");

// Redis "cache" (LEARN 2f): only when the AppHost provides one. No cache configured = no caching,
// never an in-process fallback (a per-process cache can't be invalidated from Identity).
if (TenantCacheSetup.HasCache(builder.Configuration)) builder.AddRedisDistributedCache("cache");

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IQuoteStore, PostgresQuoteStore>();
builder.Services.AddSingleton<IProjectStore, PostgresProjectStore>();   // 2n-f
builder.Services.AddSingleton<IDepositStore, PostgresDepositStore>();   // 6a
builder.Services.AddSingleton<IVisitStore, PostgresVisitStore>();       // 6b

// 6d: the payment provider. "simulated" (default) = our test-mode page; "stripe" = Stripe Checkout + signed webhook.
if (DepositEndpoints.Provider(builder.Configuration) == "stripe")
{
    var stripe = builder.Configuration.GetSection("Payments:Stripe").Get<StripeOptions>() ?? new StripeOptions();
    stripe.Validate(builder.Environment.IsProduction());   // test keys only outside Production
    builder.Services.AddSingleton(stripe);
    builder.Services.AddHttpClient<IPaymentProvider, StripePayments>();
}
else
{
    builder.Services.AddSingleton<IPaymentProvider, SimulatedPayments>();
}
builder.Services.AddScoped<PaymentFlow>();
builder.Services.AddSingleton<PaymentReconciler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PaymentReconciler>());   // 6d: reconciliation
builder.Services.AddSingleton<IInstallStore, PostgresInstallStore>();   // 6c
// 4b: automated scheduling calls. Telephony is simulated until a provider (ACS / Twilio) is configured.
builder.Services.AddSingleton<ICallStore, PostgresCallStore>();
// Final gaps: deliver the outbox (email via SMTP/Mailpit; texts simulated until an SMS provider is configured).
builder.Services.AddSingleton<PostgresContactStore>();
builder.Services.AddSingleton<IContactStore>(sp => sp.GetRequiredService<PostgresContactStore>());
builder.Services.AddSingleton<INotificationOutbox>(sp => sp.GetRequiredService<PostgresContactStore>());
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<ISmsSender, SimulatedSmsSender>();
builder.Services.AddSingleton<NotificationSender>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationSender>());
builder.Services.AddSingleton<ITelephony, SimulatedTelephony>();
builder.Services.AddSingleton<CallDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CallDispatcher>());
builder.Services.AddHttpClient<IProfileReader, IdentityProfileReader>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Tenancy:IdentityBaseUrl"] ?? "http://identity"));
builder.Services.AddBuilderProAuth(builder.Configuration);

// Tenant, customer and scopes: asked from the Identity service (LEARN 2a).
builder.Services.AddTenantLookup(builder.Configuration);

// List prices: read from the Catalog service with the caller's token (LEARN 2d).
// 1i: "is this a customer of my store?" asked from Identity with the salesperson's token.
builder.Services.AddHttpClient<ICustomerDirectory, IdentityCustomerDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Tenancy:IdentityBaseUrl"] ?? "http://identity"));

// 6c: the channels a customer consented to for order updates, asked from Identity.
builder.Services.AddHttpClient<IContactChannels, IdentityContactChannels>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Tenancy:IdentityBaseUrl"] ?? "http://identity"));

builder.Services.AddHttpClient<ICatalogPrices, CatalogPriceClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Quotes:CatalogBaseUrl"] ?? "http://catalog"));

builder.Services.AddHealthChecks()
    .AddCheck<QuoteStoreHealthCheck>("quote-store");   // readiness only (no "live" tag)

var app = builder.Build();

await app.Services.GetRequiredService<IQuoteStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IProjectStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IDepositStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IVisitStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IInstallStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<ICallStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IContactStore>().InitializeAsync(CancellationToken.None);

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapQuoteEndpoints();
app.MapGuidedQuoteEndpoints();   // 2n-b..e
app.MapProjectEndpoints();       // 2n-f
app.MapDepositEndpoints();       // 6a
app.MapSalesEndpoints();         // 1i
app.MapCallEventEndpoints();     // 4b: telephony provider callback (signed)
app.MapStaffQueue();             // 3e: the store desk's work queue

app.Run();

/// <summary>Readiness: can we reach OUR database? (Liveness never checks dependencies.)</summary>
internal sealed class QuoteStoreHealthCheck(IQuoteStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.PingAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("ping returned false");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("quote store unreachable", ex);
        }
    }
}

public partial class Program;
