using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BuilderPro.Identity.Api.Tests;

/// <summary>Store picker (1f): X-BuilderPro-Tenant is a hint, only ever choosing among your OWN memberships.</summary>
public sealed class StoreSwitcherTests(IdentityFactory f) : IClassFixture<IdentityFactory>, IAsyncLifetime
{
    private readonly IdentityFactory _f = f;

    // dana: customer of BOTH stores (no tenant claim in her token).
    public async Task InitializeAsync()
    {
        await _f.Store.UpsertAsync("t-acme", IdentityFactory.KeyFor("dana2"), "c-dana-a", ["quotes.read"], null, default);
        await _f.Store.UpsertAsync("t-globex", IdentityFactory.KeyFor("dana2"), "c-dana-g", ["quotes.read"], null, default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Dana(string? pick)
    {
        var c = _f.As("dana2");
        if (pick is not null) c.DefaultRequestHeaders.Add("X-BuilderPro-Tenant", pick);
        return c;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Theory]
    [InlineData("t-acme", "c-dana-a")]
    [InlineData("t-globex", "c-dana-g")]
    public async Task Picking_a_store_resolves_that_membership(string pick, string customer)
    {
        var me = await Dana(pick).GetFromJsonAsync<IdentityMeResponse>("/api/identity/me");

        Assert.Equal(pick, me!.TenantId);
        Assert.Equal(customer, me.CustomerId);
    }

    [Fact]
    public async Task Without_a_pick_two_stores_are_still_ambiguous()
    {
        var r = await Dana(null).GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Picking_a_store_you_dont_belong_to_is_refused()
    {
        var r = await Dana("t-initech").GetAsync("/api/identity/me");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("tenant_not_permitted", await CodeOf(r));
    }

    [Fact]
    public async Task A_malformed_pick_is_ignored_not_trusted()
    {
        var r = await Dana("t-acme;drop").GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);   // treated as no pick
    }

    [Fact]
    public async Task The_header_cannot_override_a_revoked_membership()
    {
        await _f.Store.UpsertAsync("t-acme", IdentityFactory.KeyFor("rex"), "c-rex", [], null, default);
        var m = (await _f.Store.FindByUserAsync(IdentityFactory.KeyFor("rex"), default)).Single();
        await _f.Store.RevokeAsync(m.Id, "left", default);

        var c = _f.As("rex");
        c.DefaultRequestHeaders.Add("X-BuilderPro-Tenant", "t-acme");
        var r = await c.GetAsync("/api/identity/me");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Mine_lists_active_memberships_with_store_names()
    {
        var mine = await Dana(null).GetFromJsonAsync<MyMembershipDto[]>("/api/identity/memberships/mine");

        Assert.Equal(["t-acme", "t-globex"], mine!.Select(m => m.TenantId));
        Assert.Equal(["Acme Home Improvement", "Globex Building Supply"], mine!.Select(m => m.StoreName));
    }

    [Fact]
    public async Task Mine_needs_no_admin_role_and_shows_only_your_own()
    {
        var mine = await _f.As("nobody-" + Guid.NewGuid().ToString("N")[..6]).GetFromJsonAsync<MyMembershipDto[]>("/api/identity/memberships/mine");
        Assert.Empty(mine!);
    }
}
