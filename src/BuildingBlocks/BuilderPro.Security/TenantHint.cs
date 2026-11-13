using Microsoft.AspNetCore.Http;

namespace BuilderPro.Security;

/// <summary>
/// Which tenant the caller WANTS to act for (1f store switcher). Two sources, both only HINTS:
///   1. the X-BuilderPro-Tenant request header (the store picker in the app), else
///   2. the token's tenant claim (UserContext.TenantHint).
/// A hint never grants anything: TenantResolver accepts it only if it matches an ACTIVE membership.
/// That's why a header (which the caller fully controls) is safe to accept here.
/// </summary>
public static class TenantHint
{
    public const string Header = "X-BuilderPro-Tenant";

    public static string? From(HttpContext http, UserContext user)
    {
        var header = http.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(header)) return user.TenantHint;
        // A malformed header is treated as "no header", never as a tenant id.
        return ClaimsNormalizer.IsIdentifier(header) ? header : user.TenantHint;
    }

    /// <summary>The caller's user context with the effective hint applied.</summary>
    public static UserContext Apply(HttpContext http, UserContext user) => user with { TenantHint = From(http, user) };
}
