using System.Net;
using System.Text;
using BuilderPro.Catalog.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuilderPro.Catalog.Api.Tests;

/// <summary>Unit tests of the Identity call itself, with a fake HTTP handler (no network).</summary>
public sealed class IdentityTenantLookupTests
{
    private sealed class StubIdentity(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (IdentityTenantLookup Lookup, StubIdentity Stub) Create(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var stub = new StubIdentity(answer);
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://identity.test") };
        return (new IdentityTenantLookup(http, NullLogger<IdentityTenantLookup>.Instance), stub);
    }

    private static DefaultHttpContext WithBearer(string? token)
    {
        var ctx = new DefaultHttpContext();
        if (token is not null) ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx;
    }

    [Fact]
    public async Task Forwards_the_callers_own_token_and_returns_the_tenant()
    {
        var (lookup, stub) = Create(_ => Json(HttpStatusCode.OK, """{"tenantId":"t-acme","userKey":"k"}"""));

        var result = await lookup.ResolveAsync(WithBearer("abc.def.ghi"), default);

        Assert.True(result.Resolved);
        Assert.Equal("t-acme", result.TenantId);
        Assert.Equal("/api/identity/me", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", stub.Last.Headers.Authorization!.Scheme);
        Assert.Equal("abc.def.ghi", stub.Last.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "no_membership")]
    [InlineData(HttpStatusCode.Forbidden, "tenant_not_permitted")]
    [InlineData(HttpStatusCode.Conflict, "multiple_active_memberships")]
    public async Task Identity_refusals_keep_their_status_and_code(HttpStatusCode status, string code)
    {
        var (lookup, _) = Create(_ => Json(status, $$"""{"title":"{{code}}","code":"{{code}}"}"""));

        var result = await lookup.ResolveAsync(WithBearer("t"), default);

        Assert.False(result.Resolved);
        Assert.Equal((int)status, result.Status);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public async Task No_bearer_token_is_401_without_calling_identity()
    {
        var (lookup, stub) = Create(_ => throw new InvalidOperationException("must not be called"));

        var result = await lookup.ResolveAsync(WithBearer(null), default);

        Assert.Equal(StatusCodes.Status401Unauthorized, result.Status);
        Assert.Null(stub.Last);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, """{"tenantId":null}""")]
    [InlineData(HttpStatusCode.OK, """{"tenantId":"not a valid id!"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Anything_unexpected_fails_closed_as_503(HttpStatusCode status, string body)
    {
        var (lookup, _) = Create(_ => Json(status, body));

        var result = await lookup.ResolveAsync(WithBearer("t"), default);

        Assert.False(result.Resolved);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Status);
        Assert.Equal("tenant_lookup_unavailable", result.Code);
    }

    [Fact]
    public async Task Identity_unreachable_fails_closed_as_503()
    {
        var (lookup, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var result = await lookup.ResolveAsync(WithBearer("t"), default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Status);
    }
}
