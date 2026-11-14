namespace BuilderPro.Quotes.Domain;

/// <summary>
/// 6b: the measure visit re-prices the job with the MEASURED quantities, but at the HELD unit prices
/// (the deposit held them). A line the held quote didn't have (a new option, say) keeps today's price.
/// </summary>
public static class HeldPrices
{
    public static PricedQuote Apply(PricedQuote measured, PricedQuote held)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(held);
        var heldPrice = held.Lines.GroupBy(l => l.Sku, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().UnitPrice, StringComparer.Ordinal);
        var lines = measured.Lines
            .Where(l => l.Unit != GuidedQuoteBuilder.PercentLineUnit)
            .Select(l => new QuoteLineInput(l.Sku, l.Name, l.Unit, l.Quantity, heldPrice.GetValueOrDefault(l.Sku, l.UnitPrice)))
            .ToList();
        // A percentage option (e.g. a protection plan at 6 %) is a percentage of the RE-PRICED job, not its old amount.
        foreach (var p in measured.Lines.Where(l => l.Unit == GuidedQuoteBuilder.PercentLineUnit))
            lines.Add(GuidedQuoteBuilder.PercentLine(p.Sku, p.Name, p.Quantity, lines));
        var result = QuotePricer.Price(new QuoteInput(lines, held.TaxRate));
        return result.Quote ?? throw new InvalidOperationException($"Re-pricing at held prices failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");
    }
}

/// <summary>6b: the measure-visit times a store offers: the next days' fixed start times, in the store's time zone.</summary>
public static class VisitSlots
{
    /// <summary>Starts (UTC) from tomorrow (store time) for <paramref name="days"/> days, skipping closed days.</summary>
    public static IReadOnlyList<DateTimeOffset> Next(
        DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<TimeOnly> times, int days, IReadOnlyCollection<DayOfWeek> closed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var slots = new List<DateTimeOffset>();
        for (var d = 1; d <= days; d++)
        {
            var day = today.AddDays(d);
            if (closed.Contains(day.DayOfWeek)) continue;
            foreach (var t in times.Order())
            {
                var local = day.ToDateTime(t, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local)) continue;   // skipped by a DST change
                slots.Add(new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime());
            }
        }
        return slots;
    }
}
