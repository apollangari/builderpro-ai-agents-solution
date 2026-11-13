using BuilderPro.Quotes.Domain;

namespace BuilderPro.Quotes.Domain.Tests;

public sealed class QuotePricerTests
{
    private static Money Usd(decimal amount) => new(amount, "USD");

    private static QuoteLineInput Line(string sku, decimal qty, decimal price, string currency = "USD") =>
        new(sku, sku, "each", qty, new Money(price, currency));

    private static PricedQuote PriceOk(decimal taxRate, params QuoteLineInput[] lines)
    {
        var result = QuotePricer.Price(new QuoteInput(lines, taxRate));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Code)));
        return result.Quote!;
    }

    private static IReadOnlyList<PricingError> PriceFails(decimal taxRate, params QuoteLineInput[] lines)
    {
        var result = QuotePricer.Price(new QuoteInput(lines, taxRate));
        Assert.False(result.Succeeded);
        Assert.Null(result.Quote);
        return result.Errors;
    }

    // ---------- Totals ----------

    [Fact]
    public void Prices_a_simple_quote()
    {
        var q = PriceOk(0.0825m, Line("PAINT", 3, 34.98m), Line("LABOR", 2.5m, 65.00m));

        Assert.Equal(Usd(104.94m), q.Lines[0].LineTotal);   // 3 x 34.98
        Assert.Equal(Usd(162.50m), q.Lines[1].LineTotal);   // 2.5 x 65.00
        Assert.Equal(Usd(267.44m), q.Subtotal);
        Assert.Equal(Usd(22.06m), q.Tax);                   // 267.44 x 0.0825 = 22.0638 -> 22.06
        Assert.Equal(Usd(289.50m), q.Total);
    }

    [Fact]
    public void Zero_tax_means_total_equals_subtotal()
    {
        var q = PriceOk(0m, Line("A", 1, 10m));
        Assert.Equal(Usd(0m), q.Tax);
        Assert.Equal(q.Subtotal, q.Total);
    }

    [Fact]
    public void Free_items_are_allowed()
    {
        var q = PriceOk(0.1m, Line("SAMPLE", 1, 0m), Line("A", 1, 10m));
        Assert.Equal(Usd(11.00m), q.Total);
    }

    [Fact]
    public void Lines_keep_their_order_and_details()
    {
        var q = PriceOk(0m, new QuoteLineInput("TILE", "Ceramic tile", "m2", 12.345m, Usd(21.50m)), Line("A", 1, 1m));
        Assert.Equal(["TILE", "A"], q.Lines.Select(l => l.Sku));
        Assert.Equal("m2", q.Lines[0].Unit);
        Assert.Equal(12.345m, q.Lines[0].Quantity);
    }

    [Fact]
    public void Prices_a_roof_with_trade_units_and_fractional_squares()
    {
        // A 24.5-square roof: shingles by the bundle, underlayment by the roll, labour per square.
        var q = PriceOk(0.0825m,
            new QuoteLineInput("ROOF-SHINGLE-ARCH", "Architectural shingles", "bundle", 74, Usd(42.97m)),
            new QuoteLineInput("ROOF-UNDERLAY-SYN", "Synthetic underlayment", "roll", 3, Usd(89.00m)),
            new QuoteLineInput("ROOF-DRIP-EDGE-10", "Drip edge 10 ft", "each", 18, Usd(9.48m)),
            new QuoteLineInput("ROOF-LABOR-SQ", "Tear-off and install", "square", 24.5m, Usd(185.00m)));

        Assert.Equal(Usd(3179.78m), q.Lines[0].LineTotal);   // 74 x 42.97
        Assert.Equal(Usd(4532.50m), q.Lines[3].LineTotal);   // 24.5 x 185.00
        Assert.Equal(Usd(8149.92m), q.Subtotal);
        Assert.Equal(Usd(672.37m), q.Tax);                    // 8149.92 x 0.0825 = 672.3684
        Assert.Equal(Usd(8822.29m), q.Total);
    }

    // ---------- Rounding: one rule, one place ----------

    [Fact]
    public void Line_totals_round_half_away_from_zero_not_bankers()
    {
        // 0.25 x 0.50 = 0.125: banker's rounding would give 0.12, commercial gives 0.13.
        var q = PriceOk(0m, Line("A", 0.25m, 0.50m));
        Assert.Equal(Usd(0.13m), q.Lines[0].LineTotal);
    }

    [Fact]
    public void Rounds_each_line_then_sums_so_the_printed_lines_add_up()
    {
        // Each line is 1/3 x 1.00 = 0.3333... -> 0.33. Three printed lines of 0.33 must total 0.99,
        // not 1.00 (which is what rounding the unrounded sum would give).
        var q = PriceOk(0m, Line("A", 0.333m, 1m), Line("B", 0.333m, 1m), Line("C", 0.333m, 1m));
        Assert.All(q.Lines, l => Assert.Equal(Usd(0.33m), l.LineTotal));
        Assert.Equal(Usd(0.99m), q.Total);
    }

    [Fact]
    public void Tax_is_rounded_once_on_the_subtotal_not_per_line()
    {
        // Per-line tax: 10 x round(0.05 x 0.10)=round(0.005)=0.01 -> 0.10. Once: round(0.50 x 0.10)=0.05.
        var lines = Enumerable.Range(0, 10).Select(i => Line($"L{i}", 1, 0.05m)).ToArray();
        var q = PriceOk(0.10m, lines);
        Assert.Equal(Usd(0.50m), q.Subtotal);
        Assert.Equal(Usd(0.05m), q.Tax);
    }

    [Fact]
    public void Same_input_gives_the_same_price_every_time()
    {
        var input = new QuoteInput([Line("A", 1.234m, 9.99m), Line("B", 7, 0.01m)], 0.0725m);
        var first = QuotePricer.Price(input).Quote!;
        for (var i = 0; i < 50; i++)
        {
            var again = QuotePricer.Price(input).Quote!;
            Assert.Equal(first.Total, again.Total);
            Assert.Equal(first.Tax, again.Tax);
        }
    }

    [Fact]
    public void Large_quotes_keep_full_precision()
    {
        var q = PriceOk(0.2m, Line("A", 100_000m, 99_999.99m));
        Assert.Equal(Usd(9_999_999_000.00m), q.Subtotal);
        Assert.Equal(Usd(11_999_998_800.00m), q.Total);
    }

    // ---------- Invalid input: reported, never "fixed" silently ----------

    [Fact]
    public void Empty_quote_is_refused()
    {
        var errors = PriceFails(0m);
        Assert.Contains(errors, e => e.Code == "no_lines");
    }

    [Fact]
    public void Too_many_lines_is_refused()
    {
        var lines = Enumerable.Range(0, QuotePricer.MaxLines + 1).Select(i => Line($"L{i}", 1, 1m)).ToArray();
        Assert.Contains(PriceFails(0m, lines), e => e.Code == "too_many_lines");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000.001)]
    [InlineData(1.2345)]       // 4 decimals
    public void Bad_quantities_are_refused(double qty)
    {
        var errors = PriceFails(0m, Line("A", (decimal)qty, 1m));
        var e = Assert.Single(errors);
        Assert.Equal("invalid_quantity", e.Code);
        Assert.Equal(0, e.Line);
    }

    [Fact]
    public void Trailing_zero_decimals_are_fine()
    {
        // 2.50000 has scale 5 but is 2.5: that's a formatting detail, not a fourth decimal.
        PriceOk(0m, Line("A", 2.50000m, 1m));
    }

    [Fact]
    public void Negative_price_is_refused()
    {
        Assert.Contains(PriceFails(0m, Line("A", 1, -0.01m)), e => e.Code == "invalid_price");
    }

    [Fact]
    public void Duplicate_sku_is_refused_on_the_second_line()
    {
        var e = Assert.Single(PriceFails(0m, Line("A", 1, 1m), Line("A", 2, 1m)));
        Assert.Equal("duplicate_sku", e.Code);
        Assert.Equal(1, e.Line);
    }

    [Fact]
    public void Missing_sku_is_refused()
    {
        Assert.Contains(PriceFails(0m, Line(" ", 1, 1m)), e => e.Code == "missing_sku");
    }

    [Fact]
    public void Mixed_currencies_are_refused()
    {
        var e = Assert.Single(PriceFails(0m, Line("A", 1, 1m, "USD"), Line("B", 1, 1m, "EUR")));
        Assert.Equal("mixed_currency", e.Code);
        Assert.Equal(1, e.Line);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(0.51)]
    public void Out_of_range_tax_rate_is_refused(double rate)
    {
        Assert.Contains(PriceFails((decimal)rate, Line("A", 1, 1m)), e => e.Code == "invalid_tax_rate");
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var errors = PriceFails(-1m, Line("A", 0, -1m), Line("A", 1, 1m, "EUR"));
        Assert.Equal(
            ["invalid_tax_rate", "invalid_quantity", "invalid_price", "duplicate_sku", "mixed_currency"],
            errors.Select(e => e.Code));
    }
}

public sealed class MoneyTests
{
    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDT")]
    [InlineData("")]
    public void Currency_must_be_an_iso_code(string code)
    {
        Assert.Throws<ArgumentException>(() => new Money(1m, code));
    }

    [Fact]
    public void Adding_different_currencies_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new Money(1m, "USD") + new Money(1m, "EUR"));
    }

    [Fact]
    public void Decimal_arithmetic_is_exact()
    {
        Assert.Equal(new Money(0.3m, "USD"), new Money(0.1m, "USD") + new Money(0.2m, "USD"));
    }

    [Theory]
    [InlineData(0.125, 0.13)]
    [InlineData(0.135, 0.14)]
    [InlineData(-0.125, -0.13)]
    [InlineData(2.004, 2.00)]
    public void Rounding_is_half_away_from_zero(double input, double expected)
    {
        Assert.Equal((decimal)expected, Rounding.ToMinorUnit(new Money((decimal)input, "USD")).Amount);
    }
}
