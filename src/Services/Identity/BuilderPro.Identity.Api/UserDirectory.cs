using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuilderPro.Identity.Api;

/// <summary>The profile fields a customer edits (the same as sign-up, minus username and email).</summary>
public sealed record UserProfile(
    string FirstName, string LastName, string Email, string Phone,
    string AddressLine1, string? AddressLine2, string City, string Region, string PostalCode, string Country,
    string ContactConsent, string ContactHours);

public enum PasswordChange { Changed, Rejected }

/// <summary>A search hit (1i salesperson's customer search): the directory's user id and contact basics.</summary>
public sealed record DirectoryUser(string Id, string FirstName, string LastName, string Email, string? Phone, string? PostalCode);

/// <summary>
/// The user store behind sign-in (Keycloak locally, Entra in Azure later). The Identity service is the
/// ONLY code that talks to it, with its own confidential client: the browser never gets admin rights.
/// </summary>
public interface IUserDirectory
{
    Task<(UserProfile Profile, bool HasPassword)?> GetAsync(string userId, CancellationToken ct);

    /// <summary>Saves the profile; returns the directory's own validation messages, or empty when saved.</summary>
    Task<IReadOnlyList<string>> UpdateAsync(string userId, UserProfile profile, CancellationToken ct);

    /// <summary>True when this password signs this user in (checked by the directory, never stored or logged here).</summary>
    Task<bool> VerifyPasswordAsync(string userId, string password, CancellationToken ct);

    /// <summary>Sets a new password under the directory's password policy; Rejected + message when it breaks the policy.</summary>
    Task<(PasswordChange Result, string? Message)> SetPasswordAsync(string userId, string newPassword, CancellationToken ct);

    /// <summary>Users whose name, email or username contains the text (at most max).</summary>
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(string text, int max, CancellationToken ct);
}

public sealed class KeycloakOptions
{
    public string BaseUrl { get; set; } = "";          // http://localhost:8080
    public string Realm { get; set; } = "builderpro";
    public string ClientId { get; set; } = "builderpro-identity";
    public string ClientSecret { get; set; } = "";
}

/// <summary>
/// Keycloak Admin REST API with a service-account token (client_credentials, realm-management:
/// manage-users). Password checks use the token endpoint with the user's username + current password
/// on the same confidential client; the password is passed through, never kept.
/// </summary>
public sealed class KeycloakUserDirectory(HttpClient http, KeycloakOptions options, TimeProvider clock, ILogger<KeycloakUserDirectory> log) : IUserDirectory
{
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string Admin(string userId, string rest = "") => $"{options.BaseUrl}/admin/realms/{options.Realm}/users/{Uri.EscapeDataString(userId)}{rest}";
    private string TokenUrl => $"{options.BaseUrl}/realms/{options.Realm}/protocol/openid-connect/token";

    public async Task<(UserProfile Profile, bool HasPassword)?> GetAsync(string userId, CancellationToken ct)
    {
        var user = await GetUserAsync(userId, ct);
        if (user is null) return null;
        var creds = await SendAsync(HttpMethod.Get, Admin(userId, "/credentials"), null, ct);
        var hasPassword = creds.IsSuccessStatusCode
            && (await creds.Content.ReadFromJsonAsync<JsonArray>(ct) ?? []).Any(c => c?["type"]?.GetValue<string>() == "password");
        return (ToProfile(user), hasPassword);
    }

    public async Task<IReadOnlyList<string>> UpdateAsync(string userId, UserProfile p, CancellationToken ct)
    {
        var user = await GetUserAsync(userId, ct) ?? throw new InvalidOperationException("user not found");
        // Change only our fields on the full representation (Keycloak replaces what it receives).
        user["firstName"] = p.FirstName;
        user["lastName"] = p.LastName;
        var attrs = user["attributes"] as JsonObject ?? [];
        void Set(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) attrs.Remove(key);
            else attrs[key] = new JsonArray(value);
        }
        Set("phone", p.Phone);
        Set("addressLine1", p.AddressLine1);
        Set("addressLine2", p.AddressLine2);
        Set("city", p.City);
        Set("region", p.Region);
        Set("postalCode", p.PostalCode);
        Set("country", p.Country);
        Set("contactConsent", p.ContactConsent);
        Set("contactHours", p.ContactHours);
        user["attributes"] = attrs;

