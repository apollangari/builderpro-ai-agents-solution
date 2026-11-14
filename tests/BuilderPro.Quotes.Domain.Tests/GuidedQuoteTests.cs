using BuilderPro.Quotes.Domain;

namespace BuilderPro.Quotes.Domain.Tests;

public sealed class MeasurementTests
{
    private static Measurement Countertop(Section[] top, Section[]? island = null, Section[]? splash = null) =>
        Measurements.Measure(Measurements.Countertop, new MeasurementInput(Countertop: new CountertopInput(top, island, splash))).Measurement!;

    [Fact]
    public void A_10_ft_run_at_25_in_deep_is_20_83_sq_ft_with_10_ft_of_edge()
    {
        var m = Countertop([new(120, 25)]);
        Assert.Equal(20.84m, m.Values["sqft"]);           // 3000 / 144 = 20.833.. rounded UP to 0.01
        Assert.Equal(10m, m.Values["edgeFt"]);
        Assert.Equal(20.84m, m.ProductQuantity);
    }

    [Fact]
    public void An_l_shape_with_island_and_backsplash_adds_every_part()
    {
        var m = Countertop([new(120, 25), new(96, 25)], island: [new(72, 36)], splash: [new(216, 4)]);
        Assert.Equal(37.50m, m.Values["countertopSqFt"]);   // (3000 + 2400) / 144
        Assert.Equal(18.00m, m.Values["islandSqFt"]);
        Assert.Equal(6.00m, m.Values["backsplashSqFt"]);
        Assert.Equal(61.50m, m.Values["sqft"]);
        Assert.Equal(36m, m.Values["edgeFt"]);              // fronts 216 in + island perimeter 216 in = 432 in
    }

    [Theory]
    [InlineData(3, 25)]      // too narrow
    [InlineData(120, 100)]   // too deep
    public void Impossible_sections_are_named(decimal width, decimal depth)
    {
        var r = Measurements.Measure(Measurements.Countertop, new(Countertop: new CountertopInput([new(width, depth)], null, null)));
        Assert.False(r.Succeeded);
        Assert.Contains(r.Errors, e => e.Code == "invalid_section");
    }

    [Fact]
    public void Countertops_need_at_least_one_section() =>
        Assert.Contains(Measurements.Measure(Measurements.Countertop, new(Countertop: new CountertopInput([], [], null))).Errors,
            e => e.Code == "missing_measurements");

    [Fact]
    public void Flooring_adds_ten_percent_waste_and_rounds_up()
    {
        var m = Measurements.Measure(Measurements.Area, new(Rooms: [new Room(12, 10), new Room(10.5m, 9)])).Measurement!;
        Assert.Equal(214.5m, m.Values["roomSqFt"]);
        Assert.Equal(236m, m.Values["sqft"]);             // 235.95 -> 236
    }

    [Fact]
    public void Roofs_use_the_roof_estimator()
    {
        var m = Measurements.Measure(Measurements.Roof, new(Roof: new RoofInput(2000, 6))).Measurement!;
        Assert.Equal(74m, m.Values["bundles"]);
        Assert.Equal(3m, m.Values["underlaymentRolls"]);
        Assert.Equal("bundles", m.ProductBasis);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(1001)]
    public void Counts_are_whole_numbers_from_1_to_1000(decimal count) =>
        Assert.False(Measurements.Measure(Measurements.Count, new(Count: count)).Succeeded);
}

public sealed class GuidedQuoteBuilderTests
{
    private static Money Usd(decimal a) => new(a, "USD");
    private static readonly GuidedProduct Quartz = new("CT-QTZ", "Quartz", "sqft", Usd(89m));

