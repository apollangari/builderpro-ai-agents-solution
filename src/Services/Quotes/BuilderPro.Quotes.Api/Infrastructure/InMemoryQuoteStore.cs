namespace BuilderPro.Quotes.Api.Infrastructure;

/// <summary>For tests only. Production always uses Postgres; there is no silent fallback.</summary>
public sealed class InMemoryQuoteStore : IQuoteStore
{
    private readonly Lock _gate = new();
    private readonly List<Quote> _rows = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

    public Task CreateAsync(Quote quote, CancellationToken ct)
    {
        lock (_gate) _rows.Add(quote);
        return Task.CompletedTask;
    }

    public Task<Quote> IssueAsync(Quote quote, CancellationToken ct)
    {
        lock (_gate)
        {
            var next = _rows.Where(q => q.TenantId == quote.TenantId && q.Issue is not null).Select(q => q.Issue!.Number).DefaultIfEmpty(1000).Max() + 1;
            var numbered = quote with { Issue = quote.Issue! with { Number = next } };
            _rows.Add(numbered);
            return Task.FromResult(numbered);
        }
    }

    public Task<bool> AcceptAsync(string tenantId, string customerId, Guid id, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _rows.FindIndex(q => q.Id == id && q.TenantId == tenantId && q.CustomerId == customerId
                                         && q.Status == "issued" && q.Issue is not null && q.Issue.ValidUntil > at);
            if (i < 0) return Task.FromResult(false);
            _rows[i] = _rows[i] with { Status = "accepted", Issue = _rows[i].Issue! with { AcceptedAt = at } };
            return Task.FromResult(true);
        }
    }

    public Task<Quote?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Visible(tenantId, customerId).FirstOrDefault(q => q.Id == id));
    }

    public Task<IReadOnlyList<Quote>> ListAsync(string tenantId, string? customerId, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Quote>>([.. Visible(tenantId, customerId).OrderByDescending(q => q.CreatedAt).Take(limit)]);
        }
    }

    private IEnumerable<Quote> Visible(string tenantId, string? customerId) =>
        _rows.Where(q => q.TenantId == tenantId && (customerId is null || q.CustomerId == customerId));
}
