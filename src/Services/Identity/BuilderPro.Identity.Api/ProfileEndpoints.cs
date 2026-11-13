using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;
using BuilderPro.Identity.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Identity.Api;

public sealed record ProfileDto(
    string FirstName, string LastName, string Email, string Phone,
    string AddressLine1, string? AddressLine2, string City, string Region, string PostalCode, string Country,
    string ContactConsent, string ContactHours, bool CanChangePassword);

public sealed record ProfileUpdateRequest(
    string? FirstName, string? LastName, string? Phone,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Country,
    string? ContactConsent, string? ContactHours);

public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);

public sealed record FieldErrorDto(string Field, string Message);

/// <summary>
/// The customer's own profile and password (owner request), edited in the app and saved to the user
/// store by this service. Rules mirror the sign-up form (realm user profile); the store re-checks them.
/// Contact-preference changes are recorded in the consent history (source "profile").
/// Email stays read-only here: it's the sign-in name and changing it needs verification.
/// </summary>
public static partial class ProfileEndpoints
{
    public const int MinPassword = 8;
    public const int MaxPassword = 128;

    public static RouteGroupBuilder MapProfileEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/profile", GetAsync).WithSummary("My profile (sign-up fields) and whether I have a password here.");
        api.MapPut("/profile", UpdateAsync).WithSummary("Save my profile. Contact-preference changes are added to the consent history.");
        api.MapPost("/password", ChangePasswordAsync).WithSummary("Change my password: the current one is checked first. Not for Google sign-ins.");
        return api;
    }

    internal static async Task<IResult> GetAsync(ClaimsPrincipal principal, IUserDirectory directory, CancellationToken ct)
    {
        var id = principal.FindFirst("sub")?.Value;
        if (id is null) return Results.Unauthorized();
        var found = await directory.GetAsync(id, ct);
        return found is null ? Results.NotFound() : Results.Ok(ToDto(found.Value.Profile, found.Value.HasPassword));
    }

    internal static async Task<IResult> UpdateAsync(
        ProfileUpdateRequest r, ClaimsPrincipal principal, AuthOptions auth, IUserDirectory directory, IConsentStore consents,
        IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var id = principal.FindFirst("sub")?.Value;
        if (id is null) return Results.Unauthorized();
        var current = await directory.GetAsync(id, ct);
        if (current is null) return Results.NotFound();

        var errors = Validate(r);
        if (errors.Count > 0) return Invalid("invalid_profile", errors);

        var p = new UserProfile(r.FirstName!.Trim(), r.LastName!.Trim(), current.Value.Profile.Email, r.Phone!.Trim(),
            r.AddressLine1!.Trim(), string.IsNullOrWhiteSpace(r.AddressLine2) ? null : r.AddressLine2.Trim(),
            r.City!.Trim(), r.Region!.Trim(), r.PostalCode!.Trim().ToUpperInvariant(), r.Country!, r.ContactConsent!, r.ContactHours!);
        var refused = await directory.UpdateAsync(id, p, ct);
        if (refused.Count > 0) return Invalid("invalid_profile", [.. refused.Select(m => new FieldErrorDto("", m))]);

        var user = ClaimsNormalizer.FromPrincipal(principal, auth);
        await ConsentPolicy.RecordIfChangedAsync(consents, user.UserKey, p.ContactConsent, p.ContactHours,
            ConsentEndpoints.WordingVersion(config), "profile", clock.GetUtcNow(), ct);
        return Results.Ok(ToDto(p, current.Value.HasPassword));
    }

    internal static async Task<IResult> ChangePasswordAsync(PasswordChangeRequest r, ClaimsPrincipal principal, IUserDirectory directory, CancellationToken ct)
    {
        var id = principal.FindFirst("sub")?.Value;
        if (id is null) return Results.Unauthorized();
        var current = await directory.GetAsync(id, ct);
        if (current is null) return Results.NotFound();
        if (!current.Value.HasPassword)
            return Problem(StatusCodes.Status409Conflict, "no_password", "You sign in with another provider (e.g. Google), so there's no password to change here.");

        if (string.IsNullOrEmpty(r.CurrentPassword) || r.CurrentPassword.Length > MaxPassword)
            return Invalid("invalid_password", [new("currentPassword", "Enter your current password.")]);
        if (string.IsNullOrEmpty(r.NewPassword) || r.NewPassword.Length < MinPassword || r.NewPassword.Length > MaxPassword)
            return Invalid("invalid_password", [new("newPassword", $"Use at least {MinPassword} characters.")]);
        if (r.NewPassword == r.CurrentPassword)
            return Invalid("invalid_password", [new("newPassword", "The new password must be different.")]);

        if (!await directory.VerifyPasswordAsync(id, r.CurrentPassword, ct))
            return Invalid("wrong_password", [new("currentPassword", "That isn't your current password.")]);

        var (result, message) = await directory.SetPasswordAsync(id, r.NewPassword, ct);
        return result == PasswordChange.Changed
            ? Results.NoContent()
            : Invalid("invalid_password", [new("newPassword", message ?? "That password isn't allowed.")]);
    }

    /// <summary>Same rules as the sign-up form (infra/keycloak realm user profile).</summary>
    internal static List<FieldErrorDto> Validate(ProfileUpdateRequest r)
    {
        var e = new List<FieldErrorDto>();
        void Required(string field, string? value, int max, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) e.Add(new(field, $"{label} is required."));
            else if (value.Trim().Length > max) e.Add(new(field, $"{label} is too long."));
        }
        Required("firstName", r.FirstName, 255, "First name");
        Required("lastName", r.LastName, 255, "Last name");
        Required("phone", r.Phone, 20, "Mobile phone");
        if (!string.IsNullOrWhiteSpace(r.Phone) && !Phone().IsMatch(r.Phone.Trim())) e.Add(new("phone", "Enter a phone number like +1 555 123 4567."));
        Required("addressLine1", r.AddressLine1, 120, "Street address");
        if ((r.AddressLine2?.Length ?? 0) > 120) e.Add(new("addressLine2", "Apartment, suite is too long."));
        Required("city", r.City, 80, "City");
        Required("region", r.Region, 80, "State / province");
        Required("postalCode", r.PostalCode, 12, "ZIP / postal code");
        if (!string.IsNullOrWhiteSpace(r.PostalCode) && !Postal().IsMatch(r.PostalCode.Trim())) e.Add(new("postalCode", "Enter a valid ZIP or postal code."));
        if (r.Country is not ("US" or "CA")) e.Add(new("country", "Choose United States or Canada."));
        if (!ConsentPolicy.IsValid(r.ContactConsent, r.ContactHours)) e.Add(new("contactConsent", "Choose how and when we may contact you."));
        return e;
    }

    private static ProfileDto ToDto(UserProfile p, bool hasPassword) => new(
        p.FirstName, p.LastName, p.Email, p.Phone, p.AddressLine1, p.AddressLine2, p.City, p.Region, p.PostalCode, p.Country,
        p.ContactConsent, p.ContactHours, hasPassword);

    private static IResult Invalid(string code, IReadOnlyList<FieldErrorDto> errors) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: code, detail: "Some fields need a fix.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["errors"] = errors });

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    [GeneratedRegex(@"^\+?[0-9 ().-]{7,20}$")]
    private static partial Regex Phone();

    [GeneratedRegex(@"^[A-Za-z0-9 -]{3,12}$")]
    private static partial Regex Postal();
}
