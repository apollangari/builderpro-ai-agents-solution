using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace BuilderPro.Gateway;

/// <summary>Per-IP budgets per minute. All values come from configuration (Gateway:RateLimits).</summary>
public sealed class RateLimitOptions
{
    public int PublicPerMinute { get; set; } = 300;   // cheap, anonymous reads
    public int DefaultPerMinute { get; set; } = 100;  // authenticated reads
    public int WritesPerMinute { get; set; } = 30;    // anything that changes state
    public string[] PublicPaths { get; set; } = ["/api/auth-config"];
}

/// <summary>
/// Tiered, per-IP, fixed-window rate limiting. QueueLimit = 0: when a budget is spent the
/// gateway sheds load immediately with 429 + Retry-After instead of queueing unboundedly.
/// Services keep their own limits too (defence in depth).
/// </summary>
public static class RateLimiting
{
    public const string Policy = "tiered";

    public static IServiceCollection AddTieredRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Gateway:RateLimits").Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            limiter.AddPolicy(Policy, http =>
            {
                var tier = TierFor(http.Request, options);
                var permits = tier switch
                {
                    "public" => options.PublicPerMinute,
                    "writes" => options.WritesPerMinute,
                    _ => options.DefaultPerMinute,
                };
                // Partition key = tier + client IP: each IP has its own counter per tier.
                var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"{tier}:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });
        return services;
    }

    internal static string TierFor(HttpRequest request, RateLimitOptions options)
    {
        if (options.PublicPaths.Any(p => request.Path.StartsWithSegments(p))) return "public";
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)) return "writes";
        return "default";
    }
}
