using BuilderPro.Me.Api;
using BuilderPro.Security;

var builder = WebApplication.CreateBuilder(args);

// Telemetry, health checks, service discovery, resilient HTTP: one line.
builder.AddServiceDefaults();

builder.Services.AddProblemDetails();          // errors as RFC 9457 problem+json
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddBuilderProAuth(builder.Configuration);   // generic OIDC (Keycloak / Entra)

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();                        // who are you? (validates the bearer token)
app.UseAuthorization();                         // are you allowed? (RequireAuthorization on endpoints)

app.MapDefaultEndpoints();                      // /health and /alive (anonymous)
app.MapMeEndpoints();                           // /api/auth-config (anonymous), /api/me (protected)

app.Run();

// Exposes the implicit Program class to WebApplicationFactory<Program> in tests.
public partial class Program;