    private static readonly GuidedGroup Edge = new("edge", "options", "Edge", "single", "edgeFt", true, null,
        [new("EDGE-EASED", "Eased edge", "ft", Usd(0m)), new("EDGE-OGEE", "Ogee edge", "ft", Usd(18m))]);
    private static readonly GuidedGroup Sink = new("sink", "options", "Sink", "quantity", "choice", true, "No sink",
        [new("SINK-DBL", "Double bowl sink", "each", Usd(238m))]);
    private static readonly GuidedGroup Cutouts = new("cutouts", "options", "Cut-outs", "quantity", "choice", false, null,
        [new("CUT-FAUCET", "Faucet hole", "each", Usd(25m), Included: 4)]);
    private static readonly GuidedGroup Plan = new("plan", "protection", "Protection plan", "single", "fixed", true, "No protection plan",
        [new("PLAN-10Y", "10-year protection plan", "each", Usd(399m))]);

    private static Measurement Sqft(decimal sqft, decimal edge) =>
        new("countertop", new Dictionary<string, decimal> { ["sqft"] = sqft, ["edgeFt"] = edge }, "sqft");

    private static GuidedResult Build(Measurement m, params GuidedChoice[] choices) =>
        GuidedQuoteBuilder.Build(new GuidedRequest(Quartz, m, 25m, [Edge, Sink, Cutouts, Plan], choices, 0.0825m));

    [Fact]
    public void A_complete_countertop_quote_prices_every_part_through_the_quote_pricer()
    {
        var r = Build(Sqft(42m, 18m),
            new("edge", "EDGE-OGEE"), new("sink", "SINK-DBL", 1), new("cutouts", "CUT-FAUCET", 5), new("plan", "PLAN-10Y"));

        Assert.True(r.Complete);
        var q = r.Quote!;
        Assert.Equal(["CT-QTZ", "EDGE-OGEE", "SINK-DBL", "CUT-FAUCET", "PLAN-10Y"], q.Lines.Select(l => l.Sku));
        Assert.Equal(["product", "options", "options", "options", "protection"], r.LineGroups);
        Assert.Equal(3738m, q.Lines[0].LineTotal.Amount);   // 42 x 89
        Assert.Equal(324m, q.Lines[1].LineTotal.Amount);    // 18 ft x 18
        Assert.Equal(1m, q.Lines[3].Quantity);              // 5 holes, 4 included
        Assert.Equal(3738m + 324m + 238m + 25m + 399m, q.Subtotal.Amount);
    }

    [Fact]
    public void Below_the_minimum_a_separate_minimum_line_bills_the_difference()
    {
        var r = Build(Sqft(20.84m, 10m), new("edge", "EDGE-EASED"), new("sink", null), new("plan", null));
        // The eased edge is free ($0) but still a line: the customer chose it.
        Assert.Equal(["CT-QTZ", "CT-QTZ-MIN", "EDGE-EASED"], r.Quote!.Lines.Select(l => l.Sku));
        Assert.Equal(4.16m, r.Quote.Lines[1].Quantity);
        Assert.Equal("minimum", r.LineGroups[1]);
    }

    [Fact]
    public void Free_options_and_none_answers_add_no_cost()
    {
        var r = Build(Sqft(42m, 18m), new("edge", "EDGE-EASED"), new("sink", null), new("cutouts", "CUT-FAUCET", 3), new("plan", null));
        Assert.True(r.Complete);
        Assert.Equal(3738m, r.Quote!.Subtotal.Amount);   // eased edge $0; 3 holes all included; no sink; no plan
    }

    [Fact]
    public void Unanswered_required_steps_are_listed_but_the_quote_still_previews()
    {
        var r = Build(Sqft(42m, 18m), new GuidedChoice("edge", "EDGE-EASED"));
        Assert.NotNull(r.Quote);
        Assert.False(r.Complete);
        Assert.Equal(["sink", "plan"], r.Missing);
    }

