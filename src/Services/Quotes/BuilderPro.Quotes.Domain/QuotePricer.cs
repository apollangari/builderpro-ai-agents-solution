namespace BuilderPro.Quotes.Domain;

/// <summary>One line of a quote request. UnitPrice comes from the CATALOG, never from the caller's imagination.</summary>
public sealed record QuoteLineInput(string Sku, string Name, string Unit, decimal Quantity, Money UnitPrice);

/// <summary>What to price. TaxRate is a fraction: 0.0825 means 8.25 %.</summary>
public sealed record QuoteInput(IReadOnlyList<QuoteLineInput> Lines, decimal TaxRate);

public sealed record PricedLine(string Sku, string Name, string Unit, decimal Quantity, Money UnitPrice, Money LineTotal);

public sealed record PricedQuote(IReadOnlyList<PricedLine> Lines, Money Subtotal, decimal TaxRate, Money Tax, Money Total);

/// <summary>A rule the input broke. Code is stable (clients and agents branch on it); Line is 0-based, or null for the whole quote.</summary>
public sealed record PricingError(string Code, string Message, int? Line = null);

/// <summary>Either a priced quote or the full list of what's wrong (never just the first problem).</summary>
public sealed record PricingResult(PricedQuote? Quote, IReadOnlyList<PricingError> Errors)
{
    public bool Succeeded => Quote is not null;
}

/// <summary>
/// THE pricing rules. Pure: no I/O, no clock, no randomness, so the same input always gives the
/// same price, and every rule below is a unit test.
///
///   1. A quote has 1..MaxLines lines; each SKU appears at most once.
///   2. Quantity is &gt; 0, at most MaxQuantity, with at most QuantityDecimals decimals (m2, m, litres).
///   3. Unit prices are &gt;= 0 and every line uses the same currency.
///   4. Line total = quantity x unit price, rounded to cents PER LINE (what's printed is what's summed).
///   5. Subtotal = sum of the rounded line totals.
///   6. Tax = subtotal x tax rate, rounded ONCE at the end (not per line).
///   7. Total = subtotal + tax.
/// </summary>
public static class QuotePricer
{
    public const int MaxLines = 100;
    public const decimal MaxQuantity = 100_000m;
    public const int QuantityDecimals = 3;
    public const decimal MaxTaxRate = 0.5m;

    public static PricingResult Price(QuoteInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = Validate(input);
        if (errors.Count > 0) return new PricingResult(null, errors);

        var currency = input.Lines[0].UnitPrice.Currency;
        var lines = input.Lines
            .Select(l => new PricedLine(l.Sku, l.Name, l.Unit, l.Quantity, l.UnitPrice, Rounding.ToMinorUnit(l.UnitPrice * l.Quantity)))
            .ToArray();

        var subtotal = lines.Aggregate(Money.Zero(currency), (sum, l) => sum + l.LineTotal);
        var tax = Rounding.ToMinorUnit(subtotal * input.TaxRate);

        return new PricingResult(new PricedQuote(lines, subtotal, input.TaxRate, tax, subtotal + tax), []);
    }

    private static List<PricingError> Validate(QuoteInput input)
    {
        var errors = new List<PricingError>();
        var lines = input.Lines ?? [];

        if (lines.Count == 0) errors.Add(new("no_lines", "A quote needs at least one line."));
        if (lines.Count > MaxLines) errors.Add(new("too_many_lines", $"A quote has at most {MaxLines} lines."));
        if (input.TaxRate < 0 || input.TaxRate > MaxTaxRate) errors.Add(new("invalid_tax_rate", $"Tax rate must be between 0 and {MaxTaxRate}."));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? currency = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (string.IsNullOrWhiteSpace(l.Sku)) errors.Add(new("missing_sku", "Every line needs a SKU.", i));
            else if (!seen.Add(l.Sku)) errors.Add(new("duplicate_sku", $"{l.Sku} appears more than once; change its quantity instead.", i));

            if (l.Quantity <= 0) errors.Add(new("invalid_quantity", "Quantity must be greater than zero.", i));
            else if (l.Quantity > MaxQuantity) errors.Add(new("invalid_quantity", $"Quantity must be at most {MaxQuantity}.", i));
            else if (l.Quantity.Scale > QuantityDecimals && decimal.Round(l.Quantity, QuantityDecimals) != l.Quantity)
                errors.Add(new("invalid_quantity", $"Quantity has at most {QuantityDecimals} decimals.", i));

            if (l.UnitPrice.Amount < 0) errors.Add(new("invalid_price", "Unit price cannot be negative.", i));

            currency ??= l.UnitPrice.Currency;
            if (l.UnitPrice.Currency != currency) errors.Add(new("mixed_currency", $"All lines must be in {currency}.", i));
        }

        return errors;
    }
}
