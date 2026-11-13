using BuilderPro.Identity.Api;
using BuilderPro.Identity.Api.Domain;
using BuilderPro.Identity.Api.Infrastructure;
using BuilderPro.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Aspire client integration: registers NpgsqlDataSource from ConnectionStrings:identitydb,
// plus a readiness health check and OpenTelemetry tracing of every SQL command.
// No connection string -> startup fails (there is NO in-memory fallback at runtime).
builder.AddNpgsqlDataSource("identitydb");

// Redis "cache" (LEARN 2f): only when the AppHost provides one. No cache configured = no caching,
// never an in-process fallback (a per-process cache can't be invalidated from Identity).
if (TenantCacheSetup.HasCache(builder.Configuration)) builder.AddRedisDistributedCache("cache");

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMembershipStore, PostgresMembershipStore>();
// Membership changes delete the cached tenant lookups of the other services (LEARN 2f).
builder.Services.AddTenantCacheInvalidation(builder.Configuration);
builder.Services.AddBuilderProAuth(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(IdentityEndpoints.AdminPolicy, p => p.RequireAuthenticatedUser()
        .RequireRole(builder.Configuration["Auth:AdminRole"] ?? "platform-admin"));

builder.Services.AddHealthChecks()
    .AddCheck<MembershipStoreHealthCheck>("membership-store");   // readiness only (no "live" tag)

var app = builder.Build();

await app.Services.GetRequiredService<IMembershipStore>().InitializeAsync(CancellationToken.None);
await DevSeed.RunAsync(app);

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapIdentityEndpoints();

app.Run();

/// <summary>Readiness: can we reach OUR database? (Liveness never checks dependencies.)</summary>
internal sealed class MembershipStoreHealthCheck(IMembershipStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.PingAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("ping returned false");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("membership store unreachable", ex);
        }
    }
}

public partial class Program;
