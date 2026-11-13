using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace BuilderPro.Gateway.Tests;

/// <summary>Stands in for every backend service: records what the gateway forwarded, answers 200.</summary>
public sealed class FakeBackend : HttpMessageHandler
{
    public List<HttpRequestMessage> Received { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Received.Add(request);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"backend":"fake","path":"{{request.RequestUri!.AbsolutePath}}"}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>YARP asks this factory for the HTTP client it forwards with; we hand it the fake backend.</summary>
public sealed class FakeForwarderClientFactory(FakeBackend backend) : IForwarderHttpClientFactory
{
    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(backend, disposeHandler: false);
}

/// <summary>Signed in when X-Test-User is present. Same idea as the Me API tests.</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var identity = new ClaimsIdentity([new Claim("sub", user.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public FakeBackend Backend { get; } = new();
    public int PublicPerMinute { get; init; } = 300;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Authority", "http://idp.test/realms/builderpro");
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.UseSetting("services:me-api:http:0", "http://me-api.test");
        builder.UseSetting("services:identity:http:0", "http://identity.test");
        builder.UseSetting("services:catalog:http:0", "http://catalog.test");
        builder.UseSetting("services:quotes:http:0", "http://quotes.test");
        builder.UseSetting("Gateway:RateLimits:PublicPerMinute", PublicPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IForwarderHttpClientFactory>();
            services.AddSingleton<IForwarderHttpClientFactory>(new FakeForwarderClientFactory(Backend));

            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}
