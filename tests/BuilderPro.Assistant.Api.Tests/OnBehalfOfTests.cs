using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BuilderPro.Assistant.Api.Tests;

/// <summary>Stands in for the identity provider's token endpoint.</summary>
public sealed class FakeTokenEndpoint : HttpMessageHandler
{
    public List<Dictionary<string, string>> Forms { get; } = [];
    public Func<HttpResponseMessage> Answer { get; set; } = () => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { access_token = "platform-token", token_type = "Bearer", expires_in = 300 }),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        Forms.Add(body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' '))));
        return Answer();
    }
}

/// <summary>3c: on-behalf-of. The caller's token is exchanged, never forwarded; the answer is checked.</summary>
public sealed class OnBehalfOfTests
{
    private static (OAuthTokenExchange Exchange, FakeTokenEndpoint Idp, FakeTimeProvider Clock) Make(string protocol = TokenExchangeProtocols.TokenExchange)
    {
        var idp = new FakeTokenEndpoint();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        var options = new TokenExchangeOptions
        {
            Enabled = true, Protocol = protocol, TokenUrl = "https://idp.test/token", ClientId = "builderpro-assistant",
            ClientSecret = "s3cret", Audience = "builderpro-api", Scope = protocol == TokenExchangeProtocols.EntraObo ? "api://platform/.default" : null,
        };
        return (new OAuthTokenExchange(new HttpClient(idp), options, clock, NullLogger<OAuthTokenExchange>.Instance), idp, clock);
    }

    [Fact]
    public async Task RFC_8693_exchange_for_the_platform_audience_cached_until_shortly_before_expiry()
    {
        var (exchange, idp, clock) = Make();
        Assert.Equal("platform-token", await exchange.ForPlatformAsync("caller-token", default));
        var form = Assert.Single(idp.Forms);
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", form["grant_type"]);
        Assert.Equal(("caller-token", "urn:ietf:params:oauth:token-type:access_token", "builderpro-api"), (form["subject_token"], form["subject_token_type"], form["audience"]));

        await exchange.ForPlatformAsync("caller-token", default);
        Assert.Single(idp.Forms);   // cached
        clock.Advance(TimeSpan.FromSeconds(280));   // inside the 30 s safety margin
        await exchange.ForPlatformAsync("caller-token", default);
        Assert.Equal(2, idp.Forms.Count);
        await exchange.ForPlatformAsync("another-callers-token", default);
        Assert.Equal(3, idp.Forms.Count);   // one exchange per caller token, never shared
    }

    [Fact]
    public async Task Entra_on_behalf_of_uses_the_jwt_bearer_grant_and_scope()
    {
        var (exchange, idp, _) = Make(TokenExchangeProtocols.EntraObo);
        await exchange.ForPlatformAsync("caller-token", default);
        var form = Assert.Single(idp.Forms);
        Assert.Equal(("urn:ietf:params:oauth:grant-type:jwt-bearer", "on_behalf_of", "caller-token", "api://platform/.default"),
            (form["grant_type"], form["requested_token_use"], form["assertion"], form["scope"]));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}")]
    [InlineData(HttpStatusCode.OK, "{\"access_token\":\"t\",\"token_type\":\"mac\",\"expires_in\":300}")]
    [InlineData(HttpStatusCode.OK, "{\"access_token\":\"t\",\"token_type\":\"Bearer\",\"expires_in\":0}")]
    [InlineData(HttpStatusCode.OK, "{\"access_token\":\"has space\",\"token_type\":\"Bearer\",\"expires_in\":300}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Refusals_and_unusable_answers_give_no_token(HttpStatusCode status, string body)
    {
        var (exchange, idp, _) = Make();
        idp.Answer = () => new HttpResponseMessage(status) { Content = new StringContent(body) };
        Assert.Null(await exchange.ForPlatformAsync("caller-token", default));
    }

    [Fact]
    public void Settings_are_checked_at_startup()
    {
        TokenExchangeOptions Ok() => new() { Enabled = true, TokenUrl = "https://idp.test/token", ClientSecret = "x" };
        Ok().Validate(allowHttpForLocalDev: false);
        Assert.Throws<InvalidOperationException>(() => new TokenExchangeOptions { Enabled = true, TokenUrl = "http://idp.example.com/token", ClientSecret = "x" }.Validate(allowHttpForLocalDev: true));   // plain HTTP to a real host
        new TokenExchangeOptions { Enabled = true, TokenUrl = "http://keycloak:8080/realms/x/token", ClientSecret = "x" }.Validate(allowHttpForLocalDev: true);
        Assert.Throws<InvalidOperationException>(() => new TokenExchangeOptions { Enabled = true, TokenUrl = "http://keycloak:8080/t", ClientSecret = "x" }.Validate(allowHttpForLocalDev: false));
        Assert.Throws<InvalidOperationException>(() => new TokenExchangeOptions { Enabled = true, TokenUrl = "https://idp.test/token" }.Validate(false));            // no secret
        Assert.Throws<InvalidOperationException>(() => new TokenExchangeOptions { Enabled = true, TokenUrl = "https://idp.test/token", ClientSecret = "x", Protocol = "magic" }.Validate(false));
        Assert.Throws<InvalidOperationException>(() => new TokenExchangeOptions { Enabled = true, TokenUrl = "https://idp.test/token", ClientSecret = "x", Protocol = "entra-obo" }.Validate(false));   // no scope
    }

    [Fact]
    public async Task Tool_calls_carry_the_exchanged_token_never_the_callers()
    {
        var idp = new FakeTokenEndpoint();
        var seen = new List<string?>();
        var downstream = new CaptureHandler(seen);
        var factory = new SingleClientFactory(new HttpClient(downstream) { BaseAddress = new Uri("http://platform.test") });
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer caller-token";
        var options = new TokenExchangeOptions { Enabled = true, TokenUrl = "https://idp.test/token", ClientSecret = "x" };
        var exchange = new OAuthTokenExchange(new HttpClient(idp), options, TimeProvider.System, NullLogger<OAuthTokenExchange>.Instance);
        var api = new HttpPlatformApi(factory, new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http }, exchange);

        await api.GetAsync("/api/catalog/categories", default);
        Assert.Equal(new[] { "Bearer platform-token" }, seen);

        idp.Answer = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var refused = await new HttpPlatformApi(factory, new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http },
            new OAuthTokenExchange(new HttpClient(idp), options, TimeProvider.System, NullLogger<OAuthTokenExchange>.Instance)).GetAsync("/api/projects", default);
        Assert.Equal(401, refused.GetProperty("error").GetInt32());
        Assert.Single(seen);   // nothing went downstream with the caller's token
    }

    private sealed class CaptureHandler(List<string?> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
