using BuilderPro.Catalog.Api.Domain;
using BuilderPro.Catalog.Api.Infrastructure;
using BuilderPro.Identity.Api.Infrastructure;
using BuilderPro.Quotes.Api;
using BuilderPro.Quotes.Api.Infrastructure;
using BuilderPro.Quotes.Domain;

namespace BuilderPro.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MembershipStoreTests(PostgresFixture pg)
{
    private async Task<PostgresMembershipStore> NewStoreAsync()
    {
        var store = new PostgresMembershipStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);   // schema creation is idempotent
        return store;
    }

    [Fact]
    public async Task Upsert_then_find_round_trips_every_field()
    {
        var store = await NewStoreAsync();
        var expires = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var created = await store.UpsertAsync("t-acme", "user-1", "c-1", ["quotes.read", "quotes.write"], expires, default);
        var found = Assert.Single(await store.FindByUserAsync("user-1", default));

        Assert.Equal(created.Id, found.Id);
        Assert.Equal("t-acme", found.TenantId);
        Assert.Equal("c-1", found.CustomerId);
        Assert.Equal(["quotes.read", "quotes.write"], found.Scopes);
        Assert.Equal(expires, found.ExpiresAt);
        Assert.Null(found.RevokedAt);
    }

    [Fact]
    public async Task Upsert_is_idempotent_and_reactivates_a_revoked_membership()
    {
        var store = await NewStoreAsync();
        var first = await store.UpsertAsync("t-acme", "user-1", null, [], null, default);
        await store.RevokeAsync(first.Id, "left", default);

        var again = await store.UpsertAsync("t-acme", "user-1", null, ["quotes.read"], null, default);

        Assert.Equal(first.Id, again.Id);
        Assert.Null(again.RevokedAt);
        Assert.Single(await store.ListByTenantAsync("t-acme", default));
    }

    [Fact]
    public async Task Revoke_keeps_the_first_time_and_reason()
    {
        var store = await NewStoreAsync();
        var m = await store.UpsertAsync("t-acme", "user-1", null, [], null, default);

        var once = await store.RevokeAsync(m.Id, "first", default);
        var twice = await store.RevokeAsync(m.Id, "second", default);

        Assert.Equal("first", twice!.RevokedReason);
        Assert.Equal(once!.RevokedAt, twice.RevokedAt);
        Assert.Null(await store.RevokeAsync(Guid.NewGuid(), "missing", default));
    }

    [Fact]
    public async Task List_is_per_tenant_and_ordered_by_user()
    {
        var store = await NewStoreAsync();
        await store.UpsertAsync("t-acme", "zed", null, [], null, default);
        await store.UpsertAsync("t-acme", "amy", null, [], null, default);
        await store.UpsertAsync("t-globex", "bob", null, [], null, default);

        Assert.Equal(["amy", "zed"], (await store.ListByTenantAsync("t-acme", default)).Select(m => m.UserKey));
        Assert.True(await store.PingAsync(default));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ProductStoreTests(PostgresFixture pg)
{
    private async Task<PostgresProductStore> NewStoreAsync()
    {
        var store = new PostgresProductStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);
        return store;
    }

    [Fact]
    public async Task Same_sku_in_two_tenants_never_leaks()
    {
        var store = await NewStoreAsync();
        await store.UpsertAsync(new Product("t-acme", "PAINT", "Acme paint", "desc", "each", 34.98m, "USD", true), default);
        await store.UpsertAsync(new Product("t-globex", "PAINT", "Globex paint", null, "each", 42.00m, "USD", true), default);

        var acme = Assert.Single(await store.ListActiveAsync("t-acme", default));
        Assert.Equal("Acme paint", acme.Name);
        Assert.Equal(34.98m, acme.ListPrice);
        Assert.Equal("desc", acme.Description);
        Assert.Equal(42.00m, (await store.FindActiveAsync("t-globex", "PAINT", default))!.ListPrice);
        Assert.Null(await store.FindActiveAsync("t-acme", "NOPE", default));
    }

    [Fact]
    public async Task Category_image_and_attributes_round_trip_through_jsonb()
    {
        var store = await NewStoreAsync();
        var attrs = new Dictionary<string, string> { ["material"] = "quartz", ["color"] = "Calacatta white", ["thicknessCm"] = "3" };
        await store.UpsertAsync(new Product("t-acme", "CT-1", "Quartz", null, "sqft", 89m, "USD", true, "countertops", "/products/ct-1.svg", attrs), default);

        var p = (await store.FindActiveAsync("t-acme", "CT-1", default))!;

        Assert.Equal("countertops", p.Category);
        Assert.Equal("/products/ct-1.svg", p.ImageUrl);
        Assert.Equal("Calacatta white", p.AttributeValues["color"]);
        Assert.Equal(3, p.AttributeValues.Count);
    }

    [Fact]
    public async Task Inactive_products_are_hidden_and_upsert_updates_in_place()
    {
        var store = await NewStoreAsync();
        await store.UpsertAsync(new Product("t-acme", "B", "B", null, "each", 1m, "USD", true), default);
        await store.UpsertAsync(new Product("t-acme", "A", "A", null, "m2", 2m, "USD", true), default);
        await store.UpsertAsync(new Product("t-acme", "B", "B", null, "each", 1m, "USD", Active: false), default);

        Assert.Equal(["A"], (await store.ListActiveAsync("t-acme", default)).Select(p => p.Sku));
        Assert.Null(await store.FindActiveAsync("t-acme", "B", default));
        Assert.True(await store.PingAsync(default));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class QuoteStoreTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private async Task<PostgresQuoteStore> NewStoreAsync()
    {
        var store = new PostgresQuoteStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);
        return store;
    }

    private static Quote NewQuote(string tenant, string? customer, DateTimeOffset at)
    {
        var priced = QuotePricer.Price(new QuoteInput(
            [new QuoteLineInput("PAINT", "Paint", "each", 3, new Money(34.98m, "USD")),
             new QuoteLineInput("TILE", "Tile", "m2", 12.345m, new Money(21.50m, "USD"))],
            0.0825m)).Quote!;
        return new Quote(Guid.NewGuid(), tenant, customer, "user-1", "draft", priced, at);
    }

    [Fact]
    public async Task Create_then_find_round_trips_the_snapshot()
    {
        var store = await NewStoreAsync();
        var q = NewQuote("t-acme", "c-alice", T0);
        await store.CreateAsync(q, default);

        var found = (await store.FindAsync("t-acme", "c-alice", q.Id, default))!;

        Assert.Equal(q.Priced.Total, found.Priced.Total);
        Assert.Equal(q.Priced.Tax, found.Priced.Tax);
        Assert.Equal(0.0825m, found.Priced.TaxRate);
        Assert.Equal(["PAINT", "TILE"], found.Priced.Lines.Select(l => l.Sku));
        Assert.Equal(12.345m, found.Priced.Lines[1].Quantity);
        Assert.Equal(q.Priced.Lines[1].LineTotal, found.Priced.Lines[1].LineTotal);
        Assert.Equal(T0, found.CreatedAt);
        Assert.Equal("c-alice", found.CustomerId);
    }

    [Fact]
    public async Task Customers_see_their_own_staff_see_the_tenant_other_tenants_see_nothing()
    {
        var store = await NewStoreAsync();
        var alice = NewQuote("t-acme", "c-alice", T0);
        var abe = NewQuote("t-acme", "c-abe", T0.AddMinutes(1));
        var globex = NewQuote("t-globex", "c-carol", T0.AddMinutes(2));
        foreach (var q in new[] { alice, abe, globex }) await store.CreateAsync(q, default);

        Assert.Equal([alice.Id], (await store.ListAsync("t-acme", "c-alice", 50, default)).Select(q => q.Id));
        Assert.Equal([abe.Id, alice.Id], (await store.ListAsync("t-acme", null, 50, default)).Select(q => q.Id));   // newest first
        Assert.Null(await store.FindAsync("t-acme", "c-alice", abe.Id, default));
        Assert.Null(await store.FindAsync("t-acme", null, globex.Id, default));
        Assert.Single(await store.ListAsync("t-acme", null, 1, default));
        Assert.True(await store.PingAsync(default));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ConsentStoreTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Appends_and_reads_back_newest_first()
    {
        var db = await pg.NewDatabaseAsync();
        var store = new PostgresConsentStore(db);
        await store.InitializeAsync(default);
        await new PostgresMembershipStore(db).InitializeAsync(default);   // both share the identity schema
        await store.InitializeAsync(default);

        var first = await store.AppendAsync("u1", "both", "business", "v1", "signup", T0, default);
        await store.AppendAsync("u1", "none", "business", "v1", "sync", T0.AddDays(1), default);
        await store.AppendAsync("u2", "text", "evenings", "v1", "signup", T0, default);

        var history = await store.HistoryAsync("u1", 10, default);
        Assert.Equal(["none", "both"], history.Select(h => h.Consent));
        Assert.Equal(T0, history[1].At);
        Assert.Equal(first.Id, history[1].Id);
        Assert.Equal("none", (await store.LatestAsync("u1", default))!.Consent);
        Assert.Null(await store.LatestAsync("nobody", default));
    }

    [Fact]
    public async Task The_database_rejects_unknown_values()
    {
        var store = new PostgresConsentStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.AppendAsync("u1", "fax", "business", "v1", "sync", T0, default));
    }
}
