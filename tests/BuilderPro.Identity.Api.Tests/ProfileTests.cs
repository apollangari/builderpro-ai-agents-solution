using System.Net;
using System.Net.Http.Json;
using BuilderPro.Identity.Api;

namespace BuilderPro.Identity.Api.Tests;

/// <summary>Stands in for Keycloak: users by id, their passwords, and a password policy (min 10 chars here).</summary>
public sealed class FakeUserDirectory : IUserDirectory
{
    public Dictionary<string, (UserProfile Profile, string? Password)> Users { get; } = new()
    {
        ["alice"] = (new UserProfile("Alice", "Acme", "alice@example.test", "+1 555 0100", "1 Main St", null, "Prosper", "TX", "75078", "US", "both", "business"), "Passw0rd!"),
        ["gina"] = (new UserProfile("Gina", "Google", "gina@example.test", "+1 555 0101", "2 Main St", null, "Prosper", "TX", "75078", "US", "text", "evenings"), null),
    };

    public Task<(UserProfile Profile, bool HasPassword)?> GetAsync(string userId, CancellationToken ct) =>
        Task.FromResult(Users.TryGetValue(userId, out var u) ? (u.Profile, u.Password is not null) : ((UserProfile, bool)?)null);

    public Task<IReadOnlyList<string>> UpdateAsync(string userId, UserProfile profile, CancellationToken ct)
    {
        Users[userId] = (profile, Users[userId].Password);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<bool> VerifyPasswordAsync(string userId, string password, CancellationToken ct) =>
        Task.FromResult(Users.TryGetValue(userId, out var u) && u.Password == password);

    public bool SearchDown { get; set; }

    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(string text, int max, CancellationToken ct)
    {
        if (SearchDown) throw new HttpRequestException("directory down");
        return Task.FromResult<IReadOnlyList<DirectoryUser>>([.. Users
            .Where(u => $"{u.Key} {u.Value.Profile.FirstName} {u.Value.Profile.LastName} {u.Value.Profile.Email}".Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .Select(u => new DirectoryUser(u.Key, u.Value.Profile.FirstName, u.Value.Profile.LastName, u.Value.Profile.Email, u.Value.Profile.Phone, u.Value.Profile.PostalCode))]);
    }

    public Task<(PasswordChange Result, string? Message)> SetPasswordAsync(string userId, string newPassword, CancellationToken ct)
    {
        if (newPassword.Length < 10) return Task.FromResult<(PasswordChange, string?)>((PasswordChange.Rejected, "The new password is too short."));
        Users[userId] = (Users[userId].Profile, newPassword);
        return Task.FromResult<(PasswordChange, string?)>((PasswordChange.Changed, null));
    }
}

public sealed class ProfileTests(IdentityFactory f) : IClassFixture<IdentityFactory>
{
    private readonly IdentityFactory _f = f;

    private static object Update(string consent = "both", string phone = "+1 555 0199", string postal = "75078") => new
    {
        firstName = "Alice", lastName = "Acme-Smith", phone, addressLine1 = "9 Elm St", addressLine2 = "Unit 2",
        city = "Prosper", region = "TX", postalCode = postal, country = "US", contactConsent = consent, contactHours = "evenings",
    };

    [Fact]
    public async Task My_profile_comes_back_with_whether_I_have_a_password()
    {
        var p = await _f.As("alice").GetFromJsonAsync<ProfileDto>("/api/identity/profile");
        Assert.Equal(("Alice", "75078", true), (p!.FirstName, p.PostalCode, p.CanChangePassword));
        var g = await _f.As("gina").GetFromJsonAsync<ProfileDto>("/api/identity/profile");
        Assert.False(g!.CanChangePassword);   // a Google sign-in
    }

    [Fact]
    public async Task Saving_updates_the_store_and_records_a_contact_change_in_the_consent_history()
    {
        var r = await _f.As("alice").PutAsJsonAsync("/api/identity/profile", Update(consent: "text"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(("Acme-Smith", "9 Elm St", "text"), (_f.Directory.Users["alice"].Profile.LastName, _f.Directory.Users["alice"].Profile.AddressLine1, _f.Directory.Users["alice"].Profile.ContactConsent));
        Assert.Equal("alice@example.test", _f.Directory.Users["alice"].Profile.Email);   // email isn't editable here

        var latest = await _f.Consents.LatestAsync(IdentityFactory.KeyFor("alice"), default);
        Assert.Equal(("text", "evenings", "profile"), (latest!.Consent, latest.Hours, latest.Source));
    }

    [Fact]
    public async Task Bad_fields_are_named_and_nothing_is_saved()
    {
        var before = _f.Directory.Users["alice"].Profile;
        var r = await _f.As("alice").PutAsJsonAsync("/api/identity/profile", Update(phone: "call me", postal: "!"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("\"field\":\"phone\"", body);
        Assert.Contains("\"field\":\"postalCode\"", body);
        Assert.Equal(before, _f.Directory.Users["alice"].Profile);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_follows_the_policy()
    {
        using var f = new IdentityFactory();
        var wrong = await f.As("alice").PostAsJsonAsync("/api/identity/password", new { currentPassword = "nope", newPassword = "Better-Passw0rd" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("wrong_password", await wrong.Content.ReadAsStringAsync());

        var weak = await f.As("alice").PostAsJsonAsync("/api/identity/password", new { currentPassword = "Passw0rd!", newPassword = "short-1x" });
        Assert.Contains("too short", await weak.Content.ReadAsStringAsync());   // the store's policy, in plain words

        var ok = await f.As("alice").PostAsJsonAsync("/api/identity/password", new { currentPassword = "Passw0rd!", newPassword = "Better-Passw0rd" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal("Better-Passw0rd", f.Directory.Users["alice"].Password);
    }

    [Fact]
    public async Task A_google_sign_in_has_no_password_to_change()
    {
        var r = await _f.As("gina").PostAsJsonAsync("/api/identity/password", new { currentPassword = "x", newPassword = "Better-Passw0rd" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("no_password", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_gets_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/identity/profile")).StatusCode);
}
