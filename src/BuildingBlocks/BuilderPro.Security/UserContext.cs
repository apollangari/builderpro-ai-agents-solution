using System.Security.Claims;
using System.Text.RegularExpressions;

namespace BuilderPro.Security;

/// <summary>
/// The caller, in OUR words, whatever identity provider issued the token.
/// Code below the edge reads UserContext, never raw claim names. That's what makes
/// "Keycloak in dev, Entra in prod, a customer's own IdP later" a configuration change.
/// </summary>
public sealed record UserContext(
    string UserKey,                 // stable cross-IdP key (ExternalIdentityKey)
    string Issuer,
    string? UserName,
    string? Email,
    IReadOnlyList<string> Roles,
    string? TenantHint)             // tenant claim from the token, if any: a HINT, verified against memberships
{
    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.Ordinal);
}

public static partial class ClaimsNormalizer
{
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public static bool IsIdentifier(string? value) => value is not null && Identifier().IsMatch(value);

    /// <summary>Builds the UserContext from an authenticated principal using the configured claim names.</summary>
    public static UserContext FromPrincipal(ClaimsPrincipal principal, AuthOptions options)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("UserContext requires an authenticated principal");
        }

        var issuer = principal.FindFirst("iss")?.Value ?? "";
        var userName = principal.FindFirst("preferred_username")?.Value
                       ?? principal.FindFirst("upn")?.Value          // Entra work accounts
                       ?? principal.FindFirst("name")?.Value;
        var email = principal.FindFirst("email")?.Value;
        var roles = principal.FindAll(options.RoleClaim).Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // Exactly one well-formed tenant claim, or no hint at all. Two tenant claims, or a
        // malformed one, is treated as "no hint", never "pick the first".
        var tenants = principal.FindAll(options.TenantClaim).Select(c => c.Value).ToArray();
        var tenantHint = tenants.Length == 1 && IsIdentifier(tenants[0]) ? tenants[0] : null;

        return new UserContext(ExternalIdentityKey.FromPrincipal(principal), issuer, userName, email, roles, tenantHint);
    }
}
