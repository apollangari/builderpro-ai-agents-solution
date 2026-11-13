using BuilderPro.Catalog.Api;
using BuilderPro.Catalog.Api.Domain;
using BuilderPro.Catalog.Api.Infrastructure;
using BuilderPro.Catalog.Api.Tenancy;
using BuilderPro.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Own database only (database-per-service). No connection string -> startup fails.
builder.AddNpgsqlDataSource("catalogdb");

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IProductStore, PostgresProductStore>();
builder.Services.AddBuilderProAuth(builder.Configuration);

// Typed client for the Identity service. "http://identity" is a LOGICAL name resolved by
// service discovery (Aspire injects services__identity__http__0). Retries, timeouts and the
// circuit breaker come from ServiceDefaults.
builder.Services.AddHttpClient<ITenantLookup, IdentityTenantLookup>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Catalog:IdentityBaseUrl"] ?? "http://identity"));

builder.Services.AddHealthChecks()
    .AddCheck<ProductStoreHealthCheck>("product-store");   // readiness only (no "live" tag)

var app = builder.Build();

await app.Services.GetRequiredService<IProductStore>().InitializeAsync(CancellationToken.None);
await DevSeed.RunAsync(app);

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapCatalogEndpoints();

app.Run();

/// <summary>Readiness: can we reach OUR database? (Liveness never checks dependencies.)</summary>
internal sealed class ProductStoreHealthCheck(IProductStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.PingAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("ping returned false");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("product store unreachable", ex);
        }
    }
}

public partial class Program;
