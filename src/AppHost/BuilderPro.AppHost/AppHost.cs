// The whole local system, described in code. Read top to bottom: it IS the
// container diagram (C4 level 2) of what runs on your machine.
//
// 1a: Angular -> Me API.   1b: + Keycloak, login, token validation.
// 1c: + YARP gateway: the browser talks ONLY to the gateway.
// 1d: + Postgres + Identity service: tenant comes from MEMBERSHIPS, not just the token.
// 2a: + Catalog service: products per tenant; asks Identity which tenant the caller is in.
// 2d: + Quotes service: prices quotes with catalog list prices and the pure pricing domain.
// 2f: + Redis: hot-read cache for tenant lookups and catalog product lists.
// 3a: + Assistant: read-only product agent (Microsoft Agent Framework, gpt-4o on Microsoft Foundry).

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Keycloak: the local OpenID Connect identity provider (Entra replaces it in Azure).
// A plain container, so no extra NuGet package is needed. FIXED port 8080 because
// the token issuer ("iss") contains the URL: it must be identical for the browser
// (which gets the token) and the API (which validates it).
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
// Mailpit (1f): catches every email locally (sign-up verification, password reset).
// Open its "http" endpoint in the dashboard (http://localhost:8025) to read them.
// Keycloak reaches it as "mailpit:1025" on the Aspire container network. DEV ONLY.
// ---------------------------------------------------------------------------
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.33")
    .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp")
    .WithHttpHealthCheck("/livez", endpointName: "http");

var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", value: "admin", secret: true);
// The Identity service's own Keycloak client (profile edits, password changes). Dev default; set a real one in user secrets.
var identityClientSecret = builder.AddParameter("keycloak-identity-client-secret", value: "builderpro-identity-dev-secret", secret: true);
// 3c: the Assistant's client for on-behalf-of token exchange. Dev default; set a real one in user secrets.
var assistantClientSecret = builder.AddParameter("keycloak-assistant-client-secret", value: "builderpro-assistant-dev-secret", secret: true);

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.4")
    .WithArgs("start-dev", "--import-realm")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    .WithEnvironment("IDENTITY_CLIENT_SECRET", identityClientSecret)   // read by the realm import
    .WithEnvironment("ASSISTANT_CLIENT_SECRET", assistantClientSecret) // 3c: on-behalf-of client
    // Realm (clients, test users, tenant mapper) imported on first start.
    .WithBindMount("../../../infra/keycloak/realms", "/opt/keycloak/data/import", isReadOnly: true)
    // 1e: Navy & Sand login, sign-up and password pages (CSS on top of keycloak.v2).
    .WithBindMount("../../../infra/keycloak/themes/builderpro", "/opt/keycloak/themes/builderpro", isReadOnly: true)
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithHttpHealthCheck("/health/ready", endpointName: "management")
    .WaitFor(mailpit);                  // realm import configures SMTP -> mailpit

// "Continue with Google" (1e): only when BOTH user secrets exist, so the app runs without them.
//   dotnet user-secrets set "Parameters:google-client-id" "..."     --project src/AppHost/BuilderPro.AppHost
//   dotnet user-secrets set "Parameters:google-client-secret" "..." --project src/AppHost/BuilderPro.AppHost
// The realm file reads GOOGLE_* at import time; without them the Google provider stays disabled.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:google-client-id"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:google-client-secret"]))
{
    var googleClientId = builder.AddParameter("google-client-id");
    var googleClientSecret = builder.AddParameter("google-client-secret", secret: true);
    keycloak
        .WithEnvironment("GOOGLE_ENABLED", "true")
        .WithEnvironment("GOOGLE_CLIENT_ID", googleClientId)
        .WithEnvironment("GOOGLE_CLIENT_SECRET", googleClientSecret);
}

// Real email instead of Mailpit (1f), e.g. Zoho for builderpro.everisys.com. Opt-in: only when the
// smtp-* user secrets exist, so local testing never emails real people by accident.
//   Parameters:smtp-host      smtp.zoho.com (or smtppro.zoho.com / smtp.zoho.eu / smtp.zoho.in for your region/plan)
//   Parameters:smtp-port      587 (STARTTLS)
//   Parameters:smtp-user      the Zoho mailbox, e.g. no-reply@builderpro.everisys.com
//   Parameters:smtp-password  a Zoho APP-SPECIFIC password (never the account password)
//   Parameters:smtp-from      optional; defaults to smtp-user
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:smtp-host"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:smtp-password"]))
{
    var smtpUser = builder.Configuration["Parameters:smtp-user"] ?? "";
    keycloak
        .WithEnvironment("SMTP_HOST", builder.AddParameter("smtp-host"))
        .WithEnvironment("SMTP_PORT", builder.Configuration["Parameters:smtp-port"] ?? "587")
        .WithEnvironment("SMTP_STARTTLS", "true")
        .WithEnvironment("SMTP_SSL", "false")
        .WithEnvironment("SMTP_AUTH", "true")
        .WithEnvironment("SMTP_USER", smtpUser)
        .WithEnvironment("SMTP_PASSWORD", builder.AddParameter("smtp-password", secret: true))
        .WithEnvironment("SMTP_FROM", builder.Configuration["Parameters:smtp-from"] ?? smtpUser);
}

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
// Redis: a CACHE, never a source of truth. Services that read cached data and Identity
// (which invalidates tenant lookups on membership changes) all reference it.
// ---------------------------------------------------------------------------
var cache = builder.AddRedis("cache");

