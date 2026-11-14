using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuilderPro.Quotes.Api;

/// <summary>Where the customer pays a deposit, and the provider's reference for it.</summary>
public sealed record CheckoutStart(string ProviderRef, string Url, DateTimeOffset ExpiresAt);

/// <summary>What the provider says about a checkout (reconciliation). Paid carries the amount actually taken.</summary>
public enum ProviderOutcome { Open, Paid, Failed }
public sealed record ProviderStatus(ProviderOutcome Outcome, long? AmountMinor = null, string? Currency = null, string? PaymentRef = null, string? Detail = null);

/// <summary>The provider said NO (a 4xx): a confirmed failure, unlike a timeout or a 5xx (unknown).</summary>
public sealed class PaymentRefusedException(string message) : Exception(message);

/// <summary>
/// 6d: the payment provider seam. "simulated" (development and tests) is our own test-mode page;
/// "stripe" is Stripe Checkout, confirmed ONLY by Stripe's signed webhook, never by the browser.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }
    /// <summary>Opens a checkout. Throws PaymentRefusedException for a refusal; anything else (timeout, 5xx) leaves the outcome unknown.</summary>
    Task<CheckoutStart> StartAsync(Deposit deposit, int quoteNumber, string returnBaseUrl, CancellationToken ct);

    /// <summary>Reconciliation: asks the provider what happened to this deposit's checkout.</summary>
    Task<ProviderStatus> LookupAsync(Deposit deposit, CancellationToken ct);
}

public sealed class SimulatedPayments(TimeProvider clock) : IPaymentProvider
{
    public string Name => DepositEndpoints.Simulated;

    public Task<CheckoutStart> StartAsync(Deposit deposit, int quoteNumber, string returnBaseUrl, CancellationToken ct) =>
        Task.FromResult(new CheckoutStart($"sim_{deposit.Id:N}", $"/pay/{deposit.Id}", clock.GetUtcNow().AddDays(1)));

    /// <summary>The test page decides directly, so a lookup only notices an expired checkout.</summary>
    public Task<ProviderStatus> LookupAsync(Deposit deposit, CancellationToken ct) =>
        Task.FromResult(deposit.CheckoutExpiresAt is DateTimeOffset e && e <= clock.GetUtcNow()
            ? new ProviderStatus(ProviderOutcome.Failed, Detail: "checkout expired")
            : new ProviderStatus(ProviderOutcome.Open));
}

public sealed class StripeOptions
{
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.stripe.com";

    /// <summary>Test keys only outside Production: a live key in development would charge real cards.</summary>
    public void Validate(bool production)
    {
        if (string.IsNullOrWhiteSpace(SecretKey) || string.IsNullOrWhiteSpace(WebhookSecret))
            throw new InvalidOperationException("Payments:Provider is stripe but Payments:Stripe:SecretKey / WebhookSecret are missing.");
        if (!production && !SecretKey.StartsWith("sk_test_", StringComparison.Ordinal) && !SecretKey.StartsWith("rk_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Only Stripe TEST keys (sk_test_ / rk_test_) are allowed outside Production.");
    }
}

/// <summary>Stripe Checkout Sessions over Stripe's REST API (form-encoded). One session per deposit attempt.</summary>
public sealed class StripePayments(HttpClient http, StripeOptions options, ILogger<StripePayments> log) : IPaymentProvider
{
    public string Name => "stripe";

