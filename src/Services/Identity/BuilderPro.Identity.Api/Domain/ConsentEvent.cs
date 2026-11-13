namespace BuilderPro.Identity.Api.Domain;

/// <summary>
/// One entry in a customer's contact-consent history (1f). Append-only: never updated or deleted,
/// so "what had they agreed to, under which wording, on date X?" always has an answer.
///
/// Consent: call | text | both | none.  Hours: business | evenings | weekends | anytime.
/// WordingVersion identifies the exact sign-up text they saw (Identity:ConsentWording in config).
/// Source: signup (first sign-in through a store link) | sync (seen in a later token: a change).
/// </summary>
public sealed record ConsentEvent(
    long Id, string UserKey, string Consent, string Hours, string WordingVersion, string Source, DateTimeOffset At);

public interface IConsentStore
{
    Task InitializeAsync(CancellationToken ct);
    Task<ConsentEvent?> LatestAsync(string userKey, CancellationToken ct);
    Task<ConsentEvent> AppendAsync(string userKey, string consent, string hours, string wordingVersion, string source, DateTimeOffset at, CancellationToken ct);
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ConsentEvent>> HistoryAsync(string userKey, int limit, CancellationToken ct);
}

/// <summary>The rule for recording consent. Pure decisions; the store does the I/O.</summary>
public static class ConsentPolicy
{
    public static readonly string[] Consents = ["call", "text", "both", "none"];
    public static readonly string[] Hours = ["business", "evenings", "weekends", "anytime"];

    public static bool IsValid(string? consent, string? hours) =>
        consent is not null && hours is not null && Consents.Contains(consent, StringComparer.Ordinal) && Hours.Contains(hours, StringComparer.Ordinal);

    /// <summary>Record only when there is nothing yet, or something the customer chose differs.</summary>
    public static bool ShouldRecord(ConsentEvent? latest, string consent, string hours, string wordingVersion) =>
        latest is null || latest.Consent != consent || latest.Hours != hours || latest.WordingVersion != wordingVersion;

    public static async Task<ConsentEvent?> RecordIfChangedAsync(
        IConsentStore store, string userKey, string? consent, string? hours, string wordingVersion, string source, DateTimeOffset now, CancellationToken ct)
    {
        if (!IsValid(consent, hours)) return null;   // no (or unknown) answer in the token: nothing to record
        var latest = await store.LatestAsync(userKey, ct);
        return ShouldRecord(latest, consent!, hours!, wordingVersion)
            ? await store.AppendAsync(userKey, consent!, hours!, wordingVersion, source, now, ct)
            : null;
    }
}
