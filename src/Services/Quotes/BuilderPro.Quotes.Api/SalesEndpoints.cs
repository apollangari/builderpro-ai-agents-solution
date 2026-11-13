using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using BuilderPro.Quotes.Domain;
using BuilderPro.Security;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Quotes.Api;

public sealed record SaleDto(Guid ProjectId, string ProjectTitle, string? CustomerId, int QuoteNumber, decimal QuoteTotal, decimal Deposit, string Currency, DateTimeOffset PaidAt);

public sealed record SalespersonCommissionDto(
    string SalespersonKey, string? Name, int Sales, decimal SoldTotal, string Currency, decimal? CommissionPercent, decimal? Commission,
    IReadOnlyList<SaleDto> Lines);

public sealed record CommissionReportDto(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<SalespersonCommissionDto> Salespeople);

/// <summary>
/// 1i: salespeople prepare projects and quotes on a customer's behalf (POST /api/projects with customerId).
/// The customer accepts and pays the deposit through their own flow, exactly as if they had built it.
/// A sale counts once its deposit is PAID; it's credited to the salesperson who prepared the project.
///
/// GET /api/sales/commission?from=&amp;to= : a salesperson (scope "sales") sees their own line; a manager
/// (scope "sales.report") sees everyone's. The rate is per store (Sales:CommissionPercent:{tenant} or
/// :Default); without one the report shows sales only.
/// </summary>
public static class SalesEndpoints
{
    public const string SalesScope = "sales";
    public const string ReportScope = "sales.report";
    public const int MaxDays = 366;

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/sales").RequireAuthorization()
            .MapGet("/commission", CommissionAsync)
            .WithSummary("Sales credited to salespeople (paid deposits) in a period, with commission when the store sets a rate.");
        return app;
    }

    /// <summary>A readable name for the salesperson, from the token (shown to the customer as "Prepared by").</summary>
    public static string NameOf(ClaimsPrincipal principal, UserContext user)
    {
        var name = principal.FindFirst("name")?.Value
                   ?? string.Join(' ', new[] { principal.FindFirst("given_name")?.Value, principal.FindFirst("family_name")?.Value }.Where(x => !string.IsNullOrWhiteSpace(x)));
        name = string.IsNullOrWhiteSpace(name) ? user.UserName ?? "Store salesperson" : name;
        return name.Length > 80 ? name[..80] : name;
    }

    internal static async Task<IResult> CommissionAsync(
        DateTimeOffset? from, DateTimeOffset? to, HttpContext http, ITenantLookup tenants, IDepositStore deposits, IProjectStore projects,
        IQuoteStore quotes, IConfiguration config, TimeProvider clock, AuthOptions auth, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");
        var manager = tenant.HasScope(ReportScope);
        if (tenant.CustomerId is not null || (!manager && !tenant.HasScope(SalesScope)))
            return Problem(StatusCodes.Status403Forbidden, "missing_scope", $"This needs the {SalesScope} or {ReportScope} scope.");

        // Default: this calendar month (UTC).
        var now = clock.GetUtcNow();
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var start = from ?? month;
        var end = to ?? month.AddMonths(1);
        if (start >= end || (end - start).TotalDays > MaxDays)
            return Problem(StatusCodes.Status400BadRequest, "invalid_period", $"from must be before to, at most {MaxDays} days apart.");

        var me = ClaimsNormalizer.FromPrincipal(http.User, auth).UserKey;
        var percent = config.GetValue<decimal?>($"Sales:CommissionPercent:{tenant.TenantId}") ?? config.GetValue<decimal?>("Sales:CommissionPercent:Default");

        var sales = new List<(Project Project, SaleDto Sale)>();
        foreach (var d in await deposits.ListPaidAsync(tenant.TenantId!, start, end, ct))
        {
            var project = await projects.FindAsync(tenant.TenantId!, null, d.ProjectId, ct);
            if (project?.SalespersonKey is null || (!manager && project.SalespersonKey != me)) continue;
            var quote = await quotes.FindAsync(tenant.TenantId!, null, d.QuoteId, ct);
            if (quote?.Issue is null) continue;
            sales.Add((project, new SaleDto(project.Id, project.Title, project.CustomerId, quote.Issue.Number, quote.Priced.Total.Amount, d.Amount, d.Currency, d.PaidAt!.Value)));
        }

        var rows = sales
            .GroupBy(s => s.Project.SalespersonKey!)
            .Select(g =>
            {
                var sold = g.Sum(s => s.Sale.QuoteTotal);
                var currency = g.First().Sale.Currency;
                decimal? commission = percent is decimal p ? DepositPolicy.AmountFor(new Money(sold, currency), p).Amount : null;
                return new SalespersonCommissionDto(g.Key, g.Last().Project.SalespersonName, g.Count(), sold, currency, percent, commission,
                    [.. g.Select(s => s.Sale)]);
            })
            .OrderByDescending(r => r.SoldTotal)
            .ToList();

        // A salesperson with no sales yet still sees their own (empty) line.
        if (!manager && rows.Count == 0)
            rows.Add(new SalespersonCommissionDto(me, NameOf(http.User, ClaimsNormalizer.FromPrincipal(http.User, auth)), 0, 0m, "USD", percent, percent is null ? null : 0m, []));

        return Results.Ok(new CommissionReportDto(start, end, rows));
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>Is this a customer of the caller's store? Asked from the Identity service with the caller's own token.</summary>
public interface ICustomerDirectory
{
    /// <summary>True / false, or null when Identity couldn't answer.</summary>
    Task<bool?> IsCustomerAsync(HttpContext http, string customerId, CancellationToken ct);
}

public sealed class IdentityCustomerDirectory(HttpClient client, ILogger<IdentityCustomerDirectory> log) : ICustomerDirectory
{
    public async Task<bool?> IsCustomerAsync(HttpContext http, string customerId, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(http.Request.Headers[HeaderNames.Authorization], out var auth) || auth.Parameter is null) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/identity/customers/{Uri.EscapeDataString(customerId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Parameter);
        var hint = http.Request.Headers[TenantHint.Header].ToString();
        if (ClaimsNormalizer.IsIdentifier(hint)) request.Headers.Add(TenantHint.Header, hint);
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return true;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return false;
            log.LogWarning("Customer check answered {Status}", (int)response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            log.LogWarning(ex, "Customer check against the Identity service failed");
            return null;
        }
    }
}
