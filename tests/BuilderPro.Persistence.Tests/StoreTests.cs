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
public sealed class PriceZoneStoreTests(PostgresFixture pg)
{
    private async Task<PostgresPriceZoneStore> NewStoreAsync()
    {
        var store = new PostgresPriceZoneStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);   // idempotent, like every schema step
        return store;
    }

    [Fact]
    public async Task Zips_map_to_one_zone_per_tenant_and_prices_stay_in_their_tenant()
    {
        var store = await NewStoreAsync();
        await store.UpsertZoneAsync(new PriceZone("t-acme", "north", "North", ["60601", "60602"]), default);
        await store.UpsertZoneAsync(new PriceZone("t-globex", "north", "North", ["60601"]), default);
        await store.UpsertPriceAsync(new ZonePrice("t-acme", "PAINT", "north", 39.50m), default);
        await store.UpsertPriceAsync(new ZonePrice("t-globex", "PAINT", "north", 45.00m), default);

        Assert.Equal("north", await store.ZoneForZipAsync("t-acme", "60602", default));
        Assert.Null(await store.ZoneForZipAsync("t-globex", "60602", default));
        Assert.Equal(39.50m, (await store.PricesAsync("t-acme", "north", default))["PAINT"]);
        Assert.Equal(45.00m, (await store.PricesAsync("t-globex", "north", default))["PAINT"]);
    }

    [Fact]
    public async Task Re_upserting_a_zone_replaces_its_zips_and_a_zip_can_move_zones()
    {
        var store = await NewStoreAsync();
        await store.UpsertZoneAsync(new PriceZone("t-acme", "a", "A", ["10001", "10002"]), default);
        await store.UpsertZoneAsync(new PriceZone("t-acme", "b", "B", ["10003"]), default);

        await store.UpsertZoneAsync(new PriceZone("t-acme", "a", "A", ["10001"]), default);    // 10002 dropped
        await store.UpsertZoneAsync(new PriceZone("t-acme", "b", "B", ["10001", "10003"]), default); // 10001 moves to b

        Assert.Equal("b", await store.ZoneForZipAsync("t-acme", "10001", default));
        Assert.Null(await store.ZoneForZipAsync("t-acme", "10002", default));

        await store.UpsertPriceAsync(new ZonePrice("t-acme", "TILE", "a", 10m), default);
        await store.UpsertPriceAsync(new ZonePrice("t-acme", "TILE", "a", 12m), default);       // update in place
        Assert.Equal(12m, (await store.PricesAsync("t-acme", "a", default))["TILE"]);
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
        Assert.Null(found.Guided);   // a plain quote stays plain
    }

    [Fact]
    public async Task Guided_quotes_keep_zip_request_and_line_groups()
    {
        var store = await NewStoreAsync();
        var q = NewQuote("t-acme", "c-alice", T0) with
        {
            Guided = new GuidedInfo("62701", """{"zip":"62701","sku":"PAINT","choices":[]}""", ["product", "options"]),
        };
        await store.CreateAsync(q, default);

        var found = (await store.FindAsync("t-acme", "c-alice", q.Id, default))!;
        Assert.Equal("62701", found.Guided!.Zip);
        Assert.Equal(["product", "options"], found.Guided.LineGroups);
        Assert.Contains("\"sku\": \"PAINT\"", found.Guided.RequestJson);   // jsonb normalizes spacing
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
public sealed class ProjectAndIssuedQuoteStoreTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Projects_round_trip_and_stay_private()
    {
        var store = new PostgresProjectStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);
        var p = new Project(Guid.NewGuid(), "t-acme", "c-alice", "u-1", "Kitchen", "62701", null, "{}", ProjectStages.Discover, null, T0, T0);
        await store.CreateAsync(p, default);
        await store.UpdateAsync(p with { Sku = "CT-QTZ", AnswersJson = """{"choices":[]}""", Stage = ProjectStages.Configure, UpdatedAt = T0.AddHours(1) }, default);

        var found = (await store.FindAsync("t-acme", "c-alice", p.Id, default))!;
        Assert.Equal(("CT-QTZ", "configure", T0.AddHours(1)), (found.Sku, found.Stage, found.UpdatedAt));
        Assert.Contains("choices", found.AnswersJson);
        Assert.Null(await store.FindAsync("t-acme", "c-abe", p.Id, default));
        Assert.Null(await store.FindAsync("t-globex", null, p.Id, default));
        Assert.Single(await store.ListAsync("t-acme", null, 10, default));
    }

    [Fact]
    public async Task Issued_quotes_are_numbered_per_store_and_accept_only_while_valid()
    {
        var quotes = new PostgresQuoteStore(await pg.NewDatabaseAsync());
        await quotes.InitializeAsync(default);
        var priced = QuotePricer.Price(new QuoteInput([new QuoteLineInput("CT", "Quartz", "sqft", 30, new Money(89m, "USD"))], 0.0825m)).Quote!;
        Quote Issued(string tenant) => new(Guid.NewGuid(), tenant, "c-alice", "u-1", "issued", priced, T0,
            new GuidedInfo("62701", "{}", ["product"]), new QuoteIssue(0, T0.AddDays(14), Guid.NewGuid()));

        var a1 = await quotes.IssueAsync(Issued("t-acme"), default);
        var a2 = await quotes.IssueAsync(Issued("t-acme"), default);
        var g1 = await quotes.IssueAsync(Issued("t-globex"), default);
        Assert.Equal((1001, 1002, 1001), (a1.Issue!.Number, a2.Issue!.Number, g1.Issue!.Number));

        Assert.False(await quotes.AcceptAsync("t-acme", "c-abe", a1.Id, T0, default));              // not theirs
        Assert.False(await quotes.AcceptAsync("t-acme", "c-alice", a1.Id, T0.AddDays(15), default)); // expired
        Assert.True(await quotes.AcceptAsync("t-acme", "c-alice", a1.Id, T0.AddDays(1), default));
        Assert.False(await quotes.AcceptAsync("t-acme", "c-alice", a1.Id, T0.AddDays(1), default));  // only once

        var read = (await quotes.FindAsync("t-acme", "c-alice", a1.Id, default))!;
        Assert.Equal(("accepted", 1001, T0.AddDays(1)), (read.Status, read.Issue!.Number, read.Issue.AcceptedAt));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class DepositStoreTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_deposit_is_paid_once_and_stays_in_its_tenant()
    {
        var store = new PostgresDepositStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);
        var quoteId = Guid.NewGuid();
        var d = new Deposit(Guid.NewGuid(), "t-acme", Guid.NewGuid(), quoteId, "c-alice", 362.29m, "USD", 5m, DepositStatus.Pending, "simulated", T0, null);
        await store.CreateAsync(d, default);

        Assert.Null(await store.FindAsync("t-globex", null, d.Id, default));
        Assert.Null(await store.FindAsync("t-acme", "c-abe", d.Id, default));
        Assert.False(await store.MarkPaidAsync("t-globex", d.Id, T0, "u-1", default));
        Assert.True(await store.MarkPaidAsync("t-acme", d.Id, T0.AddMinutes(5), "u-1", default));
        Assert.False(await store.MarkPaidAsync("t-acme", d.Id, T0.AddMinutes(6), "u-1", default));   // only once

        var found = (await store.ForQuoteAsync("t-acme", quoteId, default))!;
        Assert.Equal(("paid", 362.29m, T0.AddMinutes(5)), (found.Status, found.Amount, found.PaidAt));
        Assert.Equal(d.Id, (await store.ForProjectAsync("t-acme", d.ProjectId, default))!.Id);
        Assert.Equal(d.Id, Assert.Single(await store.ListPaidAsync("t-acme", T0, T0.AddDays(1), default)).Id);
        Assert.Empty(await store.ListPaidAsync("t-globex", T0, T0.AddDays(1), default));
    }
}

