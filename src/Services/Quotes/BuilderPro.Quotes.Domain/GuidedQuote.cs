namespace BuilderPro.Quotes.Domain;

/// <summary>The chosen product at the ZIP's price (from the catalog, never the caller).</summary>
public sealed record GuidedProduct(string Sku, string Name, string Unit, Money Price);

/// <summary>One option of a group at the ZIP's price. Included = how many come free (e.g. 4 faucet holes).</summary>
public sealed record GuidedOption(string Sku, string Name, string Unit, Money Price, decimal Included = 0);

/// <summary>
/// An option step the store configures (2n-c / 2n-d).
///  Step:      "options" or "protection" (where the wizard shows it; protection is priced the same way).
///  Selection: "single" = one option (or none, when NoneLabel is set);
///             "quantity" = any options, each with a count (sinks, cut-outs).
///  Basis:     where a "single" option's quantity comes from: a measurement value ("edgeFt", "sqft",
///             "underlaymentRolls" ...) or "fixed" (1, e.g. a protection plan). "quantity" groups use the
///             customer's counts.
///  Required:  the customer must answer (pick one, or pick "none" when allowed).
/// </summary>
public sealed record GuidedGroup(
    string Id, string Step, string Label, string Selection, string Basis, bool Required, string? NoneLabel, IReadOnlyList<GuidedOption> Options);

/// <summary>An answer: Sku null = "none" (single groups with a NoneLabel; for quantity groups, "nothing").</summary>
public sealed record GuidedChoice(string Group, string? Sku, decimal? Quantity = null);

public sealed record GuidedRequest(
    GuidedProduct Product, Measurement Measurement, decimal? MinimumQuantity,
    IReadOnlyList<GuidedGroup> Groups, IReadOnlyList<GuidedChoice> Choices, decimal TaxRate);

/// <summary>
/// The priced quote (null when the request is invalid), which group each line belongs to
/// ("product", "minimum", or the option group's step), and the required groups not answered yet.
/// </summary>
public sealed record GuidedResult(PricedQuote? Quote, IReadOnlyList<string> LineGroups, IReadOnlyList<string> Missing, IReadOnlyList<PricingError> Errors)
{
    public bool Complete => Quote is not null && Missing.Count == 0;
}

/// <summary>
/// THE guided quote rules (2n-b..e). Pure. Turns product + measurement + answers into quote lines,
/// then QuotePricer prices them, so a guided quote is priced exactly like any other quote.
///
///   1. Product line: quantity = the measurement's product basis (e.g. sqft).
///   2. Minimum: when the store has a minimum and the quantity is below it, a separate
///      "Minimum charge" line bills the difference at the product's price (shown, never hidden).
///   3. Single groups: the chosen option x the basis quantity (a measurement value, or 1 for "fixed").
///      Zero quantity (e.g. no island, no edge) adds no line.
///   4. Quantity groups: each option x (count - included), when above zero. Counts are whole numbers 0..100.
///   5. Required groups without an answer are listed in Missing; the quote still previews, but can't be saved.
///   7. Percentage options (unit "percent", e.g. a protection plan at 6 % of the job): priced LAST, on the sum
///      of every other line, as one line with unit "%", quantity = the percentage and unit price = the job / 100,
///      so its total is exactly that percentage, rounded to the cent like any line.
///   6. Answers must name a known group and one of its options; "none" only where the group allows it;
///      at most one answer per single group, one per option in a quantity group.
/// </summary>
public static class GuidedQuoteBuilder
{
    public const string Single = "single";
    public const string QuantitySelection = "quantity";
    public const string Fixed = "fixed";
    /// <summary>An option priced as a percentage of the rest of the job (its price is the percentage).</summary>
    public const string PercentUnit = "percent";
    /// <summary>The unit of the resulting quote line: quantity = the percentage.</summary>
    public const string PercentLineUnit = "%";
    public const decimal MaxOptionCount = 100m;