        var response = await SendAsync(HttpMethod.Put, Admin(userId), user, ct);
        if (response.IsSuccessStatusCode) return [];
        var body = await response.Content.ReadAsStringAsync(ct);
        log.LogWarning("Keycloak refused a profile update ({Status})", (int)response.StatusCode);
        return [ErrorMessage(body) ?? "The profile couldn't be saved."];
    }

    public async Task<bool> VerifyPasswordAsync(string userId, string password, CancellationToken ct)
    {
        var user = await GetUserAsync(userId, ct);
        var username = user?["username"]?.GetValue<string>();
        if (username is null) return false;
        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid",
        }), ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<(PasswordChange Result, string? Message)> SetPasswordAsync(string userId, string newPassword, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Put, Admin(userId, "/reset-password"),
            new JsonObject { ["type"] = "password", ["value"] = newPassword, ["temporary"] = false }, ct);
        if (response.IsSuccessStatusCode) return (PasswordChange.Changed, null);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (PasswordChange.Rejected, PolicyMessage(ErrorMessage(body)));
    }

    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(string text, int max, CancellationToken ct)
    {
        var url = $"{options.BaseUrl}/admin/realms/{options.Realm}/users?search={Uri.EscapeDataString(text)}&max={max}&briefRepresentation=false";
        var response = await SendAsync(HttpMethod.Get, url, null, ct);
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [])
            .OfType<JsonObject>()
            .Where(u => u["id"] is not null && u["serviceAccountClientId"] is null)
            .Select(u =>
            {
                var p = ToProfile(u);
                return new DirectoryUser(u["id"]!.GetValue<string>(), p.FirstName, p.LastName, p.Email,
                    string.IsNullOrEmpty(p.Phone) ? null : p.Phone, string.IsNullOrEmpty(p.PostalCode) ? null : p.PostalCode);
            })];
    }

    private async Task<JsonObject?> GetUserAsync(string userId, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, Admin(userId), null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync(ct));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, ct);
    }

    private async Task<string> AdminTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpires) return _token;
            using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            }), ct);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidOperationException("no token");
            _token = token["access_token"]!.GetValue<string>();
            _tokenExpires = clock.GetUtcNow().AddSeconds((token["expires_in"]?.GetValue<int>() ?? 60) - 15);
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static UserProfile ToProfile(JsonObject u)
    {
        string? A(string key) => (u["attributes"]?[key] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
        return new UserProfile(
            u["firstName"]?.GetValue<string>() ?? "", u["lastName"]?.GetValue<string>() ?? "", u["email"]?.GetValue<string>() ?? "",
            A("phone") ?? "", A("addressLine1") ?? "", A("addressLine2"), A("city") ?? "", A("region") ?? "", A("postalCode") ?? "",
            A("country") ?? "US", A("contactConsent") ?? "", A("contactHours") ?? "business");
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            return json?["errorMessage"]?.GetValue<string>() ?? json?["error_description"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Keycloak's policy keys -> words a customer understands.</summary>
    private static string PolicyMessage(string? key) => key switch
    {
        null => "That password isn't allowed.",
        _ when key.Contains("MinLength", StringComparison.OrdinalIgnoreCase) => "The new password is too short.",
        _ when key.Contains("History", StringComparison.OrdinalIgnoreCase) => "Use a password you haven't used recently.",
        _ when key.Contains("Username", StringComparison.OrdinalIgnoreCase) => "The password can't contain your username.",
        _ when key.Contains("Digits", StringComparison.OrdinalIgnoreCase) => "Add a number to the new password.",
        _ when key.Contains("UpperCase", StringComparison.OrdinalIgnoreCase) => "Add an upper-case letter to the new password.",
        _ when key.Contains("SpecialChars", StringComparison.OrdinalIgnoreCase) => "Add a symbol to the new password.",
        _ => "That password isn't allowed.",
    };
}
