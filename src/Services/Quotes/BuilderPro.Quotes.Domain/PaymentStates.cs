namespace BuilderPro.Quotes.Domain;

/// <summary>
/// 6d (owner): the payment state machine.
///
///   created --> processing --> succeeded            (confirmed by the provider)
///                          --> failed               (confirmed: declined, refused, checkout expired)
///                          --> unknown              (timeout: we don't know what the provider did)
///   unknown --> (reconciliation: status lookup or webhook) --> succeeded | failed | processing (still open)
///   created --> failed                              (nothing was ever sent)
///
/// Succeeded and failed are final. Only the provider's answer (signed webhook or our own status lookup)
/// moves a payment to succeeded; the browser never does. Every move is recorded with its source.
/// </summary>
public static class PaymentStates
{
    public const string Created = "created";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public static readonly string[] All = [Created, Processing, Succeeded, Failed, Unknown];

    private static readonly Dictionary<string, string[]> Moves = new(StringComparer.Ordinal)
    {
        [Created] = [Processing, Failed],
        [Processing] = [Succeeded, Failed, Unknown],
        [Unknown] = [Succeeded, Failed, Processing],   // reconciliation
        [Succeeded] = [],
        [Failed] = [],
    };

    public static bool CanMove(string from, string to) => Moves.TryGetValue(from, out var next) && next.Contains(to, StringComparer.Ordinal);

    public static bool IsFinal(string state) => state is Succeeded or Failed;

    /// <summary>Still open with the provider: reconciliation may change it.</summary>
    public static bool NeedsAnswer(string state) => state is Processing or Unknown;
}

/// <summary>Where a payment move came from (the audit trail says who decided).</summary>
public static class PaymentSources
{
    public const string Checkout = "checkout";      // we opened the provider's checkout
    public const string Simulated = "simulated";    // our test-mode page (development and tests only)
    public const string Webhook = "webhook";        // the provider's signed notification
    public const string Lookup = "lookup";          // reconciliation: we asked the provider
}
