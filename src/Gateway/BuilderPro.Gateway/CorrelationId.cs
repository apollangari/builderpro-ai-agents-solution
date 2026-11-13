using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BuilderPro.Gateway;

/// <summary>
/// Every request gets an X-Correlation-ID: kept if the caller sent a sane one, otherwise
/// generated. It's forwarded to services (YARP copies request headers), echoed in the response,
/// attached to the trace, and put in every log line's scope, so support can say "give me the
/// correlation id" and find the whole story.
///
/// W3C trace context (traceparent) already links spans; the correlation id is the
/// human-friendly handle that also survives systems that don't speak OpenTelemetry.
/// </summary>
public static partial class CorrelationId
{
    public const string Header = "X-Correlation-ID";

    // Accept only short, boring ids: a caller-supplied header is untrusted input
    // that ends up in logs (log injection) and in other systems.
    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex Valid();

    public static string Resolve(string? incoming) =>
        incoming is not null && Valid().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var id = Resolve(context.Request.Headers[Header].FirstOrDefault());

            context.Request.Headers[Header] = id;   // forwarded downstream by YARP
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Header] = id;
                return Task.CompletedTask;
            });
            Activity.Current?.SetTag("correlation.id", id);

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Gateway");
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            {
                await next(context);
            }
        });
}