/// <summary>6b: visits in Postgres: per-slot capacity, reschedule, measured once.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VisitStoreTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Slot = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);

    private static Visit New(Guid project, DateTimeOffset at, string tenant = "t-acme") =>
        new(Guid.NewGuid(), tenant, project, "c-alice", at, VisitStatus.Booked, "u-1", Slot.AddDays(-1));

    [Fact]
    public async Task A_slot_holds_its_capacity_a_reschedule_moves_the_booking_and_measured_is_once()
    {
        var store = new PostgresVisitStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        Assert.True(await store.BookAsync(New(p1, Slot), 1, default));
        Assert.False(await store.BookAsync(New(p2, Slot), 1, default));                 // full
        Assert.True(await store.BookAsync(New(Guid.NewGuid(), Slot, "t-globex"), 1, default));   // other tenant, own capacity

        var moved = New(p1, Slot.AddHours(4));
        Assert.True(await store.BookAsync(moved, 1, default));                          // reschedule frees the first time
        Assert.True(await store.BookAsync(New(p2, Slot), 1, default));
        Assert.Equal(moved.Id, (await store.ForProjectAsync("t-acme", p1, default))!.Id);

        var counts = await store.BookedAsync("t-acme", Slot, Slot.AddDays(1), default);
        Assert.Equal((1, 1), (counts[Slot], counts[Slot.AddHours(4)]));

        Assert.True(await store.MarkMeasuredAsync("t-acme", moved.Id, "u-2", Slot.AddHours(5), default));
        Assert.False(await store.MarkMeasuredAsync("t-acme", moved.Id, "u-2", Slot.AddHours(6), default));
        var done = (await store.ForProjectAsync("t-acme", p1, default))!;
        Assert.Equal(("measured", Slot.AddHours(5)), (done.Status, done.MeasuredAt));
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

/// <summary>6c: install steps and the notification outbox in Postgres.</summary>
[Collection(PostgresCollection.Name)]
public sealed class InstallStoreTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Steps_and_their_updates_are_saved_together_per_tenant()
    {
        var store = new PostgresInstallStore(await pg.NewDatabaseAsync());
        await store.InitializeAsync(default);
        await store.InitializeAsync(default);
        var project = Guid.NewGuid();
        var e1 = new InstallEvent(Guid.NewGuid(), "t-acme", project, "scheduled", T0, "u-1", T0.AddDays(10), "Morning");
        await store.AddAsync(e1, [new Notification(Guid.NewGuid(), "t-acme", project, "c-alice", "email", "scheduled", T0),
                                  new Notification(Guid.NewGuid(), "t-acme", project, "c-alice", "sms", "scheduled", T0)], default);
        await store.AddAsync(new InstallEvent(Guid.NewGuid(), "t-acme", project, "fabricating", T0.AddDays(1), "u-1", null, null), [], default);

        var events = await store.ForProjectAsync("t-acme", project, default);
        Assert.Equal(new[] { "scheduled", "fabricating" }, events.Select(e => e.Step));
        Assert.Equal((T0.AddDays(10), "Morning"), (events[0].ScheduledFor, events[0].Note));
        Assert.Empty(await store.ForProjectAsync("t-globex", project, default));
        Assert.Equal(new[] { "email", "sms" }, (await store.NotificationsAsync("t-acme", project, default)).Select(n => n.Channel));
    }
}
