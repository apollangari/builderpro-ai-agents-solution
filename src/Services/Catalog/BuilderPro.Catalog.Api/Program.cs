using BuilderPro.Catalog.Api;
using BuilderPro.Catalog.Api.Domain;
using BuilderPro.Catalog.Api.Infrastructure;
using BuilderPro.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Own database only (database-per-service). No connection string -> startup fails.
builder.AddNpgsqlDataSource("catalogdb");

// Redis "cache" (LEARN 2f): only when the AppHost provides one. No cache configured = no caching,
// never an in-process fallback (a per-process cache can't be invalidated from Identity).
if (TenantCacheSetup.HasCache(builder.Configuration)) builder.AddRedisDistributedCache("cache");

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<PostgresProductStore>();
if (TenantCacheSetup.HasCache(builder.Configuration))
{
    var ttl = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("Catalog:CacheSeconds") ?? 300);
    builder.Services.AddSingleton<IProductStore>(sp => new CachingProductStore(
        sp.GetRequiredService<PostgresProductStore>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        ttl,
        sp.GetRequiredService<ILogger<CachingProductStore>>()));
}
else
{
    builder.Services.AddSingleton<IProductStore>(sp => sp.GetRequiredService<PostgresProductStore>());
}
builder.Services.AddBuilderProAuth(builder.Configuration);

// "Which tenant is this caller in?" -> asks the Identity service (BuilderPro.Security, LEARN 2a).
builder.Services.AddTenantLookup(builder.Configuration);

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
