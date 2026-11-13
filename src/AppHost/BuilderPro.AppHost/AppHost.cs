// The whole local system, described in code. Read top to bottom: it IS the
// container diagram (C4 level 2) of what runs on your machine.
//
// 1a: Angular -> Me API.   1b: + Keycloak, login, token validation.
// 1c: + YARP gateway: the browser talks ONLY to the gateway.
// 1d: + Postgres + Identity service: tenant comes from MEMBERSHIPS, not just the token.
// 2a: + Catalog service: products per tenant; asks Identity which tenant the caller is in.
// 2d: + Quotes service: prices quotes with catalog list prices and the pure pricing domain.

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Keycloak: the local OpenID Connect identity provider (Entra replaces it in Azure).
// A plain container, so no extra NuGet package is needed. FIXED port 8080 because
// the token issuer ("iss") contains the URL: it must be identical for the browser
// (which gets the token) and the API (which validates it).
// ---------------------------------------------------------------------------
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", value: "admin", secret: true);

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.4")
    .WithArgs("start-dev", "--import-realm")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    // Realm (clients, test users, tenant mapper) imported on first start.
    .WithBindMount("../../../infra/keycloak/realms", "/opt/keycloak/data/import", isReadOnly: true)
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithHttpHealthCheck("/health/ready", endpointName: "management");

var realmUrl = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/builderpro");

// ---------------------------------------------------------------------------
// Postgres: ONE server locally, but a separate DATABASE per service
// (database-per-service). Only the Identity service gets identitydb's connection string.
// ---------------------------------------------------------------------------
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()              // memberships survive AppHost restarts
    .WithPgAdmin();                // browse the data at the pgadmin endpoint in the dashboard
var identityDb = postgres.AddDatabase("identitydb");
var catalogDb = postgres.AddDatabase("catalogdb");
var quotesDb = postgres.AddDatabase("quotesdb");

// ---------------------------------------------------------------------------
// Identity service: tenant memberships. Seeds the Keycloak test users in Development.
// ---------------------------------------------------------------------------
var identity = builder.AddProject<Projects.BuilderPro_Identity_Api>("identity")
    .WithReference(identityDb)     // injects ConnectionStrings__identitydb
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(identityDb)
    .WaitFor(keycloak);

// ---------------------------------------------------------------------------
// Catalog service: products per tenant. It never decides the tenant itself: it calls
// Identity (http://identity, via service discovery) with the caller's own token.
// ---------------------------------------------------------------------------
var catalog = builder.AddProject<Projects.BuilderPro_Catalog_Api>("catalog")
    .WithReference(catalogDb)      // injects ConnectionStrings__catalogdb
    .WithReference(identity)       // injects services__identity__http__0
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(catalogDb)
    .WaitFor(identity);

// ---------------------------------------------------------------------------
// Quotes service: prices come from Catalog, totals from BuilderPro.Quotes.Domain.
// Calls Identity (tenant, customer, scopes) and Catalog (prices) with the caller's token.
// ---------------------------------------------------------------------------
var quotes = builder.AddProject<Projects.BuilderPro_Quotes_Api>("quotes")
    .WithReference(quotesDb)       // injects ConnectionStrings__quotesdb
    .WithReference(identity)
    .WithReference(catalog)
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(quotesDb)
    .WaitFor(identity)
    .WaitFor(catalog);

// ---------------------------------------------------------------------------
// Me API: validates the bearer token issued by Keycloak.
// ---------------------------------------------------------------------------
var meApi = builder.AddProject<Projects.BuilderPro_Me_Api>("me-api")
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")   // local http Keycloak only
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak);

// ---------------------------------------------------------------------------
// Gateway: the single front door for /api/*. Validates the token once at the edge,
// rate-limits per IP, stamps a correlation id, then forwards to services found by
// service discovery ("http://me-api" in its appsettings.json).
// ---------------------------------------------------------------------------
var gateway = builder.AddProject<Projects.BuilderPro_Gateway>("gateway")
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithReference(meApi)          // injects services__me-api__http__0 for discovery
    .WithReference(identity)
    .WithReference(catalog)
    .WithReference(quotes)
    .WithHttpHealthCheck("/health")
    .WaitFor(meApi)
    .WaitFor(identity)
    .WaitFor(catalog)
    .WaitFor(quotes);

// ---------------------------------------------------------------------------
// Angular. FIXED port 4200 because Keycloak's client only accepts redirects back to
// http://localhost:4200 (redirect URIs must be exact: it's a security control).
// ---------------------------------------------------------------------------
builder.AddJavaScriptApp("web", "../../../web", "start")
    .WithHttpEndpoint(port: 4200, env: "PORT", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithReference(gateway)        // the UI knows the gateway, never the services
    .WaitFor(gateway);

builder.Build().Run();
