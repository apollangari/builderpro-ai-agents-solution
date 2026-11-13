using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BuilderPro.Identity.Api.Tests;

/// <summary>Self-service onboarding through a store's sign-up link (decision 1e).</summary>
public sealed class OnboardingTests(IdentityFactory f) : IClassFixture<IdentityFactory>
{
    private readonly IdentityFactory _f = f;

    private static string NewUser() => "new-" + Guid.NewGuid().ToString("N")[..8];

    private Task<HttpResponseMessage> Join(string sub, string? store) =>
        _f.As(sub).PostAsJsonAsync("/api/identity/onboarding", new { store });

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task A_new_user_joins_the_store_as_a_customer()
    {
        var sub = NewUser();

        var r = await Join(sub, "acme");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = (await r.Content.ReadFromJsonAsync<JoinStoreResponse>())!;
        Assert.Equal("joined", body.Status);
        Assert.Equal("t-acme", body.TenantId);
        Assert.Equal("Acme Home Improvement", body.StoreName);
        Assert.StartsWith("c-", body.CustomerId);

        // ...and the platform now resolves their tenant, with customer scopes only.
        var me = await _f.As(sub).GetFromJsonAsync<IdentityMeResponse>("/api/identity/me");
        Assert.Equal("t-acme", me!.TenantId);
        Assert.Equal(body.CustomerId, me.CustomerId);
        Assert.Equal(["quotes.read", "quotes.write"], me.Scopes);
    }

    [Fact]
    public async Task Joining_twice_is_harmless_and_keeps_the_same_customer()
    {
        var sub = NewUser();
        var first = (await (await Join(sub, "acme")).Content.ReadFromJsonAsync<JoinStoreResponse>())!;

        var again = await Join(sub, "acme");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = (await again.Content.ReadFromJsonAsync<JoinStoreResponse>())!;
        Assert.Equal("already_member", body.Status);
        Assert.Equal(first.CustomerId, body.CustomerId);
    }

    [Fact]
    public async Task An_existing_member_is_never_changed_by_a_link()
    {
        // bob is acme STAFF with catalog.admin: a sign-up link must not downgrade or alter him.
        var bobKey = IdentityFactory.KeyFor("bob-staff");
        await _f.Store.UpsertAsync("t-acme", bobKey, null, ["quotes.read", "catalog.admin"], null, default);

        var r = await Join("bob-staff", "acme");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var bob = (await _f.Store.FindByUserAsync(bobKey, default)).Single();
        Assert.Null(bob.CustomerId);
        Assert.Equal(["quotes.read", "catalog.admin"], bob.Scopes);
    }

    [Fact]
    public async Task A_revoked_customer_cannot_rejoin_with_the_link()
    {
        var sub = NewUser();
        await Join(sub, "acme");
        var m = (await _f.Store.FindByUserAsync(IdentityFactory.KeyFor(sub), default)).Single();
        await _f.Store.RevokeAsync(m.Id, "removed by retailer", default);

        var r = await Join(sub, "acme");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("membership_inactive", await CodeOf(r));
        Assert.NotNull((await _f.Store.FindByUserAsync(IdentityFactory.KeyFor(sub), default)).Single().RevokedAt);
    }

    [Fact]
    public async Task A_customer_of_one_store_can_also_join_another()
    {
        var sub = NewUser();
        await Join(sub, "acme");

        var r = await Join(sub, "globex");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(2, (await _f.Store.FindByUserAsync(IdentityFactory.KeyFor(sub), default)).Count);
    }

    [Theory]
    [InlineData("closed", HttpStatusCode.NotFound, "store_not_found")]     // sign-up disabled
    [InlineData("nope", HttpStatusCode.NotFound, "store_not_found")]       // unknown store
    [InlineData("bad store!", HttpStatusCode.BadRequest, "invalid_store")]
    [InlineData(null, HttpStatusCode.BadRequest, "invalid_store")]
    public async Task Bad_links_are_refused_and_create_nothing(string? store, HttpStatusCode status, string code)
    {
        var sub = NewUser();

        var r = await Join(sub, store);

        Assert.Equal(status, r.StatusCode);
        Assert.Equal(code, await CodeOf(r));
        Assert.Empty(await _f.Store.FindByUserAsync(IdentityFactory.KeyFor(sub), default));
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        var r = await _f.CreateClient().PostAsJsonAsync("/api/identity/onboarding", new { store = "acme" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public void Customer_ids_are_stable_per_tenant_and_user()
    {
        Assert.Equal(Onboarding.CustomerIdFor("t-acme", "u1"), Onboarding.CustomerIdFor("t-acme", "u1"));
        Assert.NotEqual(Onboarding.CustomerIdFor("t-acme", "u1"), Onboarding.CustomerIdFor("t-globex", "u1"));
        Assert.Equal(14, Onboarding.CustomerIdFor("t-acme", "u1").Length);
    }
}
