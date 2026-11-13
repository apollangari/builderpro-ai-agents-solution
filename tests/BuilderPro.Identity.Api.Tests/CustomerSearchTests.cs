using System.Net;
using System.Net.Http.Json;

namespace BuilderPro.Identity.Api.Tests;

/// <summary>1i: a salesperson finds their own store's customers, and only theirs.</summary>
public sealed class CustomerSearchTests(IdentityFactory f) : IClassFixture<IdentityFactory>, IAsyncLifetime
{
    private readonly IdentityFactory _f = f;

    public async Task InitializeAsync()
    {
        _f.Directory.Users["carla"] = (new UserProfile("Carla", "Globex", "carla@example.test", "+1 555 0102", "3 Main St", null, "Plano", "TX", "75024", "US", "text", "business"), "Passw0rd!");
        _f.Directory.Users["sam"] = (new UserProfile("Sam", "Sales", "sam@example.test", "+1 555 0107", "7 Main St", null, "Prosper", "TX", "75078", "US", "none", "business"), "Passw0rd!");
        await _f.Store.UpsertAsync("t-acme", IdentityFactory.KeyFor("alice"), "c-alice", ["quotes.read", "quotes.write"], null, default);
        await _f.Store.UpsertAsync("t-globex", IdentityFactory.KeyFor("carla"), "c-carla", ["quotes.read"], null, default);
        await _f.Store.UpsertAsync("t-acme", IdentityFactory.KeyFor("sam"), null, ["quotes.read", "quotes.write", "sales"], null, default);
        await _f.Store.UpsertAsync("t-acme", IdentityFactory.KeyFor("bob"), null, ["quotes.read"], null, default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_salesperson_finds_customers_of_their_own_store_only()
    {
        var alice = await _f.As("sam").GetFromJsonAsync<CustomerDto[]>("/api/identity/customers?q=alice");
        var one = Assert.Single(alice!);
        Assert.Equal(("c-alice", "Alice Acme"), (one.CustomerId, one.Name));

        var carla = await _f.As("sam").GetFromJsonAsync<CustomerDto[]>("/api/identity/customers?q=carla");
        Assert.Empty(carla!);   // Globex's customer, not Acme's

        var staff = await _f.As("sam").GetFromJsonAsync<CustomerDto[]>("/api/identity/customers?q=sam");
        Assert.Empty(staff!);   // staff aren't customers
    }

    [Fact]
    public async Task Only_a_salesperson_may_search_and_the_query_is_checked()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.As("bob").GetAsync("/api/identity/customers?q=alice")).StatusCode);   // staff without "sales"
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.As("alice").GetAsync("/api/identity/customers?q=alice")).StatusCode); // a customer
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.As("sam").GetAsync("/api/identity/customers?q=x")).StatusCode);
        _f.Directory.SearchDown = true;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _f.As("sam").GetAsync("/api/identity/customers?q=alice")).StatusCode);
        }
        finally { _f.Directory.SearchDown = false; }
    }

    [Fact]
    public async Task The_customer_check_answers_for_the_salespersons_store()
    {
        Assert.Equal(HttpStatusCode.OK, (await _f.As("sam").GetAsync("/api/identity/customers/c-alice")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("sam").GetAsync("/api/identity/customers/c-carla")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.As("alice").GetAsync("/api/identity/customers/c-alice")).StatusCode);
    }
}
