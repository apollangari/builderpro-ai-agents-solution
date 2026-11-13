using BuilderPro.Identity.Api.Domain;

namespace BuilderPro.Identity.Api.Infrastructure;

/// <summary>For tests only. Production always uses Postgres; there is no silent fallback.</summary>
public sealed class InMemoryMembershipStore(TimeProvider clock) : IMembershipStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string User), Membership> _rows = [];

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<Membership>> FindByUserAsync(string userKey, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Membership>>([.. _rows.Values.Where(m => m.UserKey == userKey)]);
        }
    }

    public Task<IReadOnlyList<Membership>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Membership>>([.. _rows.Values.Where(m => m.TenantId == tenantId).OrderBy(m => m.UserKey, StringComparer.Ordinal)]);
        }
    }

    public Task<Membership> UpsertAsync(string tenantId, string userKey, string? customerId, IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var id = _rows.TryGetValue((tenantId, userKey), out var existing) ? existing.Id : Guid.NewGuid();
            var membership = new Membership(id, tenantId, userKey, customerId, [.. scopes], expiresAt, null, null, existing?.CreatedAt ?? now);
            _rows[(tenantId, userKey)] = membership;
            return Task.FromResult(membership);
        }
    }

    public Task<Membership?> RevokeAsync(Guid id, string reason, CancellationToken ct)
    {
        lock (_gate)
        {
            var hit = _rows.FirstOrDefault(kv => kv.Value.Id == id);
            if (hit.Value is null) return Task.FromResult<Membership?>(null);
            var revoked = hit.Value with { RevokedAt = hit.Value.RevokedAt ?? clock.GetUtcNow(), RevokedReason = hit.Value.RevokedReason ?? reason };
            _rows[hit.Key] = revoked;
            return Task.FromResult<Membership?>(revoked);
        }
    }
}
