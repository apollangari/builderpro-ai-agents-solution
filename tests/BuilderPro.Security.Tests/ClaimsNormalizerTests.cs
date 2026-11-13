using System.Security.Claims;
using BuilderPro.Security;

namespace BuilderPro.Security.Tests;

/// <summary>
/// The same normaliser must produce the same UserContext SHAPE from a Keycloak token and an
/// Entra token. These claim sets mirror what each provider really issues.
/// </summary>
public sealed class ClaimsNormalizerTests
{
    private static readonly AuthOptions Options = new() { TenantClaim = "tenant_id", RoleClaim = "roles" };

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Bearer"));

    [Fact]
    public void Keycloak_token_is_keyed_by_issuer_and_subject()
    {
        var user = ClaimsNormalizer.FromPrincipal(Principal(
            ("iss", "http://localhost:8080/realms/builderpro"),
            ("sub", "a11ce000-0000-4000-8000-000000000001"),
            ("preferred_username", "alice"),
            ("email", "alice@acme.test"),
            ("tenant_id", "t-acme"),
            ("roles", "customer"), ("roles", "offline_access")), Options);

        Assert.Equal(ExternalIdentityKey.ForIssuerAndSubject("http://localhost:8080/realms/builderpro", "a11ce000-0000-4000-8000-000000000001"), user.UserKey);
        Assert.StartsWith("oidc_", user.UserKey);
        Assert.Equal("alice", user.UserName);
        Assert.Equal("t-acme", user.TenantHint);
        Assert.Equal(new[] { "customer", "offline_access" }, user.Roles);
    }

    [Fact]
    public void Entra_token_is_keyed_by_oid_not_by_the_pairwise_sub()
    {
        const string oid = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
        var user = ClaimsNormalizer.FromPrincipal(Principal(
            ("iss", "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0"),
            ("sub", "pairwise-subject-differs-per-app"),
            ("oid", oid),
            ("tid", "11111111-2222-3333-4444-555555555555"),
            ("preferred_username", "alice@contoso.com"),
            ("roles", "staff")), Options);

        Assert.Equal(oid, user.UserKey);
        Assert.Equal("alice@contoso.com", user.UserName);
        Assert.Null(user.TenantHint);   // Entra's tid is the DIRECTORY, not our retailer tenant
        Assert.Equal(new[] { "staff" }, user.Roles);
    }

    [Fact]
    public void Same_subject_from_two_issuers_is_two_different_people()
    {
        var a = ExternalIdentityKey.ForIssuerAndSubject("https://idp-a.test", "123");
        var b = ExternalIdentityKey.ForIssuerAndSubject("https://idp-b.test", "123");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("two-tenant-claims")]
    [InlineData("malformed")]
    public void Ambiguous_or_malformed_tenant_claims_give_no_hint(string scenario)
    {
        var claims = new List<(string, string)> { ("iss", "https://idp.test"), ("sub", "u1") };
        if (scenario == "two-tenant-claims") { claims.Add(("tenant_id", "t-acme")); claims.Add(("tenant_id", "t-globex")); }
        else { claims.Add(("tenant_id", "t acme; DROP TABLE")); }

        var user = ClaimsNormalizer.FromPrincipal(Principal([.. claims]), Options);

        Assert.Null(user.TenantHint);
    }

    [Fact]
    public void Two_oid_claims_are_rejected()
    {
        var principal = Principal(("iss", "https://idp.test"), ("sub", "u1"), ("oid", "3f2504e0-4f89-41d3-9a0c-0305e82c3301"), ("oid", "3f2504e0-4f89-41d3-9a0c-0305e82c3302"));
        Assert.Throws<InvalidOperationException>(() => ClaimsNormalizer.FromPrincipal(principal, Options));
    }

    [Fact]
    public void Anonymous_principal_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ClaimsNormalizer.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity()), Options));
    }
}
