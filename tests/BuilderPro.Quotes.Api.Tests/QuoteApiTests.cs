using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuilderPro.Quotes.Api;
using BuilderPro.Quotes.Api.Infrastructure;
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
using Microsoft.Extensions.Time.Testing;

namespace BuilderPro.Quotes.Api.Tests;

/// <summary>X-Test-Sub = a signed-in caller from a fixed issuer. No header = anonymous.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("iss", "http://idp.test/realms/builderpro"), new Claim("sub", sub.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Stands in for Identity: subject -> tenant, customer, scopes (mirrors the dev memberships).</summary>
public sealed class FakeTenantLookup : ITenantLookup
{
    private static readonly string[] ReadWrite = ["quotes.read", "quotes.write"];

    public Dictionary<string, TenantLookup> Answers { get; } = new(StringComparer.Ordinal)
    {
        ["alice"] = TenantLookup.Ok("t-acme", ReadWrite, "c-alice"),
        ["abe"] = TenantLookup.Ok("t-acme", ReadWrite, "c-abe"),          // another acme customer
        ["bob"] = TenantLookup.Ok("t-acme", ["quotes.read"]),               // acme staff: no customer, read-only
        ["carol"] = TenantLookup.Ok("t-globex", ReadWrite, "c-carol"),
        ["nora"] = TenantLookup.Ok("t-acme", [], "c-nora"),                 // member without quote scopes
        ["erin"] = TenantLookup.Fail(StatusCodes.Status403Forbidden, "no_membership"),
        ["sam"] = TenantLookup.Ok("t-acme", ["quotes.read", "quotes.write", "sales"]),   // 1i: acme salesperson
        ["sue"] = TenantLookup.Ok("t-acme", ["quotes.read", "quotes.write", "sales"]),   // another acme salesperson
        ["boss"] = TenantLookup.Ok("t-acme", ["quotes.read", "sales.report"]),          // acme sales manager
        ["gus"] = TenantLookup.Ok("t-globex", ["quotes.read", "quotes.write", "sales"]), // globex salesperson
        ["mia"] = TenantLookup.Ok("t-acme", ["quotes.read", "scheduling.write"]),         // 6b: acme measurer / scheduler
        ["gina"] = TenantLookup.Ok("t-acme", ["quotes.read", "quotes.write"], "c-gina", ["guided"]),   // 4b: a store package without phone scheduling
    };

    public Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct) =>
        Task.FromResult(Answers[http.User.FindFirst("sub")!.Value]);
}

/// <summary>Stands in for Identity's customer check (1i): acme has alice and abe, globex has carol.</summary>
public sealed class FakeCustomerDirectory(FakeTenantLookup tenants) : ICustomerDirectory
{
    public bool Down { get; set; }

    public async Task<bool?> IsCustomerAsync(HttpContext http, string customerId, CancellationToken ct)
    {
        if (Down) return null;
        var tenant = await tenants.ResolveAsync(http, ct);
        return tenant.TenantId switch
        {
            "t-acme" => customerId is "c-alice" or "c-abe",
            "t-globex" => customerId is "c-carol",
            _ => false,
        };
    }
}

/// <summary>Stands in for Identity's consented channels (6c): alice agreed to texts, abe to nothing; null = Identity down.</summary>
public sealed class FakeContactChannels : IContactChannels
{
    public bool Down { get; set; }

    public Task<IReadOnlyList<string>?> ForCustomerAsync(HttpContext http, string customerId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(Down ? null : customerId == "c-alice" ? ["email", "sms"] : ["email"]);
}

/// <summary>Stands in for Identity's profile (4b): alice allows texts; abe has no phone.</summary>
public sealed class FakeProfiles(FakeTenantLookup tenants) : IProfileReader
{
    public async Task<CustomerProfile?> MineAsync(HttpContext http, CancellationToken ct) =>
        (await tenants.ResolveAsync(http, ct)).CustomerId switch
        {
            "c-alice" => new CustomerProfile("+1 555 010 0001", "business", "text", "alice@acme.test"),
            "c-abe" => new CustomerProfile(null, "business", "none", null),
            _ => new CustomerProfile("+1 555 010 0009", "anytime", "none"),
        };
}

/// <summary>Records emails instead of sending them.</summary>
public sealed class FakeEmail : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        Sent.Add((to, subject, body));
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for the Catalog: per-tenant price lists, or "down".</summary>
public sealed class FakeCatalog(FakeTenantLookup tenants) : ICatalogPrices
{
    public bool Down { get; set; }

    public static readonly Dictionary<string, CatalogProduct[]> ByTenant = new()
    {
        ["t-acme"] = [new("PAINT", "Interior paint", "each", 34.98m, "USD"), new("LABOR", "Installation labour", "hour", 65.00m, "USD")],
        ["t-globex"] = [new("DECK", "Cedar deck board", "each", 18.75m, "USD"), new("PAINT", "Premium paint", "each", 42.00m, "USD")],
    };

    /// <summary>The last ZIP the API passed on. ZIP 62701: list prices. 60601: PAINT only, at 39.00. "nope": invalid. Others: not served.</summary>
    public string? LastZip { get; private set; }

    /// <summary>Acme sells CT-QTZ in ZIP 62701 with an edge step and a protection plan; 25 sq ft minimum.</summary>
    public async Task<CatalogOptionsRead> GetOptionsAsync(HttpContext http, string sku, string zip, CancellationToken ct)
    {
        if (Down) return new CatalogOptionsRead(null);
        var tenant = await tenants.ResolveAsync(http, ct);
        if (zip == "nope") return new CatalogOptionsRead(null, "invalid_zip");
        if (zip != "62701") return new CatalogOptionsRead(null, "zip_not_served");
        if (tenant.TenantId != "t-acme" || sku != "CT-QTZ") return new CatalogOptionsRead(null, "unknown_sku");
        return new CatalogOptionsRead(new CatalogOptions(
            new CatalogProduct("CT-QTZ", "Quartz countertop", "sqft", 89m, "USD"), "countertop", 25m,
            [
                new("edge", "options", "Edge style", null, "single", "edgeFt", true, null,
                    [new("EDGE-EASED", "Eased edge", "ft", 0m, "USD", null, 0), new("EDGE-OGEE", "Ogee edge", "ft", 18m, "USD", null, 0)]),
                new("cutouts", "options", "Cut-outs", null, "quantity", "choice", false, null,
                    [new("CUT-FAUCET", "Faucet hole", "each", 25m, "USD", null, 4)]),
                new("plan", "protection", "Protection plan", null, "single", "fixed", true, "No protection plan",
                    [new("PLAN-10Y", "10-year protection plan", "each", 399m, "USD", null, 0)]),
            ]));
    }

    public async Task<CatalogRead> GetAsync(HttpContext http, string? zip, CancellationToken ct)
    {
        LastZip = zip;
        if (Down) return CatalogRead.Unavailable;
        var tenant = await tenants.ResolveAsync(http, ct);
        var list = ByTenant[tenant.TenantId!];
        return zip switch
        {
            null or "62701" => new CatalogRead(list.ToDictionary(p => p.Sku)),
            "60601" => new CatalogRead(list.Where(p => p.Sku == "PAINT").Select(p => p with { ListPrice = 39.00m }).ToDictionary(p => p.Sku)),
            "nope" => new CatalogRead(Products: null, "invalid_zip"),
            _ => new CatalogRead(Products: null, "zip_not_served"),
        };
    }
}

public sealed class QuotesFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    public InMemoryQuoteStore Store { get; } = new();
    public InMemoryProjectStore Projects { get; } = new();
    public InMemoryDepositStore Deposits { get; } = new();
    public InMemoryVisitStore Visits { get; } = new();
    public InMemoryInstallStore Installs { get; } = new();
    public InMemoryCallStore Calls { get; } = new();
    public InMemoryContactStore Contacts { get; }
    public FakeEmail Email { get; } = new();
    public FakeContactChannels Channels { get; } = new();
    public FakeTenantLookup Tenants { get; } = new();
    public FakeCustomerDirectory Customers { get; }
    /// <summary>6d: when set (with Payments:Provider = stripe), Stripe's API is this fake.</summary>
    public HttpMessageHandler? StripeHandler { get; set; }
    private readonly Dictionary<string, string> _settings = [];

    /// <summary>Extra configuration; call before the first request (the host is built then).</summary>
    public void Configure(string key, string value) => _settings[key] = value;
    public FakeCatalog Catalog { get; }
    public FakeTimeProvider Clock { get; } = new(Now);