    public async Task<CheckoutStart> StartAsync(Deposit deposit, int quoteNumber, string returnBaseUrl, CancellationToken ct)
    {
        var form = StripeForm.CheckoutSession(deposit, quoteNumber, returnBaseUrl.TrimEnd('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/v1/checkout/sessions") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
        // Same deposit -> same session on a retry (Stripe keeps idempotency keys for 24 h).
        // One key per checkout attempt: a retry after a timeout gets the SAME session, never a second one.
        request.Headers.Add("Idempotency-Key", $"deposit-{deposit.Id:N}-{deposit.CheckoutExpiresAt?.ToUnixTimeSeconds() ?? 0}");
        using var response = await http.SendAsync(request, ct);
        if ((int)response.StatusCode is >= 400 and < 500 and not 409 and not 429)
        {
            log.LogWarning("Stripe refused the checkout session ({Status})", (int)response.StatusCode);
            throw new PaymentRefusedException($"Stripe refused the checkout ({(int)response.StatusCode}).");
        }
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Stripe answered {(int)response.StatusCode}");
        var session = await response.Content.ReadFromJsonAsync<SessionCreated>(ct) ?? throw new HttpRequestException("Stripe returned no session");
        return new CheckoutStart(session.Id, session.Url, DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt));
    }

    public async Task<ProviderStatus> LookupAsync(Deposit deposit, CancellationToken ct)
    {
        if (deposit.ProviderRef is null) return new ProviderStatus(ProviderOutcome.Open, Detail: "no session yet");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/v1/checkout/sessions/{Uri.EscapeDataString(deposit.ProviderRef)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Stripe answered {(int)response.StatusCode}");
        var s = await response.Content.ReadFromJsonAsync<StripeSession>(ct) ?? throw new HttpRequestException("Stripe returned no session");
        return StripeOutcome.Of(s);
    }

    private sealed record SessionCreated(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("expires_at")] long ExpiresAt);
}

/// <summary>Reads a Stripe checkout session into our outcome (pure, tested).</summary>
public static class StripeOutcome
{
    public static ProviderStatus Of(StripeSession s) => (s.Status, s.PaymentStatus) switch
    {
        ("complete", "paid") or (_, "no_payment_required") => new(ProviderOutcome.Paid, s.AmountTotal, s.Currency, s.PaymentIntent ?? s.Id),
        ("expired", _) => new(ProviderOutcome.Failed, Detail: "checkout expired"),
        _ => new(ProviderOutcome.Open, Detail: $"{s.Status}/{s.PaymentStatus}"),
    };
}

/// <summary>The checkout session request, pure so it can be tested without Stripe.</summary>
public static class StripeForm
{
    public static IReadOnlyList<KeyValuePair<string, string>> CheckoutSession(Deposit d, int quoteNumber, string returnBase) =>
    [
        new("mode", "payment"),
        new("client_reference_id", d.Id.ToString()),
        new("line_items[0][quantity]", "1"),
        new("line_items[0][price_data][currency]", d.Currency.ToLowerInvariant()),
        new("line_items[0][price_data][unit_amount]", MinorUnits(d.Amount).ToString(CultureInfo.InvariantCulture)),
        new("line_items[0][price_data][product_data][name]", d.Kind == PaymentKinds.Balance
            ? $"Balance for quote Q-{quoteNumber}" : $"Deposit for quote Q-{quoteNumber} ({d.Percent:0.##}%)"),
        new("metadata[deposit_id]", d.Id.ToString()),
        new("metadata[tenant_id]", d.TenantId),
        new("payment_intent_data[metadata][deposit_id]", d.Id.ToString()),
        new("success_url", $"{returnBase}/pay/{d.Id}?return=stripe"),
        new("cancel_url", $"{returnBase}/projects/{d.ProjectId}"),
    ];

    /// <summary>USD and CAD have 2 decimals; Stripe takes integer cents.</summary>
    public static long MinorUnits(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Stripe webhook signatures: header "Stripe-Signature: t=TIMESTAMP,v1=HEX[,v1=HEX...]"; the signed text
/// is "TIMESTAMP.RAW_BODY", HMAC-SHA256 with the endpoint's signing secret. Old timestamps are refused
/// (replays), and comparison is constant-time.
/// </summary>
public static class StripeSignature
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static bool IsValid(string? header, string body, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret)) return false;
        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) timestamp = t;
            else if (kv[0].Trim() == "v1") signatures.Add(kv[1].Trim());
        }
        if (timestamp is null || signatures.Count == 0) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).Duration() > Tolerance) return false;

        var expected = Sign(timestamp.Value, body, secret);
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
    }

    public static string Sign(long timestamp, string body, string secret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}

/// <summary>The parts of a Stripe event we read. Everything else is ignored.</summary>
public sealed record StripeEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] StripeEventData Data)
{
    public static StripeEvent? Parse(string body)
    {
        try { return JsonSerializer.Deserialize<StripeEvent>(body); }
        catch (JsonException) { return null; }
    }
}

public sealed record StripeEventData([property: JsonPropertyName("object")] StripeSession Object);

public sealed record StripeSession(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("payment_status")] string? PaymentStatus,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("amount_total")] long? AmountTotal,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("payment_intent")] string? PaymentIntent,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata);