    public static GuidedResult Build(GuidedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<PricingError>();
        var groups = request.Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var lines = new List<QuoteLineInput>();
        var lineGroups = new List<string>();

        void Add(string group, QuoteLineInput line)
        {
            if (line.Quantity <= 0) return;
            lines.Add(line);
            lineGroups.Add(group);
        }

        // 1 + 2: product and minimum.
        var p = request.Product;
        var measured = request.Measurement.ProductQuantity;
        Add("product", new QuoteLineInput(p.Sku, p.Name, p.Unit, measured, p.Price));
        if (request.MinimumQuantity is decimal min && measured < min)
        {
            var unit = p.Unit == "sqft" ? "sq ft" : p.Unit;
            Add("minimum", new QuoteLineInput($"{p.Sku}-MIN", $"Minimum charge ({min:0.##} {unit} minimum)", p.Unit, min - measured, p.Price));
        }

        // 6: shape of the answers.
        foreach (var c in request.Choices)
        {
            if (!groups.TryGetValue(c.Group, out var g)) { errors.Add(new("unknown_group", $"'{c.Group}' is not an option step for this product.")); continue; }
            if (c.Sku is null)
            {
                if (g.Selection == Single && g.NoneLabel is null) errors.Add(new("none_not_allowed", $"{g.Label}: choose one of the options."));
                if (c.Quantity is not null && c.Quantity != 0) errors.Add(new("invalid_choice", $"{g.Label}: 'none' has no quantity."));
                continue;
            }
            if (!g.Options.Any(o => o.Sku == c.Sku)) errors.Add(new("unknown_option", $"{g.Label}: '{c.Sku}' is not one of its options."));
            if (g.Selection == QuantitySelection &&
                (c.Quantity is null || c.Quantity < 0 || c.Quantity > MaxOptionCount || c.Quantity != decimal.Truncate(c.Quantity.Value)))
                errors.Add(new("invalid_quantity", $"{g.Label}: enter a whole number from 0 to {MaxOptionCount}."));
        }
        foreach (var dup in request.Choices.GroupBy(c => c.Group).Where(x => groups.ContainsKey(x.Key)))
        {
            var g = groups[dup.Key];
            if (g.Selection == Single && dup.Count() > 1) errors.Add(new("too_many_choices", $"{g.Label}: choose only one."));
            if (g.Selection == QuantitySelection && dup.Where(c => c.Sku is not null).GroupBy(c => c.Sku).Any(s => s.Count() > 1))
                errors.Add(new("duplicate_option", $"{g.Label}: each option once, with its count."));
        }
        if (errors.Count > 0) return new(null, [], [], errors);

        // 3 + 4: option lines, in the store's group order. Percentage options wait for the rest (7).
        var percent = new List<(string Step, GuidedOption Option)>();
        foreach (var g in request.Groups)
        {
            foreach (var c in request.Choices.Where(c => c.Group == g.Id && c.Sku is not null))
            {
                var o = g.Options.First(x => x.Sku == c.Sku);
                if (o.Unit == PercentUnit)
                {
                    percent.Add((g.Step, o));
                    continue;
                }
                decimal qty;
                if (g.Selection == QuantitySelection) qty = Math.Max(0, c.Quantity!.Value - o.Included);
                else if (g.Basis == Fixed) qty = 1;
                else if (request.Measurement.Values.TryGetValue(g.Basis, out var v)) qty = v;
                else { errors.Add(new("unknown_basis", $"{g.Label}: nothing measured as '{g.Basis}'.")); continue; }

                var name = o.Included > 0 ? $"{o.Name} ({o.Included:0.##} included)" : o.Name;
                Add(g.Step, new QuoteLineInput(o.Sku, name, o.Unit, qty, o.Price));
            }
        }
        if (errors.Count > 0) return new(null, [], [], errors);

        // 7: percentage options on the rest of the job.
        foreach (var (step, o) in percent)
        {
            if (o.Price.Amount <= 0 || o.Price.Amount > 100)
            {
                errors.Add(new("invalid_percentage", $"{o.Name}: a percentage between 0 and 100."));
                continue;
            }
            Add(step, PercentLine(o.Sku, $"{o.Name} ({o.Price.Amount:0.##}% of the job)", o.Price.Amount, lines));
        }
        if (errors.Count > 0) return new(null, [], [], errors);

        // 5: what's still to answer.
        var answered = request.Choices.Select(c => c.Group).ToHashSet(StringComparer.Ordinal);
        var missing = request.Groups.Where(g => g.Required && !answered.Contains(g.Id)).Select(g => g.Id).ToArray();

        var priced = QuotePricer.Price(new QuoteInput(lines, request.TaxRate));
        return priced.Succeeded
            ? new(priced.Quote, lineGroups, missing, [])
            : new(null, [], missing, priced.Errors);
    }

    /// <summary>pct % of the given lines (each rounded to the cent, as the pricer does), as one quote line.</summary>
    public static QuoteLineInput PercentLine(string sku, string name, decimal pct, IEnumerable<QuoteLineInput> others)
    {
        var job = others.Where(l => l.Unit != PercentLineUnit).Aggregate(0m, (sum, l) => sum + Rounding.ToMinorUnit(l.UnitPrice * l.Quantity).Amount);
        var currency = others.First().UnitPrice.Currency;
        return new QuoteLineInput(sku, name, PercentLineUnit, pct, new Money(job / 100m, currency));
    }
}
