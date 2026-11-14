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
builder.Services.AddSingleton<IInstallStore, PostgresInstallStore>();   // 6c
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

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapQuoteEndpoints();
app.MapGuidedQuoteEndpoints();   // 2n-b..e
app.MapProjectEndpoints();       // 2n-f
app.MapDepositEndpoints();       // 6a
app.MapSalesEndpoints();         // 1i

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
