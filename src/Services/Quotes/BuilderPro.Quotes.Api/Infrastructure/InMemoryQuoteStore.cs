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
