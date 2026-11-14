using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BuilderPro.Assistant.Api;

public static class TokenExchangeProtocols
{
    public const string TokenExchange = "token-exchange";   // RFC 8693 (Keycloak, Okta, Auth0)
    public const string EntraObo = "entra-obo";              // Microsoft Entra jwt-bearer on-behalf-of
}

public sealed class TokenExchangeOptions
{
    /// <summary>Off = the caller's token is passed through (development fallback only; MCP guidance forbids it).</summary>
    public bool Enabled { get; set; }

    /// <summary>Chosen explicitly: token-exchange (RFC 8693) or entra-obo. No guessing.</summary>
    public string Protocol { get; set; } = TokenExchangeProtocols.TokenExchange;
    public string TokenUrl { get; set; } = "";               // the identity provider's token endpoint
    public string ClientId { get; set; } = "builderpro-assistant";
    public string ClientSecret { get; set; } = "";
    public string Audience { get; set; } = "builderpro-api";  // RFC 8693: what Catalog and Quotes accept
    public string? Scope { get; set; }                        // entra-obo: api://<platform-api>/.default

    /// <summary>Fail at startup, not at the first customer: complete settings and a trusted endpoint.</summary>
    public void Validate(bool allowHttpForLocalDev)
    {
        if (Protocol is not (TokenExchangeProtocols.TokenExchange or TokenExchangeProtocols.EntraObo))
            throw new InvalidOperationException($"Assistant:TokenExchange:Protocol must be {TokenExchangeProtocols.TokenExchange} or {TokenExchangeProtocols.EntraObo}.");
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("Assistant:TokenExchange needs ClientId and ClientSecret.");
        if (Protocol == TokenExchangeProtocols.EntraObo ? string.IsNullOrWhiteSpace(Scope) : string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("Assistant:TokenExchange needs Scope (entra-obo) or Audience (token-exchange).");
        if (!Uri.TryCreate(TokenUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Assistant:TokenExchange:TokenUrl must be a plain absolute URL.");
        // HTTPS always, except a local identity provider in development (localhost or a container name).
        var localHttp = uri.Scheme == Uri.UriSchemeHttp && allowHttpForLocalDev && (uri.IsLoopback || !uri.Host.Contains('.'));
        if (uri.Scheme != Uri.UriSchemeHttps && !localHttp)
            throw new InvalidOperationException("Assistant:TokenExchange:TokenUrl must be HTTPS.");
    }
}

/// <summary>
/// On-behalf-of (3c): the Assistant accepts only tokens issued FOR IT (aud builderpro-assistant: the web
/// app's, or an MCP client's). Before it calls Catalog or Quotes it exchanges that token at the identity
/// provider for a token for the platform API, still for the SAME person: same subject, so the same
/// tenant, customer and scopes. The caller's own token never leaves the Assistant (no token passthrough).
///
/// Practices: an explicit protocol; a trusted endpoint (validated at startup); a dedicated HTTP client
/// with no caller credentials, no redirects and a short timeout; the answer is checked (Bearer, positive
/// lifetime, sane size, no whitespace); one exchange per caller token, cached only until shortly before
/// it expires and keyed by a SHA-256 of the token (never the token); failures are sanitised (no tokens or
/// response bodies in logs or errors); no retries here, the caller decides.
/// </summary>
public interface ITokenExchange
{
    bool Enabled { get; }

    /// <summary>A platform-API token for the same person, or null when the identity provider refused or failed.</summary>
    Task<string?> ForPlatformAsync(string callerToken, CancellationToken ct);
}

public sealed class OAuthTokenExchange(HttpClient http, TokenExchangeOptions options, TimeProvider clock, ILogger<OAuthTokenExchange> log) : ITokenExchange
{
    public const int MaxTokenLength = 65_536;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _cache = new();

    public bool Enabled => options.Enabled;

    public async Task<string?> ForPlatformAsync(string callerToken, CancellationToken ct)
    {
        if (!IsTokenShaped(callerToken)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callerToken)));
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && hit.Until > now) return hit.Token;

        var form = new Dictionary<string, string> { ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret };
        if (options.Protocol == TokenExchangeProtocols.EntraObo)
        {
            form["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer";
            form["requested_token_use"] = "on_behalf_of";
            form["assertion"] = callerToken;
            form["scope"] = options.Scope!;
        }
        else
        {
            form["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange";
            form["subject_token"] = callerToken;
            form["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token";
            form["requested_token_type"] = "urn:ietf:params:oauth:token-type:access_token";
            form["audience"] = options.Audience;
            if (!string.IsNullOrWhiteSpace(options.Scope)) form["scope"] = options.Scope;
        }

        try
        {
            using var response = await http.PostAsync(options.TokenUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("On-behalf-of exchange refused ({Status})", (int)response.StatusCode);
                return null;
            }
            var answer = await response.Content.ReadFromJsonAsync<Exchanged>(ct);
            if (answer is null || !IsTokenShaped(answer.AccessToken)
                || !string.Equals(answer.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || answer.ExpiresIn <= 0)
            {
                log.LogWarning("On-behalf-of exchange returned an unusable token response");
                return null;
            }
            if (_cache.Count > 10_000) _cache.Clear();   // bounded
            _cache[key] = (answer.AccessToken!, now.AddSeconds(Math.Max(0, answer.ExpiresIn - 30)));
            return answer.AccessToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or System.Text.Json.JsonException)
        {
            log.LogWarning("On-behalf-of exchange unavailable ({Error})", ex.GetType().Name);   // sanitised
            return null;
        }
    }

    private static bool IsTokenShaped(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= MaxTokenLength && !token.Any(char.IsWhiteSpace);

    private sealed record Exchanged(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>Exchange switched off: no token for the platform (callers fall back to passthrough explicitly).</summary>
public sealed class NoTokenExchange : ITokenExchange
{
    public bool Enabled => false;
    public Task<string?> ForPlatformAsync(string callerToken, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// 3c: OAuth Protected Resource Metadata (RFC 9728) for the MCP endpoint, so an MCP client can discover
/// which authorization server issues tokens for it. A 401 from /api/assistant/mcp points here
/// (WWW-Authenticate: Bearer resource_metadata="...").
/// </summary>
public static class McpAuthMetadata
{
    public const string McpPath = "/api/assistant/mcp";
    public const string MetadataPath = "/.well-known/oauth-protected-resource";

    public static IEndpointRouteBuilder MapMcpAuthMetadata(this IEndpointRouteBuilder app)
    {
        IResult Metadata(HttpContext http, IConfiguration config) => Results.Ok(new
        {
            resource = $"{PublicBase(http, config)}{McpPath}",
            authorization_servers = new[] { (config["Auth:PublicAuthority"] ?? config["Auth:Authority"] ?? "").TrimEnd('/') },
            bearer_methods_supported = new[] { "header" },
            scopes_supported = new[] { "openid" },
            resource_name = "BuilderPro tools",
        });
        app.MapGet(MetadataPath, Metadata).AllowAnonymous();
        app.MapGet($"{MetadataPath}{McpPath}", Metadata).AllowAnonymous();
        return app;
    }

    /// <summary>Adds the discovery hint to 401s from the MCP endpoint.</summary>
    public static IApplicationBuilder UseMcpAuthChallenge(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (http.Request.Path.StartsWithSegments(McpPath))
        {
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            http.Response.OnStarting(() =>
            {
                if (http.Response.StatusCode == StatusCodes.Status401Unauthorized)
                    http.Response.Headers.Append("WWW-Authenticate", $"Bearer resource_metadata=\"{PublicBase(http, config)}{MetadataPath}{McpPath}\"");
                return Task.CompletedTask;
            });
        }
        await next();
    });

    /// <summary>Where clients reach us (the gateway); configurable because the service itself sits behind it.</summary>
    private static string PublicBase(HttpContext http, IConfiguration config) =>
        (config["Assistant:PublicBaseUrl"] ?? $"{http.Request.Scheme}://{http.Request.Host}").TrimEnd('/');
}
