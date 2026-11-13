namespace BuilderPro.Security;

/// <summary>
/// Generic OIDC settings. The SAME code validates Keycloak (dev) or Entra (prod) tokens;
/// only these values change per environment (appsettings / environment variables
/// such as <c>Auth__Authority</c>).
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Issuer URL. Keycloak: http://localhost:8080/realms/builderpro. Entra: https://login.microsoftonline.com/{tenant}/v2.0</summary>
    public string Authority { get; set; } = "";

    /// <summary>The API's audience: tokens must be issued FOR this API (aud claim).</summary>
    public string Audience { get; set; } = "builderpro-api";

    /// <summary>The PUBLIC client id the browser uses (sent to the UI via /api/auth-config).</summary>
    public string WebClientId { get; set; } = "builderpro-web";

    public string WebScope { get; set; } = "openid profile email";

    /// <summary>Claim that carries the tenant. Keycloak mapper: tenant_id. Entra: an app-role or extension claim (see LEARN 1b).</summary>
    public string TenantClaim { get; set; } = "tenant_id";

    /// <summary>Claim that carries roles. Keycloak mapper: roles. Entra app roles: roles.</summary>
    public string RoleClaim { get; set; } = "roles";

    /// <summary>Role that may administer memberships (platform staff, not retailer staff).</summary>
    public string AdminRole { get; set; } = "platform-admin";

    /// <summary>Only false for local http Keycloak. Production must use https metadata.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