    public QuotesFactory()
    {
        Catalog = new FakeCatalog(Tenants);
        Customers = new FakeCustomerDirectory(Tenants);
        Contacts = new InMemoryContactStore(Installs);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Authority", "http://idp.test/realms/builderpro");
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.UseSetting("ConnectionStrings:quotesdb", "Host=unused.test;Database=quotesdb");
        builder.UseSetting("Calls:WebhookSecret", "calls-test-secret");
        builder.UseSetting("Calls:DispatchSeconds", "3600");
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IQuoteStore>();
            s.AddSingleton<IQuoteStore>(Store);
            s.RemoveAll<IProjectStore>();
            s.AddSingleton<IProjectStore>(Projects);
            s.RemoveAll<IDepositStore>();
            s.AddSingleton<IDepositStore>(Deposits);
            s.RemoveAll<IVisitStore>();
            s.AddSingleton<IVisitStore>(Visits);
            s.RemoveAll<IContactStore>();
            s.AddSingleton<IContactStore>(Contacts);
            s.RemoveAll<INotificationOutbox>();
            s.AddSingleton<INotificationOutbox>(Contacts);
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(Email);
            s.RemoveAll<ICallStore>();
            s.AddSingleton<ICallStore>(Calls);
            s.RemoveAll<IProfileReader>();
            s.AddSingleton<IProfileReader>(new FakeProfiles(Tenants));
            s.RemoveAll<IInstallStore>();
            s.AddSingleton<IInstallStore>(Installs);
            s.RemoveAll<IContactChannels>();
            s.AddSingleton<IContactChannels>(Channels);
            s.RemoveAll<ITenantLookup>();
            s.AddSingleton<ITenantLookup>(Tenants);
            if (StripeHandler is not null)
                s.AddHttpClient<IPaymentProvider, StripePayments>().ConfigurePrimaryHttpMessageHandler(() => StripeHandler);
            s.RemoveAll<ICustomerDirectory>();
            s.AddSingleton<ICustomerDirectory>(Customers);
            s.RemoveAll<ICatalogPrices>();
            s.AddSingleton<ICatalogPrices>(Catalog);
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
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

public sealed class QuoteApiTests(QuotesFactory f) : IClassFixture<QuotesFactory>
{
    private readonly QuotesFactory _f = f;

    private static object Body(params (string Sku, decimal Qty)[] lines) => new { lines = lines.Select(l => new { sku = l.Sku, quantity = l.Qty }) };

    private async Task<QuoteDto> CreateOk(string sub, params (string Sku, decimal Qty)[] lines)
    {
        var r = await _f.As(sub).PostAsJsonAsync("/api/quotes", Body(lines));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // ---------- Pricing comes from the catalog and the domain, never the request ----------

    [Fact]
    public async Task Creates_a_priced_draft_with_catalog_prices_and_tenant_tax()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes", Body(("PAINT", 3), ("LABOR", 2.5m)));

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var q = (await r.Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal($"/api/quotes/{q.Id}", r.Headers.Location!.OriginalString);
        Assert.Equal("draft", q.Status);
        Assert.Equal("c-alice", q.CustomerId);
        Assert.Equal("USD", q.Currency);
        Assert.Equal(34.98m, q.Lines[0].UnitPrice);
        Assert.Equal("Interior paint", q.Lines[0].Name);
        Assert.Equal(267.44m, q.Subtotal);
        Assert.Equal(0.0825m, q.TaxRate);   // t-acme's configured rate
        Assert.Equal(22.06m, q.Tax);
        Assert.Equal(289.50m, q.Total);
    }

    [Fact]
    public async Task A_price_in_the_request_is_ignored()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes",
            new { lines = new[] { new { sku = "PAINT", quantity = 1m, unitPrice = 0.01m } }, total = 0.01m });

        var q = (await r.Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal(34.98m, q.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task Each_tenant_prices_with_its_own_catalog_and_tax()
    {
        var q = await CreateOk("carol", ("PAINT", 1));
        Assert.Equal(42.00m, q.Lines[0].UnitPrice);   // globex's PAINT, not acme's
        Assert.Equal(0.07m, q.TaxRate);
        Assert.Equal(44.94m, q.Total);
    }

    // ---------- Invalid input ----------

    [Fact]
    public async Task Sku_not_in_your_catalog_is_400_with_the_line()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes", Body(("PAINT", 1), ("DECK", 1)));   // DECK is globex's

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var err = (await ProblemOf(r)).GetProperty("errors")[0];
        Assert.Equal("unknown_sku", err.GetProperty("code").GetString());
        Assert.Equal(1, err.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Domain_rule_violations_come_back_as_codes()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes", Body(("PAINT", 0), ("PAINT", 1.2345m)));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var codes = (await ProblemOf(r)).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();
        Assert.Equal(["invalid_quantity", "duplicate_sku", "invalid_quantity"], codes);
    }

    [Fact]
    public async Task Empty_quote_is_400()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes", new { lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("no_lines", (await ProblemOf(r)).GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Malformed_sku_is_400_before_the_catalog_is_called()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes", Body(("bad sku!", 1)));
        Assert.Equal("invalid_sku", (await ProblemOf(r)).GetProperty("errors")[0].GetProperty("code").GetString());
    }

    // ---------- Who may do what ----------

    [Fact]
    public async Task Creating_needs_quotes_write()
    {
        var r = await _f.As("bob").PostAsJsonAsync("/api/quotes", Body(("PAINT", 1)));   // staff, read-only
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("missing_scope", (await ProblemOf(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reading_needs_quotes_read()
    {
        var r = await _f.As("nora").GetAsync("/api/quotes");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task No_membership_is_passed_through()
    {
        var r = await _f.As("erin").GetAsync("/api/quotes");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("no_membership", (await ProblemOf(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/quotes")).StatusCode);
    }

    // ---------- Visibility: customer < staff < tenant ----------

    [Fact]
    public async Task Customers_see_only_their_own_quotes_staff_see_the_tenants_other_tenants_see_nothing()
    {
        var mine = await CreateOk("alice", ("PAINT", 1));
        var theirs = await CreateOk("abe", ("LABOR", 1));

        var alices = await _f.As("alice").GetFromJsonAsync<QuoteSummaryDto[]>("/api/quotes");
        Assert.Contains(alices!, q => q.Id == mine.Id);
        Assert.DoesNotContain(alices!, q => q.Id == theirs.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("alice").GetAsync($"/api/quotes/{theirs.Id}")).StatusCode);

        var bobs = await _f.As("bob").GetFromJsonAsync<QuoteSummaryDto[]>("/api/quotes");
        Assert.Contains(bobs!, q => q.Id == mine.Id);
        Assert.Contains(bobs!, q => q.Id == theirs.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("carol").GetAsync($"/api/quotes/{mine.Id}")).StatusCode);
        Assert.DoesNotContain(await _f.As("carol").GetFromJsonAsync<QuoteSummaryDto[]>("/api/quotes") ?? [], q => q.Id == mine.Id);
    }

    [Fact]
    public async Task Get_returns_the_stored_snapshot()
    {
        var created = await CreateOk("alice", ("PAINT", 2));
        var read = await _f.As("alice").GetFromJsonAsync<QuoteDto>($"/api/quotes/{created.Id}");

        Assert.Equal(created.Total, read!.Total);
        Assert.Equal(created.Lines.Single().LineTotal, read.Lines.Single().LineTotal);
        Assert.Equal(QuotesFactory.Now, read.CreatedAt);
    }

    // ---------- Fail closed ----------

    [Fact]
    public async Task Catalog_down_means_503_and_nothing_stored()
    {
        var before = (await _f.Store.ListAsync("t-acme", null, 1000, default)).Count;
        _f.Catalog.Down = true;
        try
        {
            var r = await _f.As("alice").PostAsJsonAsync("/api/quotes", Body(("PAINT", 1)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Equal("catalog_unavailable", (await ProblemOf(r)).GetProperty("code").GetString());
        }
        finally
        {
            _f.Catalog.Down = false;
        }
        Assert.Equal(before, (await _f.Store.ListAsync("t-acme", null, 1000, default)).Count);
    }
}

public sealed class EstimateTests(QuotesFactory f) : IClassFixture<QuotesFactory>
{
    private readonly QuotesFactory _f = f;

    private async Task<EstimateDto[]> Estimate(string sub, params (string Sku, decimal Qty)[] items)
    {
        var r = await _f.As(sub).PostAsJsonAsync("/api/quotes/estimates", new { items = items.Select(i => new { sku = i.Sku, quantity = i.Qty }) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<EstimateDto[]>())!;
    }

    [Fact]
    public async Task Each_item_is_priced_alone_with_tenant_tax_and_nothing_is_stored()
    {
        var before = (await _f.Store.ListAsync("t-acme", null, 1000, default)).Count;

        var e = await Estimate("alice", ("PAINT", 2), ("LABOR", 10));

        Assert.Equal(69.96m, e[0].Subtotal);       // 2 x 34.98
        Assert.Equal(5.77m, e[0].Tax);             // 69.96 x 0.0825 = 5.7717
        Assert.Equal(75.73m, e[0].Total);
        Assert.Equal(650.00m, e[1].Subtotal);
        Assert.All(e, x => Assert.Empty(x.Errors));
        Assert.Equal(before, (await _f.Store.ListAsync("t-acme", null, 1000, default)).Count);
    }

    [Fact]
    public async Task Bad_items_report_their_own_errors_and_good_ones_still_price()
    {
        var e = await Estimate("alice", ("DECK", 1), ("PAINT", 0), ("PAINT", 1));

        Assert.Equal("unknown_sku", e[0].Errors.Single().Code);   // globex's product
        Assert.Equal("invalid_quantity", e[1].Errors.Single().Code);
        Assert.Null(e[1].Total);
        Assert.Equal(37.87m, e[2].Total);                          // 34.98 + 2.89 tax
    }

    [Fact]
    public async Task Estimates_use_your_own_catalog()
    {
        var e = await Estimate("carol", ("PAINT", 1));
        Assert.Equal(42.00m, e[0].UnitPrice);   // globex price, not acme's
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task One_to_six_items_per_request(int count)
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/estimates",
            new { items = Enumerable.Range(0, count).Select(_ => new { sku = "PAINT", quantity = 1 }) });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Needs_quotes_read()
    {
        var r = await _f.As("nora").PostAsJsonAsync("/api/quotes/estimates", new { items = new[] { new { sku = "PAINT", quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}

public sealed class RoofEstimatorApiTests(QuotesFactory f) : IClassFixture<QuotesFactory>
{
    private readonly QuotesFactory _f = f;

    [Fact]
    public async Task Returns_quantities_for_a_roof()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/estimators/roof", new { footprintSqFt = 2000, pitchRise = 6, wastePercent = 10, eavesAndRakesFt = 180 });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var q = (await r.Content.ReadFromJsonAsync<BuilderPro.Quotes.Domain.RoofQuantities>())!;
        Assert.Equal((24.60m, 74, 3, 18), (q.Squares, q.ShingleBundles, q.UnderlaymentRolls, q.DripEdgePieces));
    }

    [Fact]
    public async Task Bad_input_is_400_with_codes()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/estimators/roof", new { footprintSqFt = 0, pitchRise = 40 });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var codes = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();
        Assert.Equal(["invalid_footprint", "invalid_pitch"], codes);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        var r = await _f.CreateClient().PostAsJsonAsync("/api/quotes/estimators/roof", new { footprintSqFt = 1000, pitchRise = 6 });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

public sealed class PreviewTests(QuotesFactory f) : IClassFixture<QuotesFactory>
{
    private readonly QuotesFactory _f = f;

    [Fact]
    public async Task Preview_prices_exactly_like_create_but_stores_nothing()
    {
        var body = new { lines = new[] { new { sku = "PAINT", quantity = 3m }, new { sku = "LABOR", quantity = 2.5m } } };
        var before = (await _f.Store.ListAsync("t-acme", null, 1000, default)).Count;

        var preview = (await (await _f.As("alice").PostAsJsonAsync("/api/quotes/preview", body)).Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal(before, (await _f.Store.ListAsync("t-acme", null, 1000, default)).Count);

        var created = (await (await _f.As("alice").PostAsJsonAsync("/api/quotes", body)).Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal((created.Subtotal, created.Tax, created.Total), (preview.Subtotal, preview.Tax, preview.Total));
        Assert.Equal(289.50m, preview.Total);
        Assert.Equal("preview", preview.Status);
    }

    [Fact]
    public async Task Preview_needs_only_quotes_read()
    {
        var r = await _f.As("bob").PostAsJsonAsync("/api/quotes/preview", new { lines = new[] { new { sku = "PAINT", quantity = 1m } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);   // bob is read-only staff; creating stays 403
    }

    [Fact]
    public async Task Preview_reports_the_same_errors_as_create()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/preview", new { lines = new[] { new { sku = "DECK", quantity = 1m } } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---- 2n-a: prices per ZIP zone ----

    [Fact]
    public async Task A_zip_prices_with_that_zones_price()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/preview", new { lines = new[] { new { sku = "PAINT", quantity = 2m } }, zip = "60601" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("60601", _f.Catalog.LastZip);
        var quote = (await r.Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal(39.00m, quote.Lines[0].UnitPrice);   // the zone's price, not the 34.98 list price
        Assert.Equal(78.00m, quote.Subtotal);
    }

    [Fact]
    public async Task A_product_not_sold_in_the_zip_is_rejected()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/preview", new { lines = new[] { new { sku = "LABOR", quantity = 1m } }, zip = "60601" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("unknown_sku", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("99999", HttpStatusCode.UnprocessableEntity, "zip_not_served")]
    [InlineData("nope", HttpStatusCode.BadRequest, "invalid_zip")]
    public async Task A_refused_zip_keeps_the_catalogs_reason(string zip, HttpStatusCode status, string code)
    {
        var quote = await _f.As("alice").PostAsJsonAsync("/api/quotes/preview", new { lines = new[] { new { sku = "PAINT", quantity = 1m } }, zip });
        Assert.Equal(status, quote.StatusCode);
        Assert.Contains(code, await quote.Content.ReadAsStringAsync());

        var estimate = await _f.As("alice").PostAsJsonAsync("/api/quotes/estimates", new { items = new[] { new { sku = "PAINT", quantity = 1m } }, zip });
        Assert.Equal(status, estimate.StatusCode);
    }

    [Fact]
    public async Task Without_a_zip_list_prices_are_used_as_before()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/preview", new { lines = new[] { new { sku = "PAINT", quantity = 1m } } });
        Assert.Null(_f.Catalog.LastZip);
        Assert.Equal(34.98m, (await r.Content.ReadFromJsonAsync<QuoteDto>())!.Lines[0].UnitPrice);
    }
}

/// <summary>2n-b..e: guided quotes through the API, with the fake catalog above.</summary>
public sealed class GuidedQuoteApiTests(QuotesFactory f) : IClassFixture<QuotesFactory>
{
    private readonly QuotesFactory _f = f;

    private static object Body(string zip = "62701", string sku = "CT-QTZ", object? choices = null, decimal width = 120m) => new
    {
        zip, sku,
        measurements = new { countertop = new { countertop = new[] { new { widthIn = width, depthIn = 25m } }, island = new[] { new { widthIn = 72m, depthIn = 36m } } } },
        choices = choices ?? new object[] { new { group = "edge", sku = "EDGE-OGEE" }, new { group = "cutouts", sku = "CUT-FAUCET", quantity = 5 }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    [Fact]
    public async Task Preview_prices_measurements_options_and_protection_by_group()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/guided/preview", Body());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var q = (await r.Content.ReadFromJsonAsync<GuidedQuoteDto>())!;

        Assert.Equal(38.84m, q.Measurement.Values["sqft"]);   // 20.84 + 18.00
        Assert.Equal(28m, q.Measurement.Values["edgeFt"]);    // 120 in front + 216 in island perimeter = 336 in
        Assert.Equal(["product", "options", "options", "protection"], q.Lines.Select(l => l.Group));
        Assert.Equal(3456.76m, q.Lines[0].LineTotal);          // 38.84 x 89
        Assert.Equal(504m, q.Lines[1].LineTotal);              // 28 ft ogee x 18
        Assert.Equal(1m, q.Lines[2].Quantity);                 // 5 faucet holes, 4 included
        Assert.True(q.Complete);
        Assert.Empty(q.Missing);
    }

    [Fact]
    public async Task Preview_lists_open_steps_and_save_refuses_them()
    {
        var body = Body(choices: new object[] { new { group = "edge", sku = "EDGE-EASED" } });
        var preview = (await (await _f.As("alice").PostAsJsonAsync("/api/quotes/guided/preview", body)).Content.ReadFromJsonAsync<GuidedQuoteDto>())!;
        Assert.Equal(["plan"], preview.Missing);
        Assert.False(preview.Complete);

        var save = await _f.As("alice").PostAsJsonAsync("/api/quotes/guided", body);
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Contains("incomplete_quote", await save.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_small_top_gets_a_visible_minimum_line()
    {
        var body = new
        {
            zip = "62701", sku = "CT-QTZ",
            measurements = new { countertop = new { countertop = new[] { new { widthIn = 60m, depthIn = 25m } } } },
            choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = (string?)null } },
        };
        var q = (await (await _f.As("alice").PostAsJsonAsync("/api/quotes/guided/preview", body)).Content.ReadFromJsonAsync<GuidedQuoteDto>())!;
        Assert.Equal("minimum", q.Lines[1].Group);
        Assert.Equal(14.58m, q.Lines[1].Quantity);             // 25 - 10.42
    }

    [Fact]
    public async Task Save_stores_zip_groups_and_the_request_and_my_quotes_shows_it()
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/guided", Body());
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var saved = (await r.Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal("62701", saved.Zip);
        Assert.Equal("protection", saved.Lines[^1].Group);

        var stored = await _f.Store.FindAsync("t-acme", null, saved.Id, default);
        Assert.Contains("\"sku\":\"CT-QTZ\"", stored!.Guided!.RequestJson);
        Assert.DoesNotContain("89", stored.Guided.RequestJson);   // answers and measurements only, never prices

        var mine = await _f.As("alice").GetFromJsonAsync<QuoteSummaryDto[]>("/api/quotes");
        var summary = mine!.Single(q => q.Id == saved.Id);
        Assert.Equal(("62701", "Quartz countertop"), (summary.Zip, summary.Title));
    }

    [Theory]
    [InlineData("nope", "CT-QTZ", HttpStatusCode.BadRequest, "invalid_zip")]
    [InlineData("90210", "CT-QTZ", HttpStatusCode.UnprocessableEntity, "zip_not_served")]
    [InlineData("62701", "PAINT", HttpStatusCode.BadRequest, "unknown_sku")]
    public async Task Bad_zip_or_product_is_named(string zip, string sku, HttpStatusCode status, string code)
    {
        var r = await _f.As("alice").PostAsJsonAsync("/api/quotes/guided/preview", Body(zip, sku));
        Assert.Equal(status, r.StatusCode);
        Assert.Contains(code, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Impossible_measurements_and_answers_are_named()
    {
        var tooNarrow = await _f.As("alice").PostAsJsonAsync("/api/quotes/guided/preview", Body(width: 2m));
        Assert.Contains("invalid_section", await tooNarrow.Content.ReadAsStringAsync());

        var badOption = await _f.As("alice").PostAsJsonAsync("/api/quotes/guided/preview",
            Body(choices: new object[] { new { group = "edge", sku = "EDGE-GOLD" } }));
        Assert.Equal(HttpStatusCode.BadRequest, badOption.StatusCode);
        Assert.Contains("unknown_option", await badOption.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_needs_quotes_write()
    {
        var r = await _f.As("bob").PostAsJsonAsync("/api/quotes/guided", Body());   // read-only staff
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}

/// <summary>2n-f: projects keep answers, re-price on read, issue numbered quotes, and only the customer accepts.</summary>
public sealed class ProjectApiTests(QuotesFactory f) : IClassFixture<QuotesFactory>
{
    private readonly QuotesFactory _f = f;

    private static readonly object CompleteAnswers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } }, island = new[] { new { widthIn = 72m, depthIn = 36m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-OGEE" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    private async Task<ProjectDto> NewProject(string sub = "alice")
    {
        var r = await _f.As(sub).PostAsJsonAsync("/api/projects", new { zip = "62701", title = "Kitchen countertops" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    private async Task<ProjectDto> Put(Guid id, object answers, string sub = "alice") =>
        (await (await _f.As(sub).PutAsJsonAsync($"/api/projects/{id}/answers", answers)).Content.ReadFromJsonAsync<ProjectDto>())!;

    [Fact]
    public async Task A_project_moves_from_discover_to_configure_and_reprices_its_answers_on_every_read()
    {
        var p = await NewProject();
        Assert.Equal(("discover", "62701", "Kitchen countertops"), (p.Stage, p.Zip, p.Title));
        Assert.Null(p.Preview);

        var configured = await Put(p.Id, CompleteAnswers);
        Assert.Equal("configure", configured.Stage);
        Assert.True(configured.Preview!.Complete);
        Assert.Equal(38.84m, configured.Preview.Measurement.Values["sqft"]);

        var read = (await _f.As("alice").GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!;
        Assert.Equal(configured.Preview.Total, read.Preview!.Total);
        Assert.DoesNotContain("89", read.Answers.GetRawText());   // answers only, never prices
    }

    [Fact]
    public async Task Issuing_numbers_the_quote_and_holds_it_14_days()
    {
        var p = await NewProject();
        await Put(p.Id, CompleteAnswers);

        var issued = (await (await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null)).Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal("quote", issued.Stage);
        Assert.True(issued.Quote!.Number > 1000);
        Assert.Equal(QuotesFactory.Now.AddDays(14), issued.Quote.ValidUntil);
        Assert.True(issued.Quote.Current);
        Assert.Equal(issued.Preview!.Total, issued.Quote.Total);

        var again = (await (await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null)).Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(issued.Quote.Number + 1, again.Quote!.Number);   // numbers never repeat within a store
    }

    [Fact]
    public async Task An_incomplete_project_cant_be_quoted()
    {
        var p = await NewProject();
        await Put(p.Id, new { sku = "CT-QTZ", measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } }, choices = Array.Empty<object>() });
        var r = await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("incomplete_quote", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accepting_alone_does_not_hold_the_price_the_deposit_is_due()
    {
        var p = await NewProject();
        await Put(p.Id, CompleteAnswers);
        await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);

        var r = await _f.As("alice").PostAsync($"/api/projects/{p.Id}/accept", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var accepted = (await r.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(("deposit", "accepted", false), (accepted.Stage, accepted.Quote!.Status, accepted.Quote.Held));
        Assert.Equal(QuotesFactory.Now, accepted.Quote.AcceptedAt);

        // Not held yet: the customer may still change answers, which needs an updated quote.
        var changed = await _f.As("alice").PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    [Fact]
    public async Task An_agent_can_never_accept_even_with_the_customers_token()
    {
        var p = await NewProject();
        await Put(p.Id, CompleteAnswers);
        await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);

        var agent = _f.As("alice");
        agent.DefaultRequestHeaders.Add(AgentCall.Header, "concierge");
        var r = await agent.PostAsync($"/api/projects/{p.Id}/accept", null);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("agent_cannot_accept", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_changed_answer_makes_the_quote_outdated_until_reissued()
    {
        var p = await NewProject();
        await Put(p.Id, CompleteAnswers);
        await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);
        var changed = await Put(p.Id, new
        {
            sku = "CT-QTZ",
            measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } }, island = new[] { new { widthIn = 72m, depthIn = 36m } } } },
            choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
        });
        Assert.False(changed.Quote!.Current);

        var r = await _f.As("alice").PostAsync($"/api/projects/{p.Id}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("quote_outdated", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_same_answers_in_another_shape_keep_the_quote_current()
    {
        var p = await NewProject();
        await Put(p.Id, CompleteAnswers);
        await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);

        // Seen live: the page re-saved the concierge's answers with empty parts left out / added.
        var same = await Put(p.Id, new
        {
            sku = "CT-QTZ",
            measurements = new
            {
                countertop = new { layout = (string?)null, countertop = new[] { new { widthIn = 120m, depthIn = 25m } }, island = new[] { new { widthIn = 72m, depthIn = 36m } }, backsplash = Array.Empty<object>() },
                roof = (object?)null, rooms = (object?)null, count = (decimal?)null,
            },
            choices = new object[] { new { group = "edge", sku = "EDGE-OGEE", quantity = (decimal?)null }, new { group = "plan", sku = "PLAN-10Y" } },
        });
        Assert.True(same.Quote!.Current);
        Assert.Equal(HttpStatusCode.OK, (await _f.As("alice").PostAsync($"/api/projects/{p.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task An_expired_quote_cant_be_accepted()
    {
        using var f = new QuotesFactory();
        var p = (await (await f.As("alice").PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As("alice").PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers);
        await f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);
        f.Clock.Advance(TimeSpan.FromDays(15));

        var r = await f.As("alice").PostAsync($"/api/projects/{p.Id}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("quote_expired", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Projects_are_private_to_their_customer_and_store()
    {
        var p = await NewProject();
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("abe").GetAsync($"/api/projects/{p.Id}")).StatusCode);    // another acme customer
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("carol").GetAsync($"/api/projects/{p.Id}")).StatusCode);  // another store
        Assert.Equal(HttpStatusCode.OK, (await _f.As("bob").GetAsync($"/api/projects/{p.Id}")).StatusCode);          // acme staff may look
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.As("bob").PostAsync($"/api/projects/{p.Id}/accept", null)).StatusCode); // but never accept

        var mine = await _f.As("alice").GetFromJsonAsync<ProjectSummaryDto[]>("/api/projects");
        Assert.Contains(mine!, x => x.Id == p.Id);
        Assert.DoesNotContain((await _f.As("abe").GetFromJsonAsync<ProjectSummaryDto[]>("/api/projects"))!, x => x.Id == p.Id);
    }

    [Theory]
    [InlineData("90210", HttpStatusCode.UnprocessableEntity)]
    [InlineData("nope", HttpStatusCode.BadRequest)]
    public async Task A_project_needs_a_served_zip(string zip, HttpStatusCode status) =>
        Assert.Equal(status, (await _f.As("alice").PostAsJsonAsync("/api/projects", new { zip })).StatusCode);
}

/// <summary>6a: the 5 % deposit holds the price; only the customer pays.</summary>
public sealed class DepositApiTests(QuotesFactory f) : IClassFixture<QuotesFactory>
{
    private readonly QuotesFactory _f = f;

    private static readonly object CompleteAnswers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } }, island = new[] { new { widthIn = 72m, depthIn = 36m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-OGEE" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    private static async Task<ProjectDto> Accepted(QuotesFactory f, string sub = "alice")
    {
        var p = (await (await f.As(sub).PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As(sub).PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/quote", null);
        return (await (await f.As(sub).PostAsync($"/api/projects/{p.Id}/accept", null)).Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    [Fact]
    public async Task Paying_five_percent_holds_the_price_moves_to_the_measure_visit_and_locks_the_answers()
    {
        var p = await Accepted(_f);
        var start = await _f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var deposit = (await start.Content.ReadFromJsonAsync<DepositDto>())!;
        Assert.Equal(Math.Round(p.Quote!.Total * 0.05m, 2, MidpointRounding.AwayFromZero), deposit.Amount);
        Assert.Equal(("processing", 5m, $"/pay/{deposit.Id}"), (deposit.Status, deposit.Percent, deposit.CheckoutUrl));

        var again = (await (await _f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        Assert.Equal(deposit.Id, again.Id);   // one pending deposit per quote

        var paid = await _f.As("alice").PostAsync($"/api/deposits/{deposit.Id}/pay", null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var after = (await _f.As("alice").GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!;
        Assert.Equal(("measure", true, "succeeded"), (after.Stage, after.Quote!.Held, after.Deposit!.Status));
        Assert.Equal(QuotesFactory.Now, after.Deposit.PaidAt);
        Assert.Equal(HttpStatusCode.Conflict, (await _f.As("alice").PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null)).StatusCode);
    }

    [Fact]
    public async Task Only_the_customer_pays_never_staff_or_an_agent()
    {
        var p = await Accepted(_f);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.As("bob").PostAsync($"/api/projects/{p.Id}/deposit", null)).StatusCode);   // staff, read-only
        var agent = _f.As("alice");
        agent.DefaultRequestHeaders.Add(AgentCall.Header, "concierge");
        var r = await agent.PostAsync($"/api/projects/{p.Id}/deposit", null);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("agent_cannot_pay", await r.Content.ReadAsStringAsync());

        var deposit = (await (await _f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/deposits/{deposit.Id}/pay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As("abe").PostAsync($"/api/deposits/{deposit.Id}/pay", null)).StatusCode);   // another customer
    }

    [Fact]
    public async Task No_deposit_before_accepting_and_no_payment_after_the_quote_expired()
    {
        using var f = new QuotesFactory();
        var p = (await (await f.As("alice").PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As("alice").PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers);
        await f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);
        var early = await f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null);
        Assert.Contains("not_accepted", await early.Content.ReadAsStringAsync());

        await f.As("alice").PostAsync($"/api/projects/{p.Id}/accept", null);
        var deposit = (await (await f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        f.Clock.Advance(TimeSpan.FromDays(15));
        var late = await f.As("alice").PostAsync($"/api/deposits/{deposit.Id}/pay", null);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("quote_expired", await late.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Changing_answers_after_accepting_needs_an_updated_quote_before_any_deposit()
    {
        var p = await Accepted(_f);
        await _f.As("alice").PutAsJsonAsync($"/api/projects/{p.Id}/answers", new
        {
            sku = "CT-QTZ",
            measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } },
            choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
        });
        var r = await _f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);   // back at the quote step: get and accept an updated quote first
    }
}


/// <summary>1i: a salesperson prepares the project; the customer accepts and pays; the sale is credited for commission.</summary>
public sealed class SalespersonTests
{
    private static readonly object CompleteAnswers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    private static async Task<ProjectDto> Prepare(QuotesFactory f, string salesperson = "sam", string customer = "c-alice")
    {
        var r = await f.As(salesperson).PostAsJsonAsync("/api/projects", new { zip = "62701", customerId = customer, title = "Kitchen for Alice" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var p = (await r.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await f.As(salesperson).PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.As(salesperson).PostAsync($"/api/projects/{p.Id}/quote", null)).StatusCode);
        return p;
    }

    private static async Task Buy(QuotesFactory f, Guid projectId, string customer = "alice")
    {
        Assert.Equal(HttpStatusCode.OK, (await f.As(customer).PostAsync($"/api/projects/{projectId}/accept", null)).StatusCode);
        var deposit = (await (await f.As(customer).PostAsync($"/api/projects/{projectId}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await f.As(customer).PostAsync($"/api/deposits/{deposit.Id}/pay", null)).StatusCode);
    }

    [Fact]
    public async Task A_salesperson_prepares_a_quote_the_customer_sees_it_accepts_and_pays_and_the_sale_is_credited()
    {
        using var f = new QuotesFactory();
        var p = await Prepare(f);

        // The customer sees it among their own projects, prepared by the salesperson.
        var mine = await f.As("alice").GetFromJsonAsync<ProjectSummaryDto[]>("/api/projects");
        Assert.Contains(mine!, x => x.Id == p.Id && x.PreparedBy != null && x.QuoteNumber != null);

        // Accepting and paying stay the customer's: the salesperson can't.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("sam").PostAsync($"/api/projects/{p.Id}/accept", null)).StatusCode);
        await Buy(f, p.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("sam").PostAsync($"/api/projects/{p.Id}/deposit", null)).StatusCode);

        var report = (await f.As("sam").GetFromJsonAsync<CommissionReportDto>("/api/sales/commission"))!;
        var line = Assert.Single(report.Salespeople);
        var quoted = (await f.As("alice").GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!.Quote!.Total;
        Assert.Equal((1, quoted), (line.Sales, line.SoldTotal));
        Assert.Null(line.Commission);   // no rate configured: sales only
        Assert.Equal(p.Id, Assert.Single(line.Lines).ProjectId);
    }

    [Fact]
    public async Task Only_a_salesperson_starts_projects_for_customers_of_their_own_store()
    {
        using var f = new QuotesFactory();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("bob").PostAsJsonAsync("/api/projects", new { zip = "62701", customerId = "c-alice" })).StatusCode);   // staff, no sales scope
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("abe").PostAsJsonAsync("/api/projects", new { zip = "62701", customerId = "c-alice" })).StatusCode);   // a customer
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("sam").PostAsJsonAsync("/api/projects", new { zip = "62701", customerId = "c-carol" })).StatusCode);   // globex's customer
        Assert.Equal(HttpStatusCode.BadRequest, (await f.As("sam").PostAsJsonAsync("/api/projects", new { zip = "62701", customerId = "not valid!" })).StatusCode);
        f.Customers.Down = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.As("sam").PostAsJsonAsync("/api/projects", new { zip = "62701", customerId = "c-alice" })).StatusCode);
    }

    [Fact]
    public async Task Another_salesperson_cannot_change_a_colleagues_customer_project()
    {
        using var f = new QuotesFactory();
        var p = await Prepare(f);
        var r = await f.As("sue").PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("not_your_customer", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("sue").PostAsync($"/api/projects/{p.Id}/quote", null)).StatusCode);
        // The customer may still change their own project.
        Assert.Equal(HttpStatusCode.OK, (await f.As("alice").PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers)).StatusCode);
    }

    [Fact]
    public async Task Salespeople_see_their_own_commission_managers_see_everyones_at_the_store_rate()
    {
        using var f = new QuotesFactory();
        f.Configure("Sales:CommissionPercent:t-acme", "3");
        var a = await Prepare(f, "sam", "c-alice");
        await Buy(f, a.Id, "alice");
        var b = await Prepare(f, "sue", "c-abe");
        await Buy(f, b.Id, "abe");
        var notPaid = await Prepare(f, "sam", "c-alice");   // quoted, never paid: not a sale
        _ = notPaid;

        var sams = (await f.As("sam").GetFromJsonAsync<CommissionReportDto>("/api/sales/commission"))!;
        var mine = Assert.Single(sams.Salespeople);
        Assert.Equal((1, 3m), (mine.Sales, mine.CommissionPercent));
        Assert.Equal(Math.Round(mine.SoldTotal * 0.03m, 2, MidpointRounding.AwayFromZero), mine.Commission);

        var all = (await f.As("boss").GetFromJsonAsync<CommissionReportDto>("/api/sales/commission"))!;
        Assert.Equal(2, all.Salespeople.Count);

        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("alice").GetAsync("/api/sales/commission")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("bob").GetAsync("/api/sales/commission")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.As("sam").GetAsync("/api/sales/commission?from=2026-12-01T00:00:00Z&to=2026-11-01T00:00:00Z")).StatusCode);
        var gus = (await f.As("gus").GetFromJsonAsync<CommissionReportDto>("/api/sales/commission"))!;
        Assert.Equal(0, Assert.Single(gus.Salespeople).Sales);   // other store: nothing of acme's
    }
}


/// <summary>6b: book the measure visit, staff record the sizes at the held prices, the customer confirms a change.</summary>
public sealed class MeasureVisitApiTests
{
    private static object Measures(decimal width) => new { countertop = new { countertop = new[] { new { widthIn = width, depthIn = 25m } }, island = new[] { new { widthIn = 72m, depthIn = 36m } } } };

    private static readonly object CompleteAnswers = new
    {
        sku = "CT-QTZ",
        measurements = Measures(120m),
        choices = new object[] { new { group = "edge", sku = "EDGE-OGEE" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    private static readonly DateTimeOffset FirstSlot = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);   // Sat 9:00 Central

    private static async Task<ProjectDto> Paid(QuotesFactory f, string sub = "alice")
    {
        var p = (await (await f.As(sub).PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As(sub).PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/quote", null);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/accept", null);
        var d = (await (await f.As(sub).PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        await f.As(sub).PostAsync($"/api/deposits/{d.Id}/pay", null);
        return (await f.As(sub).GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!;
    }

    private static async Task Book(QuotesFactory f, Guid id, string sub = "alice", DateTimeOffset? at = null) =>
        Assert.Equal(HttpStatusCode.OK, (await f.As(sub).PostAsJsonAsync($"/api/projects/{id}/visit", new { start = at ?? FirstSlot })).StatusCode);

    [Fact]
    public async Task The_customer_books_a_visit_time_and_a_full_time_is_refused()
    {
        using var f = new QuotesFactory();
        f.Configure("Visits:PerSlot", "1");
        var p = await Paid(f);
        var slots = (await f.As("alice").GetFromJsonAsync<VisitSlotDto[]>($"/api/projects/{p.Id}/visit/slots"))!;
        Assert.Equal(FirstSlot, slots[0].Start);
        Assert.All(slots, s => Assert.True(s.Available));
        // alice's contact hours are business: Saturday doesn't suit her, weekdays do.
        Assert.Equal(false, slots[0].FitsHours);
        Assert.Contains(slots, s => s.FitsHours == true);
        Assert.All((await f.As("mia").GetFromJsonAsync<VisitSlotDto[]>($"/api/projects/{p.Id}/visit/slots"))!, s => Assert.Null(s.FitsHours));

        await Book(f, p.Id);
        var after = (await f.As("alice").GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!;
        Assert.Equal((FirstSlot, "booked"), (after.Visit!.Start, after.Visit.Status));

        var other = await Paid(f, "abe");
        Assert.False((await f.As("abe").GetFromJsonAsync<VisitSlotDto[]>($"/api/projects/{other.Id}/visit/slots"))![0].Available);
        var full = await f.As("abe").PostAsJsonAsync($"/api/projects/{other.Id}/visit", new { start = FirstSlot });
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Contains("slot_full", await full.Content.ReadAsStringAsync());

        // Moving my own visit frees the first time.
        await Book(f, p.Id, at: slots[1].Start);
        await Book(f, other.Id, "abe");
    }

    [Fact]
    public async Task No_visit_before_the_deposit_and_only_offered_times()
    {
        using var f = new QuotesFactory();
        var fresh = (await (await f.As("alice").PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await f.As("alice").PostAsJsonAsync($"/api/projects/{fresh.Id}/visit", new { start = FirstSlot })).StatusCode);
        var p = await Paid(f);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.As("alice").PostAsJsonAsync($"/api/projects/{p.Id}/visit", new { start = FirstSlot.AddMinutes(30) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("bob").PostAsJsonAsync($"/api/projects/{p.Id}/visit", new { start = FirstSlot })).StatusCode);   // staff without scheduling
        Assert.Equal(HttpStatusCode.OK, (await f.As("mia").PostAsJsonAsync($"/api/projects/{p.Id}/visit", new { start = FirstSlot })).StatusCode);    // store scheduler
    }

    [Fact]
    public async Task Same_measurements_keep_the_total_and_go_straight_to_install()
    {
        using var f = new QuotesFactory();
        var p = await Paid(f);
        await Book(f, p.Id);
        var r = await f.As("mia").PostAsJsonAsync($"/api/projects/{p.Id}/visit/measured", new { measurements = Measures(120m) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var after = (await r.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(("install", "accepted", true), (after.Stage, after.Quote!.Status, after.Quote.Held));
        Assert.Equal(p.Quote!.Total, after.Quote.Total);
        Assert.NotEqual(p.Quote.Number, after.Quote.Number);   // a new numbered (measured) quote
        Assert.Equal("measured", after.Visit!.Status);
    }

    [Fact]
    public async Task A_bigger_kitchen_waits_for_the_customer_to_confirm_the_new_total()
    {
        using var f = new QuotesFactory();
        var p = await Paid(f);
        await Book(f, p.Id);
        var measured = (await (await f.As("mia").PostAsJsonAsync($"/api/projects/{p.Id}/visit/measured", new { measurements = Measures(144m) })).Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(("confirm", "issued", false), (measured.Stage, measured.Quote!.Status, measured.Quote.Held));
        Assert.True(measured.Quote.Total > p.Quote!.Total);

        Assert.Equal(HttpStatusCode.Conflict, (await f.As("alice").PostAsync($"/api/projects/{p.Id}/accept", null)).StatusCode);   // locked: Confirm, not Accept
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("mia").PostAsync($"/api/projects/{p.Id}/confirm", null)).StatusCode);   // staff
        var agent = f.As("alice");
        agent.DefaultRequestHeaders.Add(AgentCall.Header, "concierge");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/projects/{p.Id}/confirm", null)).StatusCode);

        var ok = await f.As("alice").PostAsync($"/api/projects/{p.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var confirmed = (await ok.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(("install", "accepted", true), (confirmed.Stage, confirmed.Quote!.Status, confirmed.Quote.Held));
    }

    [Fact]
    public async Task Only_store_staff_record_measurements_after_a_booked_visit()
    {
        using var f = new QuotesFactory();
        var p = await Paid(f);
        var noVisit = await f.As("mia").PostAsJsonAsync($"/api/projects/{p.Id}/visit/measured", new { measurements = Measures(120m) });
        Assert.Contains("no_visit", await noVisit.Content.ReadAsStringAsync());
        await Book(f, p.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("alice").PostAsJsonAsync($"/api/projects/{p.Id}/visit/measured", new { measurements = Measures(120m) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("bob").PostAsJsonAsync($"/api/projects/{p.Id}/visit/measured", new { measurements = Measures(120m) })).StatusCode);
    }
}


/// <summary>6c: staff move the installation step by step; order updates go only to consented channels.</summary>
public sealed class InstallApiTests
{
    private static readonly object CompleteAnswers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    private static async Task<ProjectDto> Installing(QuotesFactory f, string sub = "alice")
    {
        var p = (await (await f.As(sub).PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As(sub).PutAsJsonAsync($"/api/projects/{p.Id}/answers", CompleteAnswers);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/quote", null);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/accept", null);
        var d = (await (await f.As(sub).PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        await f.As(sub).PostAsync($"/api/deposits/{d.Id}/pay", null);
        await f.As(sub).PostAsJsonAsync($"/api/projects/{p.Id}/visit", new { start = new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero) });
        var measured = (await (await f.As("mia").PostAsJsonAsync($"/api/projects/{p.Id}/visit/measured",
            new { measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } } })).Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal("install", measured.Stage);
        return measured;
    }

    private static Task<HttpResponseMessage> Step(QuotesFactory f, Guid id, string step, DateTimeOffset? when = null, string sub = "mia") =>
        f.As(sub).PostAsJsonAsync($"/api/projects/{id}/install", new { step, scheduledFor = when });

    [Fact]
    public async Task Staff_move_the_install_through_its_steps_to_done_and_the_customer_sees_each()
    {
        using var f = new QuotesFactory();
        var p = await Installing(f);
        var day = QuotesFactory.Now.AddDays(10);
        Assert.Equal(HttpStatusCode.OK, (await Step(f, p.Id, "scheduled", day)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Step(f, p.Id, "scheduled", day.AddDays(2))).StatusCode);   // moved before work starts
        Assert.Equal(HttpStatusCode.OK, (await Step(f, p.Id, "fabricating")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Step(f, p.Id, "scheduled", day)).StatusCode);       // too late to move
        Assert.Equal(HttpStatusCode.Conflict, (await Step(f, p.Id, "done")).StatusCode);                // no skipping
        Assert.Equal(HttpStatusCode.OK, (await Step(f, p.Id, "installing")).StatusCode);
        var done = (await (await Step(f, p.Id, "done")).Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(("done", "done"), (done.Stage, done.Install!.Step));

        var mine = (await f.As("alice").GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!;
        Assert.Equal(new[] { "scheduled", "scheduled", "fabricating", "installing", "done" }, mine.Install!.Events.Select(e => e.Step));
        Assert.Equal(day.AddDays(2), mine.Install.ScheduledFor);
        Assert.Equal(HttpStatusCode.Conflict, (await Step(f, p.Id, "done")).StatusCode);   // closed
    }

    [Fact]
    public async Task Order_updates_go_only_to_the_channels_the_customer_consented_to()
    {
        using var f = new QuotesFactory();
        var a = await Installing(f, "alice");   // consented to texts
        await Step(f, a.Id, "scheduled", QuotesFactory.Now.AddDays(10));
        var b = await Installing(f, "abe");     // no consent to texts or calls
        await Step(f, b.Id, "scheduled", QuotesFactory.Now.AddDays(10));
        f.Channels.Down = true;                 // Identity down: email only
        await Step(f, a.Id, "fabricating");

        var alice = (await f.As("alice").GetFromJsonAsync<NotificationDto[]>($"/api/projects/{a.Id}/notifications"))!;
        Assert.Equal(new[] { "email", "sms", "email" }, alice.Select(n => n.Channel));
        Assert.Contains("scheduled for 2026-11-23", alice[0].Message);
        var abe = (await f.As("abe").GetFromJsonAsync<NotificationDto[]>($"/api/projects/{b.Id}/notifications"))!;
        Assert.Equal(new[] { "email" }, abe.Select(n => n.Channel));
    }

    [Fact]
    public async Task Only_store_staff_record_install_steps_and_only_during_install()
    {
        using var f = new QuotesFactory();
        var fresh = (await (await f.As("alice").PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await Step(f, fresh.Id, "scheduled", QuotesFactory.Now.AddDays(3))).StatusCode);
        var p = await Installing(f);
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(f, p.Id, "scheduled", QuotesFactory.Now.AddDays(3), "alice")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(f, p.Id, "scheduled", QuotesFactory.Now.AddDays(3), "bob")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Step(f, p.Id, "scheduled", QuotesFactory.Now.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Step(f, p.Id, "painting")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Step(f, p.Id, "fabricating")).StatusCode);   // schedule first
    }
}


/// <summary>6d: the payment state machine with the simulated provider (decline, retry, audit trail).</summary>
public sealed class PaymentStateTests
{
    private static readonly object Answers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    internal static async Task<ProjectDto> Accepted(QuotesFactory f)
    {
        var p = (await (await f.As("alice").PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As("alice").PutAsJsonAsync($"/api/projects/{p.Id}/answers", Answers);
        await f.As("alice").PostAsync($"/api/projects/{p.Id}/quote", null);
        return (await (await f.As("alice").PostAsync($"/api/projects/{p.Id}/accept", null)).Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    [Fact]
    public async Task A_declined_payment_fails_and_starting_again_is_a_new_attempt_every_move_recorded()
    {
        using var f = new QuotesFactory();
        var p = await Accepted(f);
        var first = (await (await f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        Assert.Equal("processing", first.Status);

        var declined = (await (await f.As("alice").PostAsJsonAsync($"/api/deposits/{first.Id}/pay", new { outcome = "decline" })).Content.ReadFromJsonAsync<DepositDto>())!;
        Assert.Equal("failed", declined.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await f.As("alice").PostAsync($"/api/deposits/{first.Id}/pay", null)).StatusCode);   // final

        var second = (await (await f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("succeeded", (await (await f.As("alice").PostAsync($"/api/deposits/{second.Id}/pay", null)).Content.ReadFromJsonAsync<DepositDto>())!.Status);

        var trail = (await f.As("alice").GetFromJsonAsync<PaymentEventDto[]>($"/api/deposits/{first.Id}/events"))!;
        Assert.Equal(new[] { "created", "processing", "failed" }, trail.Select(e => e.To));
        Assert.Equal("simulated", trail[^1].Source);
        var ok = (await f.As("alice").GetFromJsonAsync<PaymentEventDto[]>($"/api/deposits/{second.Id}/events"))!;
        Assert.Equal(new[] { "created", "processing", "succeeded" }, ok.Select(e => e.To));
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("abe").GetAsync($"/api/deposits/{first.Id}/events")).StatusCode);
    }
}

/// <summary>Stands in for Stripe's API: records requests, answers as told (session, refusal, timeout, lookup status).</summary>
public sealed class FakeStripe : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string Body, string? IdempotencyKey)> Requests { get; } = [];
    public string StartMode { get; set; } = "ok";            // ok | refuse | timeout
    public string LookupStatus { get; set; } = "open";       // open | paid | expired
    public long? LookupAmount { get; set; }
    private int _sessions;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body, request.Headers.TryGetValues("Idempotency-Key", out var k) ? k.First() : null));
        if (request.Method == HttpMethod.Post)
        {
            if (StartMode == "timeout") throw new TaskCanceledException("timed out");
            if (StartMode == "refuse") return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"no\"}}") };
            var id = $"cs_test_{++_sessions}";
            return Json(new { id, url = $"https://checkout.stripe.test/{id}", expires_at = QuotesFactory.Now.AddHours(24).ToUnixTimeSeconds() });
        }
        var (status, payment) = LookupStatus switch { "paid" => ("complete", "paid"), "expired" => ("expired", "unpaid"), _ => ("open", "unpaid") };
        return Json(new { id = request.RequestUri.Segments[^1], status, payment_status = payment, amount_total = LookupAmount, currency = "usd", payment_intent = "pi_lookup" });
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
}

/// <summary>6d: Stripe Checkout + signed webhook + reconciliation, against a fake Stripe.</summary>
public sealed class StripePaymentTests
{
    private const string Secret = "whsec_test_secret";

    private static (QuotesFactory F, FakeStripe Stripe) Stripe()
    {
        var f = new QuotesFactory();
        var fake = new FakeStripe();
        f.Configure("Payments:Provider", "stripe");
        f.Configure("Payments:Stripe:SecretKey", "sk_test_123");
        f.Configure("Payments:Stripe:WebhookSecret", Secret);
        f.Configure("Payments:ReturnBaseUrl", "http://app.test");
        f.Configure("Payments:ReconcileSeconds", "3600");
        f.StripeHandler = fake;
        return (f, fake);
    }

    private static async Task<HttpResponseMessage> Webhook(QuotesFactory f, object evt, string? secret = Secret, long? at = null)
    {
        var body = JsonSerializer.Serialize(evt);
        var t = at ?? QuotesFactory.Now.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/stripe/webhook") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (secret is not null) request.Headers.Add("Stripe-Signature", $"t={t},v1={StripeSignature.Sign(t, body, secret)}");
        return await f.CreateClient().SendAsync(request);
    }

    private static object Completed(DepositDto d, long amount, string type = "checkout.session.completed", string paymentStatus = "paid") => new
    {
        id = "evt_1", type,
        data = new { @object = new { id = "cs_test_1", payment_status = paymentStatus, amount_total = amount, currency = "usd", payment_intent = "pi_1",
            metadata = new Dictionary<string, string> { ["deposit_id"] = d.Id.ToString(), ["tenant_id"] = "t-acme" } } },
    };

    private static async Task<DepositDto> Start(QuotesFactory f, ProjectDto p, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var r = await f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null);
        Assert.Equal(expected, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<DepositDto>())!;
    }

    [Fact]
    public async Task Checkout_opens_at_Stripe_and_only_a_signed_webhook_marks_it_paid_once()
    {
        var (f, stripe) = Stripe();
        using var _ = f;
        var p = await PaymentStateTests.Accepted(f);
        var d = await Start(f, p);
        Assert.Equal(("processing", "stripe", "https://checkout.stripe.test/cs_test_1"), (d.Status, d.Provider, d.CheckoutUrl));
        var sent = Assert.Single(stripe.Requests);
        Assert.Contains($"line_items%5B0%5D%5Bprice_data%5D%5Bunit_amount%5D={StripeForm.MinorUnits(d.Amount)}", sent.Body);
        Assert.Contains("success_url=http%3A%2F%2Fapp.test%2Fpay%2F", sent.Body);
        Assert.Equal($"deposit-{d.Id:N}-0", sent.IdempotencyKey);
        Assert.Equal(d.Id, (await Start(f, p, HttpStatusCode.OK)).Id);   // same open checkout, no second session
        Assert.Single(stripe.Requests);

        // The browser can't pay a Stripe deposit; unsigned, wrongly signed or stale webhooks change nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("alice").PostAsync($"/api/deposits/{d.Id}/pay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(f, Completed(d, StripeForm.MinorUnits(d.Amount)), secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(f, Completed(d, StripeForm.MinorUnits(d.Amount)), secret: "whsec_wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(f, Completed(d, StripeForm.MinorUnits(d.Amount)), at: QuotesFactory.Now.AddHours(-1).ToUnixTimeSeconds())).StatusCode);
        // A different amount is never marked paid.
        Assert.Equal(HttpStatusCode.OK, (await Webhook(f, Completed(d, 100))).StatusCode);
        Assert.Equal("processing", (await f.As("alice").GetFromJsonAsync<DepositDto>($"/api/deposits/{d.Id}"))!.Status);

        Assert.Equal(HttpStatusCode.OK, (await Webhook(f, Completed(d, StripeForm.MinorUnits(d.Amount)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(f, Completed(d, StripeForm.MinorUnits(d.Amount)))).StatusCode);   // replay
        var after = (await f.As("alice").GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!;
        Assert.Equal(("measure", "succeeded", true), (after.Stage, after.Deposit!.Status, after.Quote!.Held));
        var trail = (await f.As("alice").GetFromJsonAsync<PaymentEventDto[]>($"/api/deposits/{d.Id}/events"))!;
        Assert.Equal(new[] { "created", "processing", "succeeded" }, trail.Select(e => e.To));
        Assert.Equal(("webhook", "pi_1"), (trail[^1].Source, trail[^1].Detail));
    }

    [Fact]
    public async Task A_timeout_is_unknown_and_reconciliation_finds_the_payment()
    {
        var (f, stripe) = Stripe();
        using var _ = f;
        var p = await PaymentStateTests.Accepted(f);
        stripe.StartMode = "timeout";
        var d = await Start(f, p, HttpStatusCode.Accepted);
        Assert.Equal("unknown", d.Status);
        Assert.Equal(d.Id, (await Start(f, p, HttpStatusCode.OK)).Id);   // still unknown after asking again: no second deposit

        // The provider did create the session: the retry with the SAME idempotency key finds it, then the lookup says paid.
        stripe.StartMode = "ok";
        stripe.LookupStatus = "paid";
        stripe.LookupAmount = StripeForm.MinorUnits(d.Amount);
        var reconciler = f.Services.GetRequiredService<PaymentReconciler>();
        await reconciler.RunOnceAsync(default);   // unknown -> processing (session found)
        Assert.Equal("processing", (await f.As("alice").GetFromJsonAsync<DepositDto>($"/api/deposits/{d.Id}"))!.Status);
        var refreshed = (await f.As("alice").GetFromJsonAsync<DepositDto>($"/api/deposits/{d.Id}?refresh=true"))!;   // back from Stripe: ask now
        Assert.Equal("succeeded", refreshed.Status);
        Assert.All(stripe.Requests.Where(r => r.Method == HttpMethod.Post), r => Assert.Equal($"deposit-{d.Id:N}-0", r.IdempotencyKey));
        var trail = (await f.As("alice").GetFromJsonAsync<PaymentEventDto[]>($"/api/deposits/{d.Id}/events"))!;
        Assert.Equal(new[] { "created", "processing", "unknown", "processing", "succeeded" }, trail.Select(e => e.To));
        Assert.Equal("lookup", trail[^1].Source);
    }

    [Fact]
    public async Task A_refusal_fails_and_an_expired_checkout_fails_on_reconciliation()
    {
        var (f, stripe) = Stripe();
        using var _ = f;
        var p = await PaymentStateTests.Accepted(f);
        stripe.StartMode = "refuse";
        var r = await f.As("alice").PostAsync($"/api/projects/{p.Id}/deposit", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("payment_refused", await r.Content.ReadAsStringAsync());

        stripe.StartMode = "ok";
        var d = await Start(f, p);   // a new attempt
        stripe.LookupStatus = "expired";
        f.Clock.Advance(TimeSpan.FromHours(25));   // past the checkout's expiry: the reconciler asks
        Assert.Equal(1, await f.Services.GetRequiredService<PaymentReconciler>().RunOnceAsync(default));
        Assert.Equal("failed", (await f.As("alice").GetFromJsonAsync<DepositDto>($"/api/deposits/{d.Id}"))!.Status);
    }
}

/// <summary>6d: the pure pieces: state-machine rules, signatures, amounts, Stripe outcomes.</summary>
public sealed class PaymentRulesTests
{
    [Theory]
    [InlineData("created", "processing", true)]
    [InlineData("processing", "succeeded", true)]
    [InlineData("processing", "unknown", true)]
    [InlineData("unknown", "succeeded", true)]
    [InlineData("unknown", "failed", true)]
    [InlineData("created", "succeeded", false)]   // only the provider's answer pays, after a checkout
    [InlineData("succeeded", "failed", false)]    // final
    [InlineData("failed", "processing", false)]   // final: a new attempt is a new deposit
    public void The_state_machine_allows_only_the_drawn_moves(string from, string to, bool allowed) =>
        Assert.Equal(allowed, BuilderPro.Quotes.Domain.PaymentStates.CanMove(from, to));

    [Fact]
    public void Signatures_must_match_the_body_secret_and_be_recent()
    {
        var now = QuotesFactory.Now;
        var t = now.ToUnixTimeSeconds();
        const string body = "{\"id\":\"evt_1\"}";
        var good = $"t={t},v1={StripeSignature.Sign(t, body, "whsec_a")}";
        Assert.True(StripeSignature.IsValid(good, body, "whsec_a", now));
        Assert.True(StripeSignature.IsValid($"t={t},v1=deadbeef,v1={StripeSignature.Sign(t, body, "whsec_a")}", body, "whsec_a", now));   // rolled secrets
        Assert.False(StripeSignature.IsValid(good, body + " ", "whsec_a", now));
        Assert.False(StripeSignature.IsValid(good, body, "whsec_b", now));
        Assert.False(StripeSignature.IsValid(good, body, "whsec_a", now.AddMinutes(6)));
        Assert.False(StripeSignature.IsValid("garbage", body, "whsec_a", now));
        Assert.False(StripeSignature.IsValid(null, body, "whsec_a", now));
    }

    [Fact]
    public void Amounts_go_to_Stripe_in_cents_and_sessions_map_to_outcomes()
    {
        Assert.Equal(14148L, StripeForm.MinorUnits(141.48m));
        Assert.Equal(1L, StripeForm.MinorUnits(0.005m));
        Assert.Equal(ProviderOutcome.Paid, StripeOutcome.Of(new StripeSession("cs", "paid", "complete", 100, "usd", "pi", null)).Outcome);
        Assert.Equal(ProviderOutcome.Failed, StripeOutcome.Of(new StripeSession("cs", "unpaid", "expired", 100, "usd", null, null)).Outcome);
        Assert.Equal(ProviderOutcome.Open, StripeOutcome.Of(new StripeSession("cs", "unpaid", "complete", 100, "usd", null, null)).Outcome);   // bank debit pending
    }

    [Fact]
    public void Live_keys_are_refused_outside_production()
    {
        Assert.Throws<InvalidOperationException>(() => new StripeOptions { SecretKey = "sk_live_x", WebhookSecret = "whsec_x" }.Validate(production: false));
        Assert.Throws<InvalidOperationException>(() => new StripeOptions { SecretKey = "sk_test_x" }.Validate(production: false));
        new StripeOptions { SecretKey = "sk_test_x", WebhookSecret = "whsec_x" }.Validate(production: false);
    }
}


/// <summary>4b: automated scheduling calls: consent first, contact hours, never books, opt-out stops everything.</summary>
public sealed class AutomatedCallTests
{
    private static readonly object Answers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    private static async Task<ProjectDto> Paid(QuotesFactory f, string sub = "alice")
    {
        var p = (await (await f.As(sub).PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As(sub).PutAsJsonAsync($"/api/projects/{p.Id}/answers", Answers);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/quote", null);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/accept", null);
        var d = (await (await f.As(sub).PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        await f.As(sub).PostAsync($"/api/deposits/{d.Id}/pay", null);
        return p;
    }

    private static Task<HttpResponseMessage> OptIn(QuotesFactory f, Guid id, string sub = "alice", bool consent = true, string version = CallEndpoints.DefaultWordingVersion) =>
        f.As(sub).PutAsJsonAsync($"/api/projects/{id}/automated-calls", new { consent, wordingVersion = version });

    private static Task<HttpResponseMessage> Event(QuotesFactory f, Guid callId, object evt, string? secret = "calls-test-secret")
    {
        var body = JsonSerializer.Serialize(evt);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/calls/{callId}/events") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        var t = f.Clock.GetUtcNow().ToUnixTimeSeconds();
        if (secret is not null) request.Headers.Add("X-BuilderPro-Signature", $"t={t},v1={StripeSignature.Sign(t, body, secret)}");
        return f.CreateClient().SendAsync(request);
    }

    private static async Task<AutomatedCallsDto> State(QuotesFactory f, Guid id, string sub = "alice") =>
        (await f.As(sub).GetFromJsonAsync<AutomatedCallsDto>($"/api/projects/{id}/automated-calls"))!;

    [Fact]
    public async Task After_consent_the_call_waits_for_contact_hours_then_offers_times_a_person_and_a_way_to_stop()
    {
        using var f = new QuotesFactory();
        var p = await Paid(f);
        Assert.Equal(HttpStatusCode.OK, (await OptIn(f, p.Id)).StatusCode);
        var dispatcher = f.Services.GetRequiredService<CallDispatcher>();
        var phone = (SimulatedTelephony)f.Services.GetRequiredService<ITelephony>();

        Assert.Equal(0, await dispatcher.RunOnceAsync(default));   // Fri 3:00 Central: outside business hours
        var waiting = Assert.Single((await State(f, p.Id)).Calls);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero), waiting.DueAt);   // Fri 9:00 Central

        f.Clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(1, await dispatcher.RunOnceAsync(default));
        var placed = Assert.Single(phone.Placed);
        Assert.Equal("+1 555 010 0001", placed.Phone);
        Assert.Contains("automated assistant", placed.Script);
        Assert.Contains("Press 0 to talk to a person", placed.Script);
        Assert.Contains("Press 9 to stop", placed.Script);
        Assert.Contains("nothing is booked until you do", placed.Script);
        var call = Assert.Single((await State(f, p.Id)).Calls);
        Assert.Equal(("calling", 3), (call.Status, call.Proposals.Count));
        Assert.Equal(3, call.Proposals.Select(x => x.UtcDateTime.Date).Distinct().Count());   // different days

        // Unsigned callbacks change nothing; pressing 2 sends a confirm link and books nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await Event(f, call.Id, new { @event = "digits", digits = "2" }, secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Event(f, call.Id, new { @event = "digits", digits = "2" })).StatusCode);
        var chosen = Assert.Single((await State(f, p.Id)).Calls);
        Assert.Equal(("slot_chosen", call.Proposals[1]), (chosen.Status, chosen.ChosenStart));
        var sent = (await f.As("alice").GetFromJsonAsync<NotificationDto[]>($"/api/projects/{p.Id}/notifications"))!;
        Assert.Equal(new[] { "email", "sms" }, sent.Select(n => n.Channel));   // alice allows texts
        Assert.Contains($"/projects/{p.Id}?visit=", sent[0].Message);
        Assert.Null((await f.As("alice").GetFromJsonAsync<ProjectDto>($"/api/projects/{p.Id}"))!.Visit);   // nothing booked
    }

    [Fact]
    public async Task Pressing_9_or_turning_it_off_stops_every_call_and_is_recorded()
    {
        using var f = new QuotesFactory();
        var a = await Paid(f);
        await OptIn(f, a.Id);
        f.Clock.Advance(TimeSpan.FromHours(6));
        await f.Services.GetRequiredService<CallDispatcher>().RunOnceAsync(default);
        var call = Assert.Single((await State(f, a.Id)).Calls);

        await Event(f, call.Id, new { @event = "digits", digits = "9" });
        var after = await State(f, a.Id);
        Assert.Equal(("opted_out", false), (Assert.Single(after.Calls).Status, after.Consented));
        Assert.Equal(("call", false), (f.Calls.Consents[^1].Source, f.Calls.Consents[^1].Granted));
        Assert.Equal(0, await f.Services.GetRequiredService<CallDispatcher>().RunOnceAsync(default));

        // Turning it back on needs the current wording; turning it off cancels at once.
        Assert.Equal(HttpStatusCode.Conflict, (await OptIn(f, a.Id, version: "old")).StatusCode);
        await OptIn(f, a.Id);
        Assert.Contains((await State(f, a.Id)).Calls, c => c.Status == "scheduled");
        await OptIn(f, a.Id, consent: false);
        Assert.DoesNotContain((await State(f, a.Id)).Calls, c => c.Status is "scheduled" or "calling");
    }

    [Fact]
    public async Task Only_the_customer_opts_in_with_a_phone_in_a_store_that_offers_it_and_a_booked_visit_cancels_the_call()
    {
        using var f = new QuotesFactory();
        var p = await Paid(f);
        var agent = f.As("alice");
        agent.DefaultRequestHeaders.Add(AgentCall.Header, "concierge");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync($"/api/projects/{p.Id}/automated-calls", new { consent = true, wordingVersion = CallEndpoints.DefaultWordingVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await OptIn(f, p.Id, "mia")).StatusCode);   // staff

        var abe = await Paid(f, "abe");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await OptIn(f, abe.Id, "abe")).StatusCode);   // no phone on file

        var gina = await Paid(f, "gina");
        var r = await OptIn(f, gina.Id, "gina");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("feature_not_included", await r.Content.ReadAsStringAsync());
        Assert.False((await State(f, gina.Id, "gina")).Available);

        await OptIn(f, p.Id);
        await f.As("alice").PostAsJsonAsync($"/api/projects/{p.Id}/visit", new { start = new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero) });
        f.Clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(0, await f.Services.GetRequiredService<CallDispatcher>().RunOnceAsync(default));
        Assert.Equal("cancelled", Assert.Single((await State(f, p.Id)).Calls).Status);
    }
}


/// <summary>3e: the store desk's work queue.</summary>
public sealed class StaffQueueTests
{
    private static readonly object Answers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    [Fact]
    public async Task Staff_see_visits_to_measure_installs_to_move_on_and_calls_that_need_a_person()
    {
        using var f = new QuotesFactory();
        async Task<Guid> Paid(string sub)
        {
            var p = (await (await f.As(sub).PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
            await f.As(sub).PutAsJsonAsync($"/api/projects/{p.Id}/answers", Answers);
            await f.As(sub).PostAsync($"/api/projects/{p.Id}/quote", null);
            await f.As(sub).PostAsync($"/api/projects/{p.Id}/accept", null);
            var d = (await (await f.As(sub).PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
            await f.As(sub).PostAsync($"/api/deposits/{d.Id}/pay", null);
            return p.Id;
        }
        var toMeasure = await Paid("alice");
        await f.As("alice").PostAsJsonAsync($"/api/projects/{toMeasure}/visit", new { start = new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero) });
        var toInstall = await Paid("abe");
        await f.As("abe").PostAsJsonAsync($"/api/projects/{toInstall}/visit", new { start = new DateTimeOffset(2026, 11, 14, 19, 0, 0, TimeSpan.Zero) });
        await f.As("mia").PostAsJsonAsync($"/api/projects/{toInstall}/visit/measured", new { measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } } });

        var queue = (await f.As("mia").GetFromJsonAsync<QueueItemDto[]>("/api/staff/queue"))!;
        Assert.Contains(queue, q => q.ProjectId == toMeasure && q.Kind == "measure" && q.VisitStart is not null);
        Assert.Contains(queue, q => q.ProjectId == toInstall && q.Kind == "install" && q.InstallStep == null);
        Assert.Equal(JsonValueKind.Object, queue.First(q => q.ProjectId == toMeasure).Measurements.ValueKind);   // what was quoted, to start from

        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("alice").GetAsync("/api/staff/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("bob").GetAsync("/api/staff/queue")).StatusCode);   // staff without scheduling.write
    }
}


/// <summary>Final gaps: the outbox is delivered: email any time, texts only inside contact hours.</summary>
public sealed class NotificationSenderTests
{
    private static readonly object Answers = new
    {
        sku = "CT-QTZ",
        measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } },
        choices = new object[] { new { group = "edge", sku = "EDGE-EASED" }, new { group = "plan", sku = "PLAN-10Y" } },
    };

    private static async Task<Guid> Installing(QuotesFactory f, string sub)
    {
        var p = (await (await f.As(sub).PostAsJsonAsync("/api/projects", new { zip = "62701" })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await f.As(sub).PutAsJsonAsync($"/api/projects/{p.Id}/answers", Answers);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/quote", null);
        await f.As(sub).PostAsync($"/api/projects/{p.Id}/accept", null);
        var d = (await (await f.As(sub).PostAsync($"/api/projects/{p.Id}/deposit", null)).Content.ReadFromJsonAsync<DepositDto>())!;
        await f.As(sub).PostAsync($"/api/deposits/{d.Id}/pay", null);
        await f.As(sub).PostAsJsonAsync($"/api/projects/{p.Id}/visit", new { start = new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero) });
        await f.As("mia").PostAsJsonAsync($"/api/projects/{p.Id}/visit/measured", new { measurements = new { countertop = new { countertop = new[] { new { widthIn = 120m, depthIn = 25m } } } } });
        return p.Id;
    }

    [Fact]
    public async Task Email_goes_at_once_a_text_waits_for_contact_hours_and_missing_addresses_are_recorded()
    {
        using var f = new QuotesFactory();
        var alice = await Installing(f, "alice");
        var abe = await Installing(f, "abe");
        await f.As("mia").PostAsJsonAsync($"/api/projects/{alice}/install", new { step = "scheduled", scheduledFor = QuotesFactory.Now.AddDays(10) });   // email + sms
        await f.As("mia").PostAsJsonAsync($"/api/projects/{abe}/install", new { step = "scheduled", scheduledFor = QuotesFactory.Now.AddDays(10) });     // email only

        var sender = f.Services.GetRequiredService<NotificationSender>();
        var sms = (SimulatedSmsSender)f.Services.GetRequiredService<ISmsSender>();
        Assert.Equal(1, await sender.RunOnceAsync(default));   // Fri 3:00 Central: alice's email only
        var mail = Assert.Single(f.Email.Sent);
        Assert.Equal("alice@acme.test", mail.To);
        Assert.Contains("installation scheduled for", mail.Body);
        Assert.Empty(sms.Sent);
        Assert.Contains(f.Contacts.Marked.Values, m => m.Result.StartsWith("skipped: no address"));   // abe has no email on file

        f.Clock.Advance(TimeSpan.FromHours(6));   // Fri 9:00 Central: inside business hours
        Assert.Equal(1, await sender.RunOnceAsync(default));
        Assert.Equal("+1 555 010 0001", Assert.Single(sms.Sent).To);
        Assert.Equal(0, await sender.RunOnceAsync(default));   // nothing is sent twice
    }
}
