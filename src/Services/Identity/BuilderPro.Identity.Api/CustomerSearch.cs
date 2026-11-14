using System.Security.Claims;
using BuilderPro.Identity.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Identity.Api;

public sealed record CustomerDto(string CustomerId, string Name, string Email, string? Phone, string? PostalCode);

/// <summary>6c: how the store may contact this customer about their order, from their LATEST consent.</summary>
public sealed record ContactChannelsDto(string CustomerId, IReadOnlyList<string> Channels, string Hours);

/// <summary>
/// 1i: a salesperson (staff membership with the "sales" scope) looks up the store's customers to prepare
/// a project for one of them. Only customers of the caller's OWN store come back: the directory search is
/// matched against this tenant's active customer memberships. Contact details are shown to the
/// salesperson because serving the customer is the point; nothing here changes the customer.
/// </summary>
public static class CustomerSearch
{
    public const string SalesScope = "sales";
    public const string SchedulingScope = "scheduling.write";
    public const int MinQuery = 2;
    public const int MaxQuery = 80;
    public const int MaxResults = 20;

    public static RouteGroupBuilder MapCustomerSearch(this RouteGroupBuilder api)
    {
        api.MapGet("/customers", SearchAsync).WithSummary("Salesperson: find this store's customers by name, email or phone.");
        api.MapGet("/customers/{customerId}", GetAsync).WithSummary("Salesperson: is this a customer of my store? (the Quotes service asks before an on-behalf project).");
        api.MapGet("/customers/{customerId}/channels", ChannelsAsync).WithSummary("Store staff: the channels this customer consented to for order updates (email always; text / call only with consent).");
        return api;
    }

    internal static async Task<IResult> SearchAsync(
        string? q, HttpContext http, ClaimsPrincipal principal, AuthOptions options, IMembershipStore store, IUserDirectory directory,
        TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var (user, tenant, problem) = await GateAsync(http, principal, options, store, clock, ct, SalesScope);
        if (problem is not null) return problem;
        var text = q?.Trim() ?? "";
        if (text.Length is < MinQuery or > MaxQuery)
            return Problem(StatusCodes.Status400BadRequest, "invalid_query", $"Type {MinQuery} to {MaxQuery} characters.");

        IReadOnlyList<DirectoryUser> hits;
        try
        {
            hits = await directory.SearchAsync(text, MaxResults * 3, ct);
        }
        catch (HttpRequestException ex)
        {
            logs.CreateLogger("CustomerSearch").LogWarning(ex, "Directory search failed");
            return Problem(StatusCodes.Status503ServiceUnavailable, "directory_unavailable", "Customer search isn't available right now.");
        }

        var now = clock.GetUtcNow();
        var customers = (await store.ListByTenantAsync(tenant!, ct))
            .Where(m => m.CustomerId is not null && m.IsActive(now))
            .ToDictionary(m => m.UserKey, m => m.CustomerId!, StringComparer.Ordinal);

        return Results.Ok(hits
            .Select(h => (Hit: h, Key: ExternalIdentityKey.ForIssuerAndSubject(user!.Issuer, h.Id)))
            .Where(x => customers.ContainsKey(x.Key))
            .Take(MaxResults)
            .Select(x => new CustomerDto(customers[x.Key], $"{x.Hit.FirstName} {x.Hit.LastName}".Trim(), x.Hit.Email, x.Hit.Phone, x.Hit.PostalCode)));
    }

    internal static async Task<IResult> GetAsync(
        string customerId, HttpContext http, ClaimsPrincipal principal, AuthOptions options, IMembershipStore store, TimeProvider clock, CancellationToken ct)
    {
        var (_, tenant, problem) = await GateAsync(http, principal, options, store, clock, ct, SalesScope);
        if (problem is not null) return problem;
        if (!ClaimsNormalizer.IsIdentifier(customerId)) return Results.NotFound();
        var now = clock.GetUtcNow();
        var found = (await store.ListByTenantAsync(tenant!, ct)).Any(m => m.CustomerId == customerId && m.IsActive(now));
        return found ? Results.Ok(new { customerId }) : Results.NotFound();
    }

    internal static async Task<IResult> ChannelsAsync(
        string customerId, HttpContext http, ClaimsPrincipal principal, AuthOptions options, IMembershipStore store, IConsentStore consents,
        TimeProvider clock, CancellationToken ct)
    {
        var (_, tenant, problem) = await GateAsync(http, principal, options, store, clock, ct, SalesScope, SchedulingScope);
        if (problem is not null) return problem;
        if (!ClaimsNormalizer.IsIdentifier(customerId)) return Results.NotFound();
        var now = clock.GetUtcNow();
        var member = (await store.ListByTenantAsync(tenant!, ct)).FirstOrDefault(m => m.CustomerId == customerId && m.IsActive(now));
        if (member is null) return Results.NotFound();
        var latest = await consents.LatestAsync(member.UserKey, ct);
        return Results.Ok(new ContactChannelsDto(customerId, ChannelsFor(latest?.Consent), latest?.Hours ?? "business"));
    }

    /// <summary>Email about their own order always; text and calls only with the matching consent (fail closed: none recorded = email only).</summary>
    public static IReadOnlyList<string> ChannelsFor(string? consent) => consent switch
    {
        "both" => ["email", "sms", "call"],
        "text" => ["email", "sms"],
        "call" => ["email", "call"],
        _ => ["email"],
    };

    /// <summary>The caller must be STAFF of the resolved tenant (no customer id) with one of the scopes.</summary>
    private static async Task<(UserContext? User, string? Tenant, IResult? Problem)> GateAsync(
        HttpContext http, ClaimsPrincipal principal, AuthOptions options, IMembershipStore store, TimeProvider clock, CancellationToken ct,
        params string[] scopes)
    {
        var user = TenantHint.Apply(http, ClaimsNormalizer.FromPrincipal(principal, options));
        var result = TenantResolver.Resolve(user, await store.FindByUserAsync(user.UserKey, ct), clock.GetUtcNow());
        if (result.Outcome != TenantResolution.Resolved)
            return (null, null, Problem(result.Outcome == TenantResolution.Ambiguous ? StatusCodes.Status409Conflict : StatusCodes.Status403Forbidden, result.Code, "Could not determine your store."));
        var m = result.Membership!;
        if (m.CustomerId is not null || !scopes.Any(s => m.Scopes.Contains(s, StringComparer.Ordinal)))
            return (null, null, Problem(StatusCodes.Status403Forbidden, "sales_only", "Only store staff can look up customers."));
        return (user, m.TenantId, null);
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
