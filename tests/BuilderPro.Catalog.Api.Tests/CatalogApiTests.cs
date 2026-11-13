using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BuilderPro.Catalog.Api.Domain;
using BuilderPro.Catalog.Api.Infrastructure;
using BuilderPro.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuilderPro.Catalog.Api.Tests;

/// <summary>X-Test-Sub = a signed-in caller. No header = anonymous.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("sub", sub.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Stands in for the Identity service: subject -> what Identity would have answered.</summary>
public sealed class FakeTenantLookup : ITenantLookup
{
    public Dictionary<string, TenantLookup> Answers { get; } = new(StringComparer.Ordinal)
    {
        ["alice"] = TenantLookup.Ok("t-acme"),
        ["carol"] = TenantLookup.Ok("t-globex"),
        ["erin"] = TenantLookup.Fail(StatusCodes.Status403Forbidden, "no_membership"),
        ["dana"] = TenantLookup.Fail(StatusCodes.Status409Conflict, "multiple_active_memberships"),
        ["outage"] = TenantLookup.Fail(StatusCodes.Status503ServiceUnavailable, "tenant_lookup_unavailable"),
    };

    public Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct) =>
        Task.FromResult(Answers[http.User.FindFirst("sub")!.Value]);
}

public sealed class CatalogFactory : WebApplicationFactory<Program>
{
    public InMemoryProductStore Store { get; } = new();
    public InMemoryPriceZoneStore Zones { get; } = new();
    public InMemoryOptionStore Options { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Authority", "http://idp.test/realms/builderpro");
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.UseSetting("ConnectionStrings:catalogdb", "Host=unused.test;Database=catalogdb");
        builder.UseSetting("Catalog:SeedDevProducts", "false");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IProductStore>();
            s.AddSingleton<IProductStore>(Store);
            s.RemoveAll<IPriceZoneStore>();
            s.AddSingleton<IPriceZoneStore>(Zones);
            s.RemoveAll<IOptionStore>();
            s.AddSingleton<IOptionStore>(Options);
            s.RemoveAll<ITenantLookup>();
            s.AddSingleton<ITenantLookup, FakeTenantLookup>();
            s.AddAuthentication(a => { a.DefaultAuthenticateScheme = TestAuthHandler.SchemeName; a.DefaultChallengeScheme = TestAuthHandler.SchemeName; })
             .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient As(string sub)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        return c;
    }
}

public sealed class CatalogApiTests(CatalogFactory f) : IClassFixture<CatalogFactory>, IAsyncLifetime
{
    private readonly CatalogFactory _f = f;

