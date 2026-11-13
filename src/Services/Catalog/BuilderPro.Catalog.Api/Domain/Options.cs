namespace BuilderPro.Catalog.Api.Domain;

/// <summary>One choice inside an option step, e.g. "Ogee edge" or "Double bowl sink". Priced per zone like products.</summary>
public sealed record OptionItem(string Sku, string Name, string Unit, decimal ListPrice, string? ImageUrl = null, decimal Included = 0);

/// <summary>
/// An option step a store configures for a category (2n-c add-ons, 2n-d protection plans).
/// The rules that use these fields live in Quotes.Domain/GuidedQuote.cs; the catalog only stores
/// and serves them.
///  Step:       "options" | "protection"
///  Selection:  "single" | "quantity"
///  Basis:      a measurement value ("edgeFt", "sqft", ...), "fixed", or "choice" (quantity groups)
///  AppliesAttribute/Values: only for products whose attribute is one of the values
///              (e.g. protection plans for material = quartz, granite)
/// </summary>
public sealed record OptionGroup(
    string TenantId, string Category, string GroupId, string Step, string Label, string? Help,
    string Selection, string Basis, bool Required, string? NoneLabel,
    string? AppliesAttribute, IReadOnlyList<string>? AppliesValues, int Sort, IReadOnlyList<OptionItem> Options)
{
    public bool AppliesTo(Product p) =>
        AppliesAttribute is null || (p.AttributeValues.TryGetValue(AppliesAttribute, out var v) && (AppliesValues ?? []).Contains(v, StringComparer.Ordinal));

    public static readonly IReadOnlyList<string> Steps = ["options", "protection"];
    public static readonly IReadOnlyList<string> Selections = ["single", "quantity"];

    /// <summary>Every problem with a group definition (empty = valid). Used by the dev seed; the admin API later.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Categories.Find(Category) is null) errors.Add($"unknown category '{Category}'");
        if (!Steps.Contains(Step)) errors.Add($"step must be one of {string.Join(", ", Steps)}");
        if (!Selections.Contains(Selection)) errors.Add($"selection must be one of {string.Join(", ", Selections)}");
        if (Selection == "quantity" && Basis != "choice") errors.Add("quantity groups use basis 'choice'");
        if (Selection == "single" && Basis == "choice") errors.Add("single groups use a measurement value or 'fixed' as basis");
        if (Options.Count == 0) errors.Add("a group needs at least one option");
        if (Options.Select(o => o.Sku).Distinct(StringComparer.Ordinal).Count() != Options.Count) errors.Add("option SKUs repeat");
        if (Options.Any(o => o.ListPrice < 0 || o.Included < 0)) errors.Add("prices and included counts are >= 0");
        if ((AppliesAttribute is null) != (AppliesValues is null or { Count: 0 })) errors.Add("AppliesAttribute and AppliesValues go together");
        return errors;
    }
}

/// <summary>A store's minimum for a category, in the category's unit (e.g. 25 sq ft of countertop).</summary>
public sealed record CategoryRule(string TenantId, string Category, decimal MinimumQuantity);

public interface IOptionStore
{
    Task InitializeAsync(CancellationToken ct);

    /// <summary>Option steps of ONE tenant for a category, in display order.</summary>
    Task<IReadOnlyList<OptionGroup>> ListGroupsAsync(string tenantId, string category, CancellationToken ct);

    Task<decimal?> MinimumAsync(string tenantId, string category, CancellationToken ct);

    Task UpsertGroupAsync(OptionGroup group, CancellationToken ct);
    Task UpsertRuleAsync(CategoryRule rule, CancellationToken ct);
}
