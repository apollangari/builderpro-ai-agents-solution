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
using Microsoft.Extensions.Time.Testing;

namespace BuilderPro.Me.Api.Tests;

/// <summary>
/// Replaces real JWT validation in tests. A request is "signed in" when it carries
/// X-Test-User; claims come from X-Test-Tenant / X-Test-Roles. No identity provider needed.
/// Real token validation is covered by running against Keycloak (LEARN 1b labs).
/// </summary>
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

        var claims = new List<Claim> { new("sub", $"sub-{user}"), new("preferred_username", user.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant))
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }
        foreach (var role in Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            claims.Add(new Claim("roles", role));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "preferred_username", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>The API under test, with fake auth and a fixed clock.</summary>
public sealed class MeApiFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Noon = new(2026, 11, 12, 18, 0, 0, TimeSpan.Zero);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Authority", "http://idp.test/realms/builderpro");
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Noon));
            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}
