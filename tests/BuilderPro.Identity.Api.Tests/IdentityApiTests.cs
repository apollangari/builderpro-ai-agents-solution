using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BuilderPro.Identity.Api.Domain;
using BuilderPro.Identity.Api.Infrastructure;
using BuilderPro.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BuilderPro.Identity.Api.Tests;

/// <summary>X-Test-Sub (+ optional X-Test-Tenant, X-Test-Roles) = a signed-in caller from Issuer.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    public const string SchemeName = "Test";
    public const string Issuer = "http://idp.test/realms/builderpro";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("iss", Issuer), new("sub", sub.ToString()), new("preferred_username", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var t)) claims.Add(new Claim("tenant_id", t.ToString()));
        claims.AddRange(Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("roles", r)));
        var identity = new ClaimsIdentity(claims, SchemeName, "preferred_username", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class IdentityFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    public InMemoryMembershipStore Store { get; } = new(new FakeTimeProvider(Now));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Authority", TestAuthHandler.Issuer);
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.UseSetting("ConnectionStrings:identitydb", "Host=unused.test;Database=identitydb");
        builder.UseSetting("Identity:SeedDevMemberships", "false");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IMembershipStore>();
            s.AddSingleton<IMembershipStore>(Store);
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
            s.AddAuthentication(a => { a.DefaultAuthenticateScheme = TestAuthHandler.SchemeName; a.DefaultChallengeScheme = TestAuthHandler.SchemeName; })
             .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public static string KeyFor(string sub) => ExternalIdentityKey.ForIssuerAndSubject(TestAuthHandler.Issuer, sub);

    public HttpClient As(string sub, string? tenant = null, string? roles = null)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        if (tenant is not null) c.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        if (roles is not null) c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return c;
    }
}

public sealed class IdentityApiTests(IdentityFactory f) : IClassFixture<IdentityFactory>, IAsyncLifetime
{
    private readonly IdentityFactory _f = f;

    // Fixed fixtures before each test (UpsertAsync is idempotent, so re-running is safe).
    public async Task InitializeAsync()
    {
        await _f.Store.UpsertAsync("t-acme", IdentityFactory.KeyFor("alice"), "c-alice", ["quotes.read"], null, default);
        await _f.Store.UpsertAsync("t-acme", IdentityFactory.KeyFor("dana"), "c-dana-a", [], null, default);
        await _f.Store.UpsertAsync("t-globex", IdentityFactory.KeyFor("dana"), "c-dana-g", [], null, default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<Dictionary<string, object?>>())?["code"]?.ToString();

    [Fact]
    public async Task Member_gets_tenant_from_the_membership()
    {
        var me = await _f.As("alice").GetFromJsonAsync<IdentityMeResponse>("/api/identity/me");

        Assert.Equal("t-acme", me!.TenantId);
        Assert.Equal("c-alice", me.CustomerId);
        Assert.Equal(new[] { "quotes.read" }, me.Scopes);
        Assert.Equal("membership", me.Source);
    }

    [Fact]
    public async Task Signed_in_but_no_membership_is_403_no_membership()
    {
        var r = await _f.As("erin").GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("no_membership", await CodeOf(r));
    }

    [Fact]
    public async Task Two_tenants_and_no_hint_is_409()
    {
        var r = await _f.As("dana").GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("multiple_active_memberships", await CodeOf(r));
    }

    [Fact]
    public async Task A_token_tenant_without_a_membership_is_refused()
    {
        var r = await _f.As("alice", tenant: "t-globex").GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("tenant_not_permitted", await CodeOf(r));
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/identity/me")).StatusCode);
    }

    [Fact]
    public async Task Only_platform_admins_can_manage_memberships()
    {
        var body = new UpsertMembershipRequest("t-acme", IdentityFactory.KeyFor("erin"), null, ["quotes.read"], null);

        var asCustomer = await _f.As("alice", roles: "customer").PostAsJsonAsync("/api/identity/memberships", body);
        var asAdmin = await _f.As("pat", roles: "platform-admin").PostAsJsonAsync("/api/identity/memberships", body);

        Assert.Equal(HttpStatusCode.Forbidden, asCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
    }

    [Fact]
    public async Task Unknown_scopes_are_rejected()
    {
        var body = new UpsertMembershipRequest("t-acme", IdentityFactory.KeyFor("frank"), null, ["god.mode"], null);
        var r = await _f.As("pat", roles: "platform-admin").PostAsJsonAsync("/api/identity/memberships", body);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("unknown_scope", await CodeOf(r));
    }

    [Fact]
    public async Task Revoking_takes_effect_immediately_and_keeps_the_record()
    {
        var admin = _f.As("pat", roles: "platform-admin");
        var created = await (await admin.PostAsJsonAsync("/api/identity/memberships",
            new UpsertMembershipRequest("t-acme", IdentityFactory.KeyFor("gina"), null, [], null))).Content.ReadFromJsonAsync<MembershipDto>();
        Assert.Equal(HttpStatusCode.OK, (await _f.As("gina").GetAsync("/api/identity/me")).StatusCode);

        var revoked = await (await admin.PostAsJsonAsync($"/api/identity/memberships/{created!.Id}/revoke", new RevokeRequest("left the company")))
            .Content.ReadFromJsonAsync<MembershipDto>();

        Assert.False(revoked!.Active);
        Assert.Equal("left the company", revoked.RevokedReason);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.As("gina").GetAsync("/api/identity/me")).StatusCode);
        var listed = await admin.GetFromJsonAsync<MembershipDto[]>("/api/identity/memberships?tenantId=t-acme");
        Assert.Contains(listed!, m => m.Id == created.Id && !m.Active);   // still there: audit, not delete
    }
}
