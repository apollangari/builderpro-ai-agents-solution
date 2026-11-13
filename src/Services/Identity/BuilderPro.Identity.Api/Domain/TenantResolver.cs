using BuilderPro.Security;

namespace BuilderPro.Identity.Api.Domain;

public enum TenantResolution
{
    Resolved,
    NoMembership,      // 403: authenticated, but belongs to no tenant
    Ambiguous,         // 409: several active tenants and the token doesn't say which
    HintMismatch,      // 403: token claims a tenant the user has no active membership in
}

public sealed record TenantResult(TenantResolution Outcome, Membership? Membership)
{
    public string Code => Outcome switch
    {
        TenantResolution.Resolved => "resolved",
        TenantResolution.NoMembership => "no_membership",
        TenantResolution.Ambiguous => "multiple_active_memberships",
        TenantResolution.HintMismatch => "tenant_not_permitted",
        _ => "unknown",
    };
}

/// <summary>
/// THE rule for "which tenant is this request for?". Pure: no I/O, no clock, fully unit-tested.
///
///   1. Only ACTIVE memberships count (not revoked, not expired).
///   2. If the token carries a tenant hint, it must match one of them. A token can't
///      grant tenant access on its own; the membership store is the source of truth.
///   3. No hint: exactly one active membership resolves; zero or several fail CLOSED.
/// </summary>
public static class TenantResolver
{
    public static TenantResult Resolve(UserContext user, IEnumerable<Membership> memberships, DateTimeOffset now)
    {
        var active = memberships.Where(m => m.UserKey == user.UserKey && m.IsActive(now)).ToArray();

        if (user.TenantHint is { } hint)
        {
            var match = active.SingleOrDefault(m => m.TenantId == hint);
            return match is null
                ? new TenantResult(TenantResolution.HintMismatch, null)
                : new TenantResult(TenantResolution.Resolved, match);
        }

        return active.Length switch
        {
            0 => new TenantResult(TenantResolution.NoMembership, null),
            1 => new TenantResult(TenantResolution.Resolved, active[0]),
            _ => new TenantResult(TenantResolution.Ambiguous, null),
        };
    }
}
