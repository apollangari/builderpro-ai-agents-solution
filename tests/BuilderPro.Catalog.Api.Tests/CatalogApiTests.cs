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
}
