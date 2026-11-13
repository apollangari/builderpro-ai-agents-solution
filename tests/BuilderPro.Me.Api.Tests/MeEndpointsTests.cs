using System.Net;
using System.Net.Http.Json;
using BuilderPro.Me.Api;

namespace BuilderPro.Me.Api.Tests;

public sealed class MeEndpointsTests(MeApiFactory factory) : IClassFixture<MeApiFactory>
{
    private HttpClient SignedIn(string user, string? tenant = null, string? roles = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", user);
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        if (roles is not null) client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        return client;
    }

    [Fact]
    public async Task Anonymous_caller_is_rejected_with_401()
    {
        var response = await factory.CreateClient().GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signed_in_caller_sees_user_tenant_and_roles()
    {
        var me = await SignedIn("alice", "t-acme", "customer").GetFromJsonAsync<MeResponse>("/api/me");

        Assert.NotNull(me);
        Assert.True(me.Authenticated);
        Assert.Equal("sub-alice", me.UserId);
        Assert.Equal("alice", me.UserName);
        Assert.Equal("t-acme", me.TenantId);
        Assert.Equal(new[] { "customer" }, me.Roles);
        Assert.Equal(MeEndpoints.ServiceName, me.Service);
        Assert.Equal(MeApiFactory.Noon, me.ServerTimeUtc);
    }

    [Fact]
    public async Task Tenants_are_reported_from_the_token_not_from_the_request()
    {
        // Two users, two tenants: each sees only their own tenant id.
        var acme = await SignedIn("alice", "t-acme").GetFromJsonAsync<MeResponse>("/api/me");
        var globex = await SignedIn("carol", "t-globex").GetFromJsonAsync<MeResponse>("/api/me");

        Assert.Equal("t-acme", acme!.TenantId);
        Assert.Equal("t-globex", globex!.TenantId);
    }

    [Fact]
    public async Task Auth_config_is_public_and_contains_no_secrets()
    {
        var cfg = await factory.CreateClient().GetFromJsonAsync<AuthConfigResponse>("/api/auth-config");

        Assert.NotNull(cfg);
        Assert.Equal("http://idp.test/realms/builderpro", cfg.Authority);
        Assert.Equal("builderpro-web", cfg.ClientId);
        Assert.Contains("openid", cfg.Scope);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_endpoints_are_anonymous_and_up(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public sealed class AuthSetupTests
{
    [Fact]
    public void Missing_authority_fails_fast_with_a_helpful_message()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuilderPro.Security.AuthSetup.AddBuilderProAuth(services, config));

        Assert.Contains("Auth:Authority", ex.Message);
    }
}
