using System.Net;
using System.Net.Http.Json;
using BuilderPro.Identity.Api.Domain;

namespace BuilderPro.Identity.Api.Tests;

/// <summary>Contact-consent audit trail (1f).</summary>
public sealed class ConsentTests(IdentityFactory f) : IClassFixture<IdentityFactory>
{
    private readonly IdentityFactory _f = f;

    private static string NewUser() => "c-" + Guid.NewGuid().ToString("N")[..8];

    private HttpClient As(string sub, string? consent, string? hours)
    {
        var c = _f.As(sub);
        if (consent is not null) c.DefaultRequestHeaders.Add("X-Test-Consent", consent);
        if (hours is not null) c.DefaultRequestHeaders.Add("X-Test-Hours", hours);
        return c;
    }

    private async Task<ConsentSyncResponse> Sync(string sub, string? consent, string? hours) =>
        (await (await As(sub, consent, hours).PostAsync("/api/identity/consent/sync", null)).Content.ReadFromJsonAsync<ConsentSyncResponse>())!;

    [Fact]
    public async Task First_sync_records_the_choice_with_the_wording_version()
    {
        var sub = NewUser();

        var r = await Sync(sub, "text", "business");

        Assert.True(r.Recorded);
        Assert.Equal("text", r.Current!.Consent);
        Assert.Equal("business", r.Current.Hours);
        Assert.Equal("2026-11-13", r.Current.WordingVersion);
        Assert.Equal("sync", r.Current.Source);
        Assert.Equal(IdentityFactory.Now, r.Current.At);
    }

    [Fact]
    public async Task Same_choice_again_records_nothing()
    {
        var sub = NewUser();
        await Sync(sub, "both", "evenings");

        var again = await Sync(sub, "both", "evenings");

        Assert.False(again.Recorded);
        Assert.Equal("both", again.Current!.Consent);
        Assert.Single(await _f.Consents.HistoryAsync(IdentityFactory.KeyFor(sub), 10, default));
    }

    [Fact]
    public async Task A_change_is_appended_and_history_is_newest_first()
    {
        var sub = NewUser();
        await Sync(sub, "both", "business");
        await Sync(sub, "none", "business");   // withdrew call/text consent

        var history = await As(sub, null, null).GetFromJsonAsync<ConsentEventDto[]>("/api/identity/consent/history");

        Assert.Equal(["none", "both"], history!.Select(h => h.Consent));
    }

    [Theory]
    [InlineData(null, null)]                 // token without the claims (e.g. seeded users)
    [InlineData("fax", "business")]          // unknown value: never stored
    [InlineData("text", "midnight")]
    public async Task Missing_or_unknown_values_are_not_recorded(string? consent, string? hours)
    {
        var sub = NewUser();

        var r = await Sync(sub, consent, hours);

        Assert.False(r.Recorded);
        Assert.Null(r.Current);
    }

    [Fact]
    public async Task Joining_a_store_records_the_sign_up_choice()
    {
        var sub = NewUser();

        var join = await As(sub, "call", "weekends").PostAsJsonAsync("/api/identity/onboarding", new { store = "acme" });

        Assert.Equal(HttpStatusCode.Created, join.StatusCode);
        var e = Assert.Single(await _f.Consents.HistoryAsync(IdentityFactory.KeyFor(sub), 10, default));
        Assert.Equal(("call", "weekends", "signup"), (e.Consent, e.Hours, e.Source));
    }

    [Fact]
    public async Task History_is_only_ever_your_own()
    {
        var mine = NewUser();
        var theirs = NewUser();
        await Sync(theirs, "both", "anytime");

        var history = await As(mine, null, null).GetFromJsonAsync<ConsentEventDto[]>("/api/identity/consent/history");

        Assert.Empty(history!);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().PostAsync("/api/identity/consent/sync", null)).StatusCode);
    }

    [Theory]
    [InlineData("both", "business", "v1", true)]
    [InlineData("text", "business", "v1", false)]
    [InlineData("text", "evenings", "v1", true)]
    [InlineData("text", "business", "v2", true)]   // same answer, new wording: re-recorded
    public void ShouldRecord_compares_choice_and_wording(string consent, string hours, string version, bool expected)
    {
        var latest = new ConsentEvent(1, "u", "text", "business", "v1", "sync", IdentityFactory.Now);
        Assert.Equal(expected, ConsentPolicy.ShouldRecord(latest, consent, hours, version));
    }
}
