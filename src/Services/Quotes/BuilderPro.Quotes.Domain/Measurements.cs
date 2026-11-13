namespace BuilderPro.Quotes.Domain;

/// <summary>A rectangle of countertop, island or backsplash, in inches (backsplash: width x height).</summary>
public sealed record Section(decimal WidthIn, decimal DepthIn);

/// <summary>Countertops (2n-b): split the top into rectangles, like a fabricator does.</summary>
public sealed record CountertopInput(
    IReadOnlyList<Section>? Countertop, IReadOnlyList<Section>? Island, IReadOnlyList<Section>? Backsplash, string? Layout = null);

/// <summary>One room of flooring, in feet.</summary>
public sealed record Room(decimal LengthFt, decimal WidthFt);

/// <summary>What the customer measured. Which part is used depends on the category's measure kind.</summary>
public sealed record MeasurementInput(CountertopInput? Countertop = null, RoofInput? Roof = null, IReadOnlyList<Room>? Rooms = null, decimal? Count = null);

/// <summary>
/// Named quantities from a measurement, e.g. sqft = 42.5, edgeFt = 18. ProductBasis names the value
/// the chosen product is bought by; option groups name theirs (2n-c).
/// </summary>
public sealed record Measurement(string Kind, IReadOnlyDictionary<string, decimal> Values, string ProductBasis)
{
    public decimal ProductQuantity => Values[ProductBasis];
}

public sealed record MeasurementResult(Measurement? Measurement, IReadOnlyList<PricingError> Errors)
{
    public bool Succeeded => Measurement is not null;
}

/// <summary>
/// THE take-off rules per measure kind (2n-b). Pure, like RoofEstimator: quantities only, never money.
///
/// countertop: area of each part = sum(width x depth) / 144, rounded UP to 0.01 sq ft;
///             sqft = countertop + island + backsplash;
///             edgeFt (finished edge) = countertop fronts (their widths) + island perimeters, rounded UP to whole ft.
/// roof:       RoofEstimator (bundles, underlaymentRolls, dripEdgePieces, squares).
/// area:       roomSqFt = sum(length x width) rounded UP to 0.01; sqft = roomSqFt + 10 % waste, rounded UP to whole sq ft.
/// count:      a whole number of items, 1..1000.
/// </summary>
public static class Measurements
{
    public const string Countertop = "countertop";
    public const string Roof = "roof";
    public const string Area = "area";
    public const string Count = "count";
    public static readonly IReadOnlyList<string> Kinds = [Countertop, Roof, Area, Count];

    public const int MaxSections = 10;
    public const decimal MinSectionIn = 6m, MaxWidthIn = 600m, MaxDepthIn = 72m, MaxBacksplashIn = 48m;
    public const int MaxRooms = 20;
    public const decimal MaxRoomFt = 200m;
    public const decimal FlooringWaste = 0.10m;
    public const decimal MaxCount = 1000m;

    public static MeasurementResult Measure(string kind, MeasurementInput? input) => kind switch
    {
        Countertop => MeasureCountertop(input?.Countertop),
        Roof => MeasureRoof(input?.Roof),
        Area => MeasureArea(input?.Rooms),
        Count => MeasureCount(input?.Count),
        _ => Fail("unknown_measure", $"No measurement rules for '{kind}'."),
    };

    private static MeasurementResult MeasureCountertop(CountertopInput? c)
    {
        if (c is null) return Fail("missing_measurements", "Enter the countertop sections.");
        var top = c.Countertop ?? [];
        var island = c.Island ?? [];
        var splash = c.Backsplash ?? [];

        var errors = new List<PricingError>();
        if (top.Count + island.Count == 0) errors.Add(new("missing_measurements", "Enter at least one countertop or island section."));
        CheckSections(top, "countertop", MaxDepthIn, errors);
        CheckSections(island, "island", MaxDepthIn, errors);
        CheckSections(splash, "backsplash", MaxBacksplashIn, errors);
        if (errors.Count > 0) return new(null, errors);

        decimal Area(IReadOnlyList<Section> s) => CeilTo(s.Sum(x => x.WidthIn * x.DepthIn) / 144m, 2);
        var topSqFt = Area(top);
        var islandSqFt = Area(island);
        var splashSqFt = Area(splash);
        var edgeIn = top.Sum(s => s.WidthIn) + island.Sum(s => 2 * (s.WidthIn + s.DepthIn));

        return Ok(Countertop, "sqft", new()
        {
            ["countertopSqFt"] = topSqFt,
            ["islandSqFt"] = islandSqFt,
            ["backsplashSqFt"] = splashSqFt,
            ["sqft"] = topSqFt + islandSqFt + splashSqFt,
            ["edgeFt"] = decimal.Ceiling(edgeIn / 12m),
        });
    }

