using BuilderPro.Identity.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Identity.Api.Tests;

/// <summary>Every branch of THE tenant rule. Pure function: no host, no database, no clock.</summary>
public sealed class TenantResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static UserContext User(string? hint = null) => new("user-1", "https://idp.test", "alice", null, [], hint);

    private static Membership M(string tenant, string user = "user-1", DateTimeOffset? expires = null, DateTimeOffset? revoked = null) =>
        new(Guid.NewGuid(), tenant, user, null, [], expires, revoked, revoked is null ? null : "test", Now.AddDays(-30));

    [Fact]
    public void One_active_membership_resolves()
    {
        var r = TenantResolver.Resolve(User(), [M("t-acme")], Now);
        Assert.Equal(TenantResolution.Resolved, r.Outcome);
        Assert.Equal("t-acme", r.Membership!.TenantId);
    }

    [Fact]
    public void No_membership_fails_closed()
    {
        var r = TenantResolver.Resolve(User(), [], Now);
        Assert.Equal(TenantResolution.NoMembership, r.Outcome);
        Assert.Equal("no_membership", r.Code);
    }

    [Fact]
    public void Several_memberships_without_a_hint_fail_closed_never_pick_one()
    {
        var r = TenantResolver.Resolve(User(), [M("t-acme"), M("t-globex")], Now);
        Assert.Equal(TenantResolution.Ambiguous, r.Outcome);
        Assert.Null(r.Membership);
    }

    [Fact]
    public void A_hint_selects_among_several_memberships()
    {
        var r = TenantResolver.Resolve(User(hint: "t-globex"), [M("t-acme"), M("t-globex")], Now);
        Assert.Equal("t-globex", r.Membership!.TenantId);
    }

    [Fact]
    public void A_hint_alone_never_grants_access()
    {
        // The token says t-globex, but the platform has no such membership: refused.
        var r = TenantResolver.Resolve(User(hint: "t-globex"), [M("t-acme")], Now);
        Assert.Equal(TenantResolution.HintMismatch, r.Outcome);
        Assert.Equal("tenant_not_permitted", r.Code);
    }

    [Fact]
    public void Revoked_memberships_do_not_count()
    {
        var r = TenantResolver.Resolve(User(), [M("t-acme", revoked: Now.AddMinutes(-1))], Now);
        Assert.Equal(TenantResolution.NoMembership, r.Outcome);
    }

    [Fact]
    public void Expired_memberships_do_not_count_and_expiry_is_exclusive()
    {
        Assert.Equal(TenantResolution.NoMembership, TenantResolver.Resolve(User(), [M("t-acme", expires: Now)], Now).Outcome);
        Assert.Equal(TenantResolution.Resolved, TenantResolver.Resolve(User(), [M("t-acme", expires: Now.AddSeconds(1))], Now).Outcome);
    }

    [Fact]
    public void Other_users_memberships_are_ignored()
    {
        var r = TenantResolver.Resolve(User(), [M("t-acme", user: "someone-else")], Now);
        Assert.Equal(TenantResolution.NoMembership, r.Outcome);
    }
}
