using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// One call wires every service the same way. Read this file once and you know how
/// telemetry, health and outbound HTTP behave in ALL BuilderPro services.
/// </summary>
public static class ServiceDefaultsExtensions
{
    private const string HealthPath = "/health";
    private const string AlivePath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Retries with jitter, timeouts and a circuit breaker on EVERY HttpClient.
            http.AddStandardResilienceHandler();
            // "http://me-api" resolves to the real address Aspire (or Azure) assigned.
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                // Health probes every few seconds would drown the real traces.
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments(HealthPath) &&
                    !ctx.Request.Path.StartsWithSegments(AlivePath))
                .AddHttpClientInstrumentation());

        // Aspire sets OTEL_EXPORTER_OTLP_ENDPOINT to its dashboard. In Azure the same
        // variable points at the collector / App Insights. No endpoint = no export.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // "live" = the process responds. Dependency checks (database, broker) are added
        // by each service WITHOUT the "live" tag, so they only affect readiness (/health).
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // /health = ready to take traffic (all checks); /alive = process is up (live only).
        app.MapHealthChecks(HealthPath);
        app.MapHealthChecks(AlivePath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
        });
        return app;
    }
}
