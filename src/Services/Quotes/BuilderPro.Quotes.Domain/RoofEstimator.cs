namespace BuilderPro.Quotes.Domain;

/// <summary>What the customer (or an agent, later) knows about the roof.</summary>
/// <param name="FootprintSqFt">Area the roof covers seen from above (house + overhangs), in sq ft.</param>
/// <param name="PitchRise">Rise in inches per 12 in of run (a "6/12" roof = 6). 0 = flat.</param>
/// <param name="WastePercent">Extra for cuts, hips and valleys: 10 for a simple gable, 15 for complex roofs.</param>
/// <param name="EavesAndRakesFt">Length of roof edges that get drip edge, in feet (0 = unknown, skip drip edge).</param>
public sealed record RoofInput(decimal FootprintSqFt, decimal PitchRise, decimal WastePercent = 10m, decimal EavesAndRakesFt = 0m);

/// <summary>Quantities to quote. Materials are WHOLE units (you can't buy part of a bundle).</summary>
public sealed record RoofQuantities(
    decimal PitchFactor,       // slope multiplier, e.g. 1.118 for 6/12
    decimal RoofAreaSqFt,      // actual sloped surface, no waste
    decimal Squares,           // (area + waste) / 100, 2 decimals: what labour is priced on
    int ShingleBundles,        // 3 per square, rounded up
    int UnderlaymentRolls,     // 10 squares per roll, rounded up
    int DripEdgePieces);       // 10 ft pieces, rounded up (0 when edges unknown)

public sealed record RoofEstimate(RoofQuantities? Quantities, IReadOnlyList<PricingError> Errors)
{
    public bool Succeeded => Quantities is not null;
}

/// <summary>
/// THE roof take-off rules (2g). Pure: same input, same quantities, every rule a unit test.
/// Quantities, never prices: prices come from the catalog and totals from QuotePricer, so an agent
/// can say "74 bundles" without ever doing arithmetic itself.
///
///   1. Pitch factor = sqrt(1 + (rise/12)^2)            (6/12 -> 1.118)
///   2. Roof area   = footprint x pitch factor
///   3. Squares     = roof area x (1 + waste%) / 100, rounded UP to 0.01
///   4. Bundles     = ceil(squares x 3)                 (3 bundles per square)
///   5. Underlayment rolls = ceil(squares / 10)         (1,000 sq ft per roll)
///   6. Drip edge   = ceil(edges / 10)                  (10 ft pieces)
/// </summary>
public static class RoofEstimator
{
    public const int BundlesPerSquare = 3;
    public const decimal SquaresPerUnderlaymentRoll = 10m;
    public const decimal DripEdgePieceFt = 10m;
    public const decimal MaxFootprintSqFt = 50_000m;
    public const decimal MaxPitchRise = 24m;     // 24/12 = 63 degrees; steeper is a wall, not a roof
    public const decimal MaxWastePercent = 30m;

    public static RoofEstimate Estimate(RoofInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<PricingError>();
        if (input.FootprintSqFt <= 0 || input.FootprintSqFt > MaxFootprintSqFt)
            errors.Add(new("invalid_footprint", $"Footprint must be between 1 and {MaxFootprintSqFt:N0} sq ft."));
        if (input.PitchRise < 0 || input.PitchRise > MaxPitchRise)
            errors.Add(new("invalid_pitch", $"Pitch rise must be between 0 and {MaxPitchRise} (inches per 12)."));
        if (input.WastePercent < 0 || input.WastePercent > MaxWastePercent)
            errors.Add(new("invalid_waste", $"Waste must be between 0 and {MaxWastePercent} %."));
        if (input.EavesAndRakesFt < 0 || input.EavesAndRakesFt > 10_000m)
            errors.Add(new("invalid_edges", "Edge length must be between 0 and 10,000 ft."));
        if (errors.Count > 0) return new RoofEstimate(null, errors);

        var rise = input.PitchRise / 12m;
        var pitchFactor = Sqrt(1m + rise * rise);
        var area = input.FootprintSqFt * pitchFactor;
        var squares = CeilTo(area * (1m + input.WastePercent / 100m) / 100m, 2);

        return new RoofEstimate(new RoofQuantities(
            PitchFactor: Math.Round(pitchFactor, 4, MidpointRounding.AwayFromZero),
            RoofAreaSqFt: Math.Round(area, 1, MidpointRounding.AwayFromZero),
            Squares: squares,
            ShingleBundles: (int)Math.Ceiling(squares * BundlesPerSquare),
            UnderlaymentRolls: (int)Math.Ceiling(squares / SquaresPerUnderlaymentRoll),
            DripEdgePieces: (int)Math.Ceiling(input.EavesAndRakesFt / DripEdgePieceFt)), []);
    }

    private static decimal CeilTo(decimal value, int decimals)
    {
        var f = (decimal)Math.Pow(10, decimals);
        return Math.Ceiling(value * f) / f;
    }

    /// <summary>decimal square root (Newton), so the whole calculation stays in base-10.</summary>
    private static decimal Sqrt(decimal x)
    {
        if (x == 0) return 0;
        var g = (decimal)Math.Sqrt((double)x);
        for (var i = 0; i < 4; i++) g = (g + x / g) / 2m;
        return g;
    }
}