// ---------------------------------------------------------------------------
// Identity service: tenant memberships. Seeds the Keycloak test users in Development.
// ---------------------------------------------------------------------------
var identity = builder.AddProject<Projects.BuilderPro_Identity_Api>("identity")
    .WithReference(identityDb)     // injects ConnectionStrings__identitydb
    .WithReference(cache)          // injects ConnectionStrings__cache
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Keycloak__BaseUrl", keycloak.GetEndpoint("http"))
    .WithEnvironment("Keycloak__ClientSecret", identityClientSecret)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(identityDb)
    .WaitFor(cache)
    .WaitFor(keycloak);

// ---------------------------------------------------------------------------
// Catalog service: products per tenant. It never decides the tenant itself: it calls
// Identity (http://identity, via service discovery) with the caller's own token.
// ---------------------------------------------------------------------------
var catalog = builder.AddProject<Projects.BuilderPro_Catalog_Api>("catalog")
    .WithReference(catalogDb)      // injects ConnectionStrings__catalogdb
    .WithReference(cache)
    .WithReference(identity)       // injects services__identity__http__0
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(catalogDb)
    .WaitFor(cache)
    .WaitFor(identity);

// ---------------------------------------------------------------------------
// Quotes service: prices come from Catalog, totals from BuilderPro.Quotes.Domain.
// Calls Identity (tenant, customer, scopes) and Catalog (prices) with the caller's token.
// ---------------------------------------------------------------------------
var quotes = builder.AddProject<Projects.BuilderPro_Quotes_Api>("quotes")
    .WithReference(quotesDb)       // injects ConnectionStrings__quotesdb
    .WithReference(cache)
    .WithReference(identity)
    .WithReference(catalog)
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(quotesDb)
    .WaitFor(cache)
    .WaitFor(identity)
    .WaitFor(catalog);

// 6d: Stripe payments (TEST mode). Opt-in: only when BOTH user secrets exist; otherwise the deposit uses
// the simulated test-mode page. Webhooks reach your machine with the Stripe CLI:
//   dotnet user-secrets set "Parameters:stripe-secret-key"     "sk_test_..." --project src/AppHost/BuilderPro.AppHost
//   dotnet user-secrets set "Parameters:stripe-webhook-secret" "whsec_..."   --project src/AppHost/BuilderPro.AppHost
//   stripe listen --forward-to http://localhost:<gateway port>/api/payments/stripe/webhook
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:stripe-secret-key"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:stripe-webhook-secret"]))
{
    var stripeKey = builder.AddParameter("stripe-secret-key", secret: true);
    var stripeWebhook = builder.AddParameter("stripe-webhook-secret", secret: true);
    quotes
        .WithEnvironment("Payments__Provider", "stripe")
        .WithEnvironment("Payments__Stripe__SecretKey", stripeKey)
        .WithEnvironment("Payments__Stripe__WebhookSecret", stripeWebhook)
        .WithEnvironment("Payments__ReturnBaseUrl", "http://localhost:4200");
}

// ---------------------------------------------------------------------------
// Assistant (3a): an agent whose tools call Catalog and Quotes AS THE CALLER. The model is
// Microsoft Foundry; settings come from user secrets (never git):
//   Parameters:foundry-endpoint   https://<resource>.services.ai.azure.com/... (only the host is used)
//   Parameters:foundry-deployment gpt-4o
//   Parameters:foundry-api-key    (secret)
// Without them the assistant answers 503 and everything else works.
// ---------------------------------------------------------------------------
var assistant = builder.AddProject<Projects.BuilderPro_Assistant_Api>("assistant")
    .WithReference(identity)
    .WithReference(catalog)
    .WithReference(quotes)
    .WithEnvironment("Auth__Authority", realmUrl)
    .WithEnvironment("Auth__RequireHttpsMetadata", "false")
    // 3c: tokens here are FOR the Assistant; it calls the platform on-behalf-of the caller (token exchange).
    .WithEnvironment("Auth__Audience", "builderpro-assistant")
    .WithEnvironment("Assistant__TokenExchange__Enabled", "true")
    .WithEnvironment("Assistant__TokenExchange__TokenUrl", ReferenceExpression.Create($"{realmUrl}/protocol/openid-connect/token"))
    .WithEnvironment("Assistant__TokenExchange__ClientSecret", assistantClientSecret)
    .WithHttpHealthCheck("/health")
    .WaitFor(catalog)
    .WaitFor(quotes);
// Or a free local LLM with an OpenAI-compatible API (Ollama, LM Studio, vLLM), e.g. for development:
//   Parameters:assistant-provider  local
//   Parameters:local-llm-endpoint  http://localhost:11434/v1   (the server's OpenAI-compatible base URL)
//   Parameters:local-llm-model     qwen2.5:7b                  (a model that supports tool calling)
//   Parameters:local-llm-api-key   optional (e.g. FreeLLMAPI's unified key; Ollama needs none)
if (builder.Configuration["Parameters:assistant-provider"] == "local" &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:local-llm-endpoint"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:local-llm-model"]))
{
    assistant
        .WithEnvironment("Assistant__Provider", "openai-compatible")
        .WithEnvironment("Assistant__Endpoint", builder.AddParameter("local-llm-endpoint"))
        .WithEnvironment("Assistant__Deployment", builder.AddParameter("local-llm-model"));
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:local-llm-api-key"]))
    {
        assistant.WithEnvironment("Assistant__ApiKey", builder.AddParameter("local-llm-api-key", secret: true));
    }
}
else if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:foundry-endpoint"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:foundry-deployment"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:foundry-api-key"]))
{
    assistant
        .WithEnvironment("Assistant__Endpoint", builder.AddParameter("foundry-endpoint"))
        .WithEnvironment("Assistant__Deployment", builder.AddParameter("foundry-deployment"))
        .WithEnvironment("Assistant__ApiKey", builder.AddParameter("foundry-api-key", secret: true));
}

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
    .WithReference(assistant)
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
