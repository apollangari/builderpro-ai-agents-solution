using BuilderPro.Quotes.Domain;

namespace BuilderPro.Quotes.Api;

/// <summary>
/// A stored quote: who asked, for which tenant (and customer), and the priced result exactly as
/// the pricer computed it. Totals are STORED, not recomputed on read: a quote is a snapshot of
/// the prices at the time, even if the catalog changes later.
/// </summary>
public sealed record Quote(
    Guid Id,
    string TenantId,
    string? CustomerId,     // null when created by retailer staff
    string CreatedBy,       // UserKey of the creator
    string Status,          // "draft"; for project quotes "issued" then "accepted" (2n-f)
    PricedQuote Priced,
    DateTimeOffset CreatedAt,
    GuidedInfo? Guided = null,    // 2n-e: set for guided quotes
    QuoteIssue? Issue = null);    // 2n-f: set for numbered quotes issued from a project

/// <summary>
/// What a guided quote was built from (2n-e): the project ZIP, the request as sent (answers and
/// measurements, never prices) and each line's group ("product", "minimum", "options", "protection").
/// </summary>
public sealed record GuidedInfo(string Zip, string RequestJson, IReadOnlyList<string> LineGroups);

/// <summary>A numbered quote (2n-f): Q-{Number} per store, held at its price until ValidUntil once accepted.</summary>
public sealed record QuoteIssue(int Number, DateTimeOffset ValidUntil, Guid ProjectId, DateTimeOffset? AcceptedAt = null);

public interface IQuoteStore
{
    Task InitializeAsync(CancellationToken ct);
    Task CreateAsync(Quote quote, CancellationToken ct);

    /// <summary>Stores an issued quote with the store's next number (1001, 1002, ...) and returns it numbered.</summary>
    Task<Quote> IssueAsync(Quote quote, CancellationToken ct);

    /// <summary>issued -> accepted for THIS customer's quote, only while valid. False when it isn't theirs, isn't issued, or expired.</summary>
    Task<bool> AcceptAsync(string tenantId, string customerId, Guid id, DateTimeOffset at, CancellationToken ct);

    /// <summary>One quote of ONE tenant. With a customerId, only that customer's quote.</summary>
    Task<Quote?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct);

    /// <summary>Newest first. With a customerId, only that customer's quotes; without, all of the tenant's.</summary>
    Task<IReadOnlyList<Quote>> ListAsync(string tenantId, string? customerId, int limit, CancellationToken ct);

    Task<bool> PingAsync(CancellationToken ct);
}