    [Theory]
    [InlineData("nope", "X", null, "unknown_group")]
    [InlineData("edge", "EDGE-GOLD", null, "unknown_option")]
    [InlineData("edge", null, null, "none_not_allowed")]
    [InlineData("sink", "SINK-DBL", 1.5, "invalid_quantity")]
    [InlineData("sink", "SINK-DBL", null, "invalid_quantity")]
    public void Bad_answers_are_named(string group, string? sku, double? qty, string code)
    {
        var r = Build(Sqft(42m, 18m), new GuidedChoice(group, sku, qty is null ? null : (decimal)qty));
        Assert.Null(r.Quote);
        Assert.Contains(r.Errors, e => e.Code == code);
    }

    [Fact]
    public void A_single_step_takes_one_answer() =>
        Assert.Contains(Build(Sqft(42m, 18m), new("edge", "EDGE-EASED"), new("edge", "EDGE-OGEE")).Errors, e => e.Code == "too_many_choices");
}

public sealed class DepositPolicyTests
{
    [Theory]
    [InlineData(7245.71, 5, 362.29)]   // 362.2855 -> 362.29 (half away from zero)
    [InlineData(2829.66, 5, 141.48)]   // 141.483
    [InlineData(100.00, 5, 5.00)]
    [InlineData(0.10, 5, 0.01)]        // never below a cent
    [InlineData(1000.00, 10, 100.00)]  // a store may set another percent
    public void Five_percent_of_the_total_rounded_to_cents(decimal total, decimal percent, decimal expected) =>
        Assert.Equal(new Money(expected, "USD"), DepositPolicy.AmountFor(new Money(total, "USD"), percent));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void Impossible_percents_are_refused(decimal percent) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DepositPolicy.AmountFor(new Money(100m, "USD"), percent));
}


/// <summary>6b: measured quantities at the held unit prices; visit times in the store's zone.</summary>
public sealed class MeasureVisitTests
{
    private static Money Usd(decimal a) => new(a, "USD");

    private static PricedQuote Priced(decimal taxRate, params (string Sku, decimal Qty, decimal Price)[] lines) =>
        QuotePricer.Price(new QuoteInput([.. lines.Select(l => new QuoteLineInput(l.Sku, l.Sku, "each", l.Qty, Usd(l.Price)))], taxRate)).Quote!;

    [Fact]
    public void Measured_quantities_are_priced_at_the_held_unit_prices_new_lines_at_todays()
    {
        var held = Priced(0.0825m, ("CT-QTZ", 55.5m, 89m), ("EDGE-OGEE", 36m, 18m));
        var today = Priced(0.09m, ("CT-QTZ", 60m, 95m), ("EDGE-OGEE", 38m, 20m), ("CUT-FAUCET", 1m, 25m));

        var measured = HeldPrices.Apply(today, held);

        Assert.Equal(new[] { 89m, 18m, 25m }, measured.Lines.Select(l => l.UnitPrice.Amount));
        Assert.Equal(new[] { 60m, 38m, 1m }, measured.Lines.Select(l => l.Quantity));
        Assert.Equal(0.0825m, measured.TaxRate);   // the held tax rate
        Assert.Equal(Usd(60 * 89m + 38 * 18m + 25m), measured.Subtotal);
    }

    [Fact]
    public void Same_quantities_give_the_same_total_as_the_held_quote()
    {
        var held = Priced(0.0825m, ("CT-QTZ", 55.5m, 89m));
        Assert.Equal(held.Total, HeldPrices.Apply(Priced(0.0825m, ("CT-QTZ", 55.5m, 120m)), held).Total);
    }

    [Fact]
    public void Visit_times_start_tomorrow_in_store_time_and_skip_closed_days()
    {
        var now = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);   // Friday
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");   // CST, UTC-6 in November
        var slots = VisitSlots.Next(now, zone, [new TimeOnly(13, 0), new TimeOnly(9, 0)], 3, [DayOfWeek.Sunday]);

        Assert.Equal(new[]
        {
            new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 14, 19, 0, 0, TimeSpan.Zero),   // Saturday
            new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 19, 0, 0, TimeSpan.Zero),   // Monday
        }, slots);
    }
}
