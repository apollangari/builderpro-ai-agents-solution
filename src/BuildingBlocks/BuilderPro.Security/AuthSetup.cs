using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuilderPro.Security;

public static class AuthSetup
{
    /// <summary>
    /// JWT bearer validation against any OIDC issuer. What is checked on EVERY request:
    ///   signature (keys from the issuer's JWKS, fetched + cached automatically),
    ///   issuer (iss == Authority), audience (aud contains Audience), expiry (exp/nbf, 5 min skew max).
    /// </summary>
    public static IServiceCollection AddBuilderProAuth(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();

        // Fail fast: a service that starts without an issuer would reject every call later,
        // which is much harder to diagnose than refusing to start.
        if (string.IsNullOrWhiteSpace(options.Authority))
        {
            throw new InvalidOperationException(
                "Auth:Authority is not configured. Run through the AppHost (it injects Auth__Authority), " +
                "or set it in appsettings.Development.json. See docs/learn/01b-keycloak-login.md.");
        }

        services.AddSingleton(options);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                // Keep claim names exactly as the token has them ("sub", "roles"), instead of
                // the legacy long WS-* URIs. What you see in jwt.io is what the code reads.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = "preferred_username";
                jwt.TokenValidationParameters.RoleClaimType = options.RoleClaim;
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
            });

        services.AddAuthorization();
        return services;
    }
}
