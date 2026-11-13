namespace BuilderPro.Identity.Api.Domain;

/// <summary>
/// A tenant binding: this user belongs to this tenant (as this customer), with these scopes,
/// until it expires or is revoked. Adapted from builderpro-ai-platform/platform/identity.
///
/// Revocation is a TIMESTAMP, never a delete: the audit trail must outlive the access.
/// </summary>
public sealed record Membership(
    Guid Id,
    string TenantId,
    string UserKey,
    string? CustomerId,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    DateTimeOffset CreatedAt)
{
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

public interface IMembershipStore
{
    Task InitializeAsync(CancellationToken ct);
    Task<IReadOnlyList<Membership>> FindByUserAsync(string userKey, CancellationToken ct);
    Task<IReadOnlyList<Membership>> ListByTenantAsync(string tenantId, CancellationToken ct);
    /// <summary>Creates, or re-activates an existing (tenant, user) binding. Idempotent.</summary>
    Task<Membership> UpsertAsync(string tenantId, string userKey, string? customerId, IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, CancellationToken ct);
    Task<Membership?> RevokeAsync(Guid id, string reason, CancellationToken ct);
    Task<bool> PingAsync(CancellationToken ct);
}
