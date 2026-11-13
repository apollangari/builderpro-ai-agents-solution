namespace BuilderPro.Quotes.Api;

/// <summary>
/// One customer job (2n-f): where it is, what they chose and measured, how far it got.
/// It stores ANSWERS only (the guided request: sku, measurements, choices). Prices are never stored
/// here: the workspace re-prices the answers on every read, and an issued quote is the snapshot.
/// </summary>
public sealed record Project(
    Guid Id,
    string TenantId,
    string? CustomerId,
    string CreatedBy,
    string Title,
    string Zip,
    string? Sku,
    string AnswersJson,      // {"measurements": {...}, "choices": [...]} as the customer/concierge gave them
    string Stage,            // ProjectStages
    Guid? QuoteId,           // the latest issued quote
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SalespersonKey = null,    // 1i: prepared by a salesperson for the customer; their sale (commission)
    string? SalespersonName = null);

/// <summary>The stage rail. Deposit, measure visit and install arrive with 6a-6c.</summary>
public static class ProjectStages
{
    public const string Discover = "discover";     // ZIP known, no product yet
    public const string Configure = "configure";   // product chosen; measuring and answering
    public const string Quote = "quote";           // a numbered quote was issued
    public const string Deposit = "deposit";       // the customer accepted it; the 5 % deposit is due (6a)
    public const string Measure = "measure";       // deposit paid: price held, measure visit next (6b)
    public const string Confirm = "confirm";       // measured: the customer confirms the measured quote (6b)
    public const string Install = "install";       // installation in progress (6c)
    public const string Done = "done";             // installed (6c)
    public static readonly IReadOnlyList<string> All = [Discover, Configure, Quote, Deposit, Measure, Confirm, Install, Done];

    /// <summary>Once the deposit is paid the answers are the order: only the measure visit changes them (6b).</summary>
    public static bool IsLocked(string stage) => stage is Measure or Confirm or Install or Done;
}

public interface IProjectStore
{
    Task InitializeAsync(CancellationToken ct);
    Task CreateAsync(Project project, CancellationToken ct);

    /// <summary>One project of ONE tenant; with a customerId, only that customer's.</summary>
    Task<Project?> FindAsync(string tenantId, string? customerId, Guid id, CancellationToken ct);

    /// <summary>Newest activity first. With a customerId, only theirs; without (staff), the tenant's.</summary>
    Task<IReadOnlyList<Project>> ListAsync(string tenantId, string? customerId, int limit, CancellationToken ct);

    Task UpdateAsync(Project project, CancellationToken ct);
}
