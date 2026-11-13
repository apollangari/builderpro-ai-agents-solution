using System.Net;
using Microsoft.AspNetCore.Http;

namespace BuilderPro.Gateway.Tests;

public sealed class GatewayRoutingTests : IDisposable
{
    private readonly GatewayFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Auth_config_is_forwarded_without_a_token()
    {
        var response = await _factory.CreateClient().GetAsync("/api/auth-config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_factory.Backend.Received);
        Assert.Equal("/api/auth-config", _factory.Backend.Received[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_stores_public_info_is_forwarded_without_a_token_but_the_rest_of_identity_is_not()
    {
        var store = await _factory.CreateClient().GetAsync("/api/identity/stores/acme");
        Assert.Equal(HttpStatusCode.OK, store.StatusCode);
        Assert.Equal("/api/identity/stores/acme", _factory.Backend.Received.Single().RequestUri!.AbsolutePath);

        var me = await _factory.CreateClient().GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        var deeper = await _factory.CreateClient().GetAsync("/api/identity/stores/acme/secrets");
        Assert.Equal(HttpStatusCode.Unauthorized, deeper.StatusCode);   // only /stores/{id} is public
        Assert.Single(_factory.Backend.Received);
    }

    [Fact]
    public async Task Protected_route_without_a_token_is_stopped_at_the_gateway()
    {
        var response = await _factory.CreateClient().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Backend.Received);   // never reached the service
    }

    [Fact]
    public async Task Protected_route_with_a_token_is_forwarded_with_the_callers_headers()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "alice");

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(_factory.Backend.Received);
        // The service gets the caller's credentials and validates them AGAIN (defence in depth).
        Assert.True(forwarded.Headers.Contains("X-Test-User"));
        Assert.True(forwarded.Headers.Contains(CorrelationId.Header));
    }

    [Fact]
    public async Task Catalog_route_requires_a_token_and_forwards_to_the_catalog_service()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/catalog/products")).StatusCode);
        Assert.Empty(_factory.Backend.Received);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "alice");
        var response = await client.GetAsync("/api/catalog/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(_factory.Backend.Received);
        Assert.Equal("catalog.test", forwarded.RequestUri!.Host);
        Assert.Equal("/api/catalog/products", forwarded.RequestUri.AbsolutePath);
    }

    [Theory]
    [InlineData("GET", "/api/quotes")]
    [InlineData("POST", "/api/quotes")]
    [InlineData("GET", "/api/quotes/0b9c3a2e-0000-4000-8000-000000000001")]
    public async Task Quotes_routes_forward_to_the_quotes_service(string method, string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "alice");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? new StringContent("{}") : null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(_factory.Backend.Received);
        Assert.Equal("quotes.test", forwarded.RequestUri!.Host);
        Assert.Equal(path, forwarded.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task Unknown_routes_are_not_forwarded()
    {
        var response = await _factory.CreateClient().GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.Backend.Received);
    }
}

public sealed class CorrelationIdTests : IDisposable
{
    private readonly GatewayFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_correlation_id_is_generated_echoed_and_forwarded()
    {
        var response = await _factory.CreateClient().GetAsync("/api/auth-config");

        var echoed = Assert.Single(response.Headers.GetValues(CorrelationId.Header));
        Assert.Matches("^[a-f0-9]{32}$", echoed);
        Assert.Equal(echoed, _factory.Backend.Received[0].Headers.GetValues(CorrelationId.Header).Single());
    }

    [Fact]
    public async Task A_valid_incoming_id_is_kept()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationId.Header, "support-case-1234");

        var response = await client.GetAsync("/api/auth-config");

        Assert.Equal("support-case-1234", response.Headers.GetValues(CorrelationId.Header).Single());
    }

    [Theory]
    [InlineData("short")]                                   // too short
    [InlineData("has spaces in it!!")]                      // illegal characters
    [InlineData("x\r\nSet-Cookie:evil=1-------")]           // header/log injection attempt
    public void Unsafe_incoming_ids_are_replaced(string incoming)
    {
        var resolved = CorrelationId.Resolve(incoming);

        Assert.NotEqual(incoming, resolved);
        Assert.Matches("^[a-f0-9]{32}$", resolved);
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public async Task Public_budget_is_enforced_with_429_and_retry_after()
    {
        using var factory = new GatewayFactory { PublicPerMinute = 2 };
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth-config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth-config")).StatusCode);
        var third = await client.GetAsync("/api/auth-config");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("60", third.Headers.GetValues("Retry-After").Single());
        Assert.Equal(2, factory.Backend.Received.Count);   // the 3rd never reached the service
    }

    [Theory]
    [InlineData("GET", "/api/auth-config", "public")]
    [InlineData("GET", "/api/me", "default")]
    [InlineData("POST", "/api/me", "writes")]
    [InlineData("DELETE", "/api/anything", "writes")]
    public void Requests_are_put_in_the_right_tier(string method, string path, string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;

        Assert.Equal(expected, RateLimiting.TierFor(request, new RateLimitOptions()));
    }
}
