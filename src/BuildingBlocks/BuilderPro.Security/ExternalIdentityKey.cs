using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace BuilderPro.Security;

/// <summary>
/// The ONE stable key for a human across identity providers (copied from
/// builderpro-ai-platform's BuilderPro.Shared, unchanged in behaviour).
///
/// - Entra: <c>oid</c> (object id), stable per user per directory. Entra's <c>sub</c> is
///   *pairwise* (different for every app registration), so it must NOT be the key.
/// - Any other OIDC provider (Keycloak, Okta…): <c>sub</c> is only unique *per issuer*, so the
///   key is a hash of issuer + subject: <c>oidc_&lt;sha256&gt;</c>.
/// </summary>
public static class ExternalIdentityKey
{
    public static string FromPrincipal(ClaimsPrincipal principal)
    {
        var objectIds = principal.FindAll("oid").Select(c => c.Value).ToArray();
        if (objectIds.Length > 1 || (objectIds.Length == 1 && string.IsNullOrWhiteSpace(objectIds[0])))
        {
            throw new InvalidOperationException("At most one non-empty oid claim is allowed");
        }
        if (objectIds.Length == 1 && Guid.TryParse(objectIds[0], out var objectId))
        {
            return objectId.ToString("D");
        }

        return ForIssuerAndSubject(SingleClaim(principal, "iss"), SingleClaim(principal, "sub"));
    }

    public static string ForIssuerAndSubject(string issuer, string subject)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\n{subject}"));
        return $"oidc_{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static string SingleClaim(ClaimsPrincipal principal, string type)
    {
        var values = principal.FindAll(type).Select(c => c.Value).ToArray();
        return values.Length == 1 && !string.IsNullOrWhiteSpace(values[0])
            ? values[0]
            : throw new InvalidOperationException($"A single {type} claim is required");
    }
}