    private static MeasurementResult MeasureRoof(RoofInput? roof)
    {
        if (roof is null) return Fail("missing_measurements", "Enter the roof footprint and pitch.");
        var estimate = RoofEstimator.Estimate(roof);
        if (!estimate.Succeeded) return new(null, estimate.Errors);
        var q = estimate.Quantities!;
        return Ok(Roof, "bundles", new()
        {
            ["squares"] = q.Squares,
            ["bundles"] = q.ShingleBundles,
            ["underlaymentRolls"] = q.UnderlaymentRolls,
            ["dripEdgePieces"] = q.DripEdgePieces,
            ["roofSqFt"] = q.RoofAreaSqFt,
        });
    }

    private static MeasurementResult MeasureArea(IReadOnlyList<Room>? rooms)
    {
        if (rooms is null || rooms.Count == 0) return Fail("missing_measurements", "Enter at least one room.");
        if (rooms.Count > MaxRooms) return Fail("too_many_rooms", $"At most {MaxRooms} rooms.");
        if (rooms.Any(r => r.LengthFt <= 0 || r.WidthFt <= 0 || r.LengthFt > MaxRoomFt || r.WidthFt > MaxRoomFt))
            return Fail("invalid_room", $"Room sides must be between 0 and {MaxRoomFt} ft.");

        var roomSqFt = CeilTo(rooms.Sum(r => r.LengthFt * r.WidthFt), 2);
        return Ok(Area, "sqft", new()
        {
            ["roomSqFt"] = roomSqFt,
            ["sqft"] = decimal.Ceiling(roomSqFt * (1 + FlooringWaste)),
        });
    }

    private static MeasurementResult MeasureCount(decimal? count)
    {
        if (count is null) return Fail("missing_measurements", "Enter how many you need.");
        if (count < 1 || count > MaxCount || count != decimal.Truncate(count.Value))
            return Fail("invalid_count", $"Enter a whole number from 1 to {MaxCount}.");
        return Ok(Count, "count", new() { ["count"] = count.Value });
    }

    private static void CheckSections(IReadOnlyList<Section> sections, string part, decimal maxDepth, List<PricingError> errors)
    {
        if (sections.Count > MaxSections) errors.Add(new("too_many_sections", $"At most {MaxSections} {part} sections."));
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.WidthIn < MinSectionIn || s.WidthIn > MaxWidthIn)
                errors.Add(new("invalid_section", $"{Cap(part)} section {i + 1}: width must be {MinSectionIn}-{MaxWidthIn} in.", i));
            var minDepth = part == "backsplash" ? 1m : MinSectionIn;
            if (s.DepthIn < minDepth || s.DepthIn > maxDepth)
                errors.Add(new("invalid_section", $"{Cap(part)} section {i + 1}: {(part == "backsplash" ? "height" : "depth")} must be {minDepth}-{maxDepth} in.", i));
        }
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private static MeasurementResult Ok(string kind, string basis, Dictionary<string, decimal> values) =>
        new(new Measurement(kind, values, basis), []);

    private static MeasurementResult Fail(string code, string message) => new(null, [new PricingError(code, message)]);

    private static decimal CeilTo(decimal value, int decimals)
    {
        var factor = (decimal)Math.Pow(10, decimals);
        return decimal.Ceiling(value * factor) / factor;
    }
}