    // Same SKU in both tenants on purpose: the key is (tenant, sku), never sku alone.
    public async Task InitializeAsync()
    {
        await _f.Store.UpsertAsync(new Product("t-acme", "PAINT-1G", "Acme paint", null, "each", 34.98m, "USD", true), default);
        await _f.Store.UpsertAsync(new Product("t-acme", "TILE-12", "Acme tile", null, "m2", 21.50m, "USD", true), default);
        await _f.Store.UpsertAsync(new Product("t-acme", "OLD-1", "Discontinued", null, "each", 1m, "USD", Active: false), default);
        await _f.Store.UpsertAsync(new Product("t-globex", "PAINT-1G", "Globex paint", null, "each", 42.00m, "USD", true), default);
        await _f.Store.UpsertAsync(new Product("t-globex", "DECK-8", "Globex deck board", null, "each", 18.75m, "USD", true), default);

        // 2n-a zones: Acme sells both products downtown (dearer) and only paint in the suburbs.
        await _f.Zones.UpsertZoneAsync(new PriceZone("t-acme", "downtown", "Downtown", ["60601", "60602"]), default);
        await _f.Zones.UpsertZoneAsync(new PriceZone("t-acme", "suburbs", "Suburbs", ["62701"]), default);
        await _f.Zones.UpsertPriceAsync(new ZonePrice("t-acme", "PAINT-1G", "downtown", 39.50m), default);
        await _f.Zones.UpsertPriceAsync(new ZonePrice("t-acme", "TILE-12", "downtown", 24.00m), default);
        await _f.Zones.UpsertPriceAsync(new ZonePrice("t-acme", "PAINT-1G", "suburbs", 33.00m), default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<Dictionary<string, object?>>())?["code"]?.ToString();

    [Fact]
    public async Task A_member_sees_only_their_own_tenants_active_products()
    {
        var products = await _f.As("alice").GetFromJsonAsync<ProductDto[]>("/api/catalog/products");

        Assert.Equal(["PAINT-1G", "TILE-12"], products!.Select(p => p.Sku));
        Assert.Equal("Acme paint", products![0].Name);
        Assert.Equal(34.98m, products![0].ListPrice);
    }

    [Fact]
    public async Task Another_tenant_sees_its_own_version_of_the_same_sku()
    {
        var products = await _f.As("carol").GetFromJsonAsync<ProductDto[]>("/api/catalog/products");

        Assert.Equal(["DECK-8", "PAINT-1G"], products!.Select(p => p.Sku));
        Assert.Equal("Globex paint", products!.Single(p => p.Sku == "PAINT-1G").Name);
    }

    [Fact]
    public async Task Another_tenants_sku_is_a_plain_404()
    {
        var r = await _f.As("alice").GetAsync("/api/catalog/products/DECK-8");   // exists, but in t-globex
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task One_product_by_sku()
    {
        var p = await _f.As("alice").GetFromJsonAsync<ProductDto>("/api/catalog/products/TILE-12");
        Assert.Equal("m2", p!.Unit);
    }

    [Fact]
    public async Task Inactive_products_are_hidden()
    {
        var r = await _f.As("alice").GetAsync("/api/catalog/products/OLD-1");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Malformed_sku_is_400()
    {
        var r = await _f.As("alice").GetAsync("/api/catalog/products/bad%20sku");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_sku", await CodeOf(r));
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        var r = await _f.CreateClient().GetAsync("/api/catalog/products");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Theory]
    [InlineData("erin", HttpStatusCode.Forbidden, "no_membership")]
    [InlineData("dana", HttpStatusCode.Conflict, "multiple_active_memberships")]
    [InlineData("outage", HttpStatusCode.ServiceUnavailable, "tenant_lookup_unavailable")]
    public async Task Tenant_refusals_are_passed_through_and_no_products_leak(string sub, HttpStatusCode status, string code)
    {
        var r = await _f.As(sub).GetAsync("/api/catalog/products");

        Assert.Equal(status, r.StatusCode);
        Assert.Equal(code, await CodeOf(r));
    }

    // ---- 2n-a: ZIP service areas and zone prices ----

    [Theory]
    [InlineData("62701", true)]
    [InlineData("60601-1234", true)]   // ZIP+4 counts as the 5-digit ZIP
    [InlineData("90210", false)]
    public async Task Service_area_answers_yes_or_no_only(string zip, bool served)
    {
        var r = await _f.As("alice").GetAsync($"/api/catalog/service-area?zip={zip}");
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains($"\"served\":{served.ToString().ToLowerInvariant()}", body);
        Assert.DoesNotContain("downtown", body);   // zone ids and ZIP lists never leave the server
        Assert.DoesNotContain("suburbs", body);
    }

    [Fact]
    public async Task Another_store_does_not_serve_the_same_zip()
    {
        var area = await _f.As("carol").GetFromJsonAsync<ServiceAreaDto>("/api/catalog/service-area?zip=62701");
        Assert.False(area!.Served);   // Acme's zones are Acme's
    }

    [Fact]
    public async Task Products_by_zip_have_that_zones_price()
    {
        var downtown = await _f.As("alice").GetFromJsonAsync<ProductDto[]>("/api/catalog/products?zip=60601");
        var suburbs = await _f.As("alice").GetFromJsonAsync<ProductDto[]>("/api/catalog/products?zip=62701");

        Assert.Equal([("PAINT-1G", 39.50m), ("TILE-12", 24.00m)], downtown!.Select(p => (p.Sku, p.ListPrice)));
        Assert.Equal([("PAINT-1G", 33.00m)], suburbs!.Select(p => (p.Sku, p.ListPrice)));   // tile isn't sold there
    }

    [Fact]
    public async Task A_product_not_sold_in_the_zip_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("alice").GetAsync("/api/catalog/products/TILE-12?zip=62701")).StatusCode);
        var paint = await _f.As("alice").GetFromJsonAsync<ProductDto>("/api/catalog/products/PAINT-1G?zip=62701");
        Assert.Equal(33.00m, paint!.ListPrice);
    }

    [Theory]
    [InlineData("/api/catalog/products?zip=90210", HttpStatusCode.UnprocessableEntity, "zip_not_served")]
    [InlineData("/api/catalog/products?zip=abc", HttpStatusCode.BadRequest, "invalid_zip")]
    [InlineData("/api/catalog/service-area?zip=12", HttpStatusCode.BadRequest, "invalid_zip")]
    public async Task Bad_or_unserved_zips_are_named(string url, HttpStatusCode status, string code)
    {
        var r = await _f.As("alice").GetAsync(url);
        Assert.Equal(status, r.StatusCode);
        Assert.Equal(code, await CodeOf(r));
    }

