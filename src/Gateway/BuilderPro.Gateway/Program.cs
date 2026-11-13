using BuilderPro.Gateway;
using BuilderPro.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();                                  // OTel, health, discovery, resilience

// Kestrel hardening: one client can't exhaust the gateway with huge bodies or connections.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("Gateway:MaxRequestBytes") ?? 1_048_576;
    kestrel.Limits.MaxConcurrentConnections = builder.Configuration.GetValue<long?>("Gateway:MaxConcurrentConnections") ?? 1000;
});

builder.Services.AddBuilderProAuth(builder.Configuration);    // same generic OIDC as every service
builder.Services.AddTieredRateLimiting(builder.Configuration);

// Routes + clusters from appsettings.json ("ReverseProxy" section).
// Destinations are logical names (http://me-api), resolved by Aspire service discovery
// locally and by configuration/DNS in Azure: no ports or hosts in code.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

app.UseCorrelationId();     // first: everything after it (logs, traces, services) sees the id
app.UseRateLimiter();
app.UseAuthentication();    // validates the bearer token ONCE at the edge...
app.UseAuthorization();     // ...and each YARP route declares its AuthorizationPolicy

app.MapDefaultEndpoints();  // /health, /alive (anonymous, not proxied)

// Every /api/* route is proxied. Services STILL validate the token themselves:
// the gateway is a filter, not the only lock (defence in depth / Zero Trust).
app.MapReverseProxy().RequireRateLimiting(RateLimiting.Policy);

app.Run();

public partial class Program;