    [Theory]
    [InlineData("75201", "75201")]
    [InlineData(" 75201-0001 ", "75201")]
    [InlineData("k1a0b1", "K1A 0B1")]
    [InlineData("K1A 0B1", "K1A 0B1")]
    [InlineData("7520", null)]
    [InlineData("ABCDE", null)]
    [InlineData("", null)]
    public void Zip_normalization(string raw, string? expected) => Assert.Equal(expected, Zip.Normalize(raw));
}

/// <summary>2n-b..d, with its own catalog so these products don't appear in the tests above.</summary>
public sealed class CatalogOptionsTests(CatalogFactory f) : IClassFixture<CatalogFactory>, IAsyncLifetime
{
    private readonly CatalogFactory _f = f;

    public async Task InitializeAsync()
    {
        await _f.Zones.UpsertZoneAsync(new PriceZone("t-acme", "downtown", "Downtown", ["60601"]), default);
        await _f.Zones.UpsertZoneAsync(new PriceZone("t-acme", "suburbs", "Suburbs", ["62701"]), default);

        // 2n-c/d: a quartz top with an edge step (one edge not sold downtown) and a plan only for quartz.
        await _f.Store.UpsertAsync(new Product("t-acme", "CT-QTZ", "Quartz top", null, "sqft", 89m, "USD", true, "countertops", null,
            new Dictionary<string, string> { ["material"] = "quartz", ["color"] = "White" }), default);
        await _f.Store.UpsertAsync(new Product("t-acme", "CT-LAM", "Laminate top", null, "sqft", 28m, "USD", true, "countertops", null,
            new Dictionary<string, string> { ["material"] = "laminate", ["color"] = "Grey" }), default);
        foreach (var (sku, price) in new[] { ("CT-QTZ", 99m), ("CT-LAM", 30m), ("EDGE-EASED", 0m), ("PLAN-10Y", 449m) })
        {
            await _f.Zones.UpsertPriceAsync(new ZonePrice("t-acme", sku, "downtown", price), default);
        }
        await _f.Options.UpsertGroupAsync(new OptionGroup("t-acme", "countertops", "edge", "options", "Edge", null, "single", "edgeFt", true, null, null, null, 0,
            [new("EDGE-EASED", "Eased", "ft", 0m), new("EDGE-OGEE", "Ogee", "ft", 18m)]), default);
        await _f.Options.UpsertGroupAsync(new OptionGroup("t-acme", "countertops", "plan", "protection", "Plan", null, "single", "fixed", true, "No plan",
            "material", ["quartz", "granite"], 1, [new("PLAN-10Y", "10-year plan", "each", 399m)]), default);
        await _f.Options.UpsertRuleAsync(new CategoryRule("t-acme", "countertops", 25m), default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- 2n-b..d: a product's measurement form, minimum and option steps at a ZIP's prices ----

    [Fact]
    public async Task Options_come_with_the_zips_prices_and_only_what_applies()
    {
        var o = await _f.As("alice").GetFromJsonAsync<ProductOptionsDto>("/api/catalog/products/CT-QTZ/options?zip=60601");

        Assert.Equal("countertop", o!.Measure);
        Assert.Equal(25m, o.MinimumQuantity);
        Assert.Equal(99m, o.Product.ListPrice);
        Assert.Equal(["edge", "plan"], o.Groups.Select(g => g.Id));
        Assert.Equal(["EDGE-EASED"], o.Groups[0].Options.Select(x => x.Sku));   // ogee has no price downtown
        Assert.Equal(449m, o.Groups[1].Options[0].Price);                         // the zone's price, not 399

        var laminate = await _f.As("alice").GetFromJsonAsync<ProductOptionsDto>("/api/catalog/products/CT-LAM/options?zip=60601");
        Assert.Equal(["edge"], laminate!.Groups.Select(g => g.Id));              // the plan covers stone only
    }

    [Fact]
    public async Task Options_need_a_zip_where_the_product_is_sold()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.As("alice").GetAsync("/api/catalog/products/CT-QTZ/options")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("alice").GetAsync("/api/catalog/products/CT-QTZ/options?zip=62701")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _f.As("carol").GetAsync("/api/catalog/products/CT-QTZ/options?zip=60601")).StatusCode);   // another store
    }

    [Fact]
    public void Option_groups_validate_their_rules()
    {
        var bad = new OptionGroup("t", "countertops", "g", "extras", "G", null, "quantity", "edgeFt", true, null, "material", null, 0, []);
        var errors = bad.Validate();
        Assert.Contains(errors, e => e.Contains("step"));
        Assert.Contains(errors, e => e.Contains("basis 'choice'"));
        Assert.Contains(errors, e => e.Contains("at least one option"));
        Assert.Contains(errors, e => e.Contains("go together"));
    }
}
