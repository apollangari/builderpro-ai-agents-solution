using BuilderPro.Quotes.Domain;

namespace BuilderPro.Quotes.Domain.Tests;

public sealed class RoofEstimatorTests
{
    private static RoofQuantities Ok(RoofInput input)
    {
        var e = RoofEstimator.Estimate(input);
        Assert.True(e.Succeeded, string.Join("; ", e.Errors.Select(x => x.Code)));
        return e.Quantities!;
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(4, 1.0541)]
    [InlineData(6, 1.118)]
    [InlineData(8, 1.2019)]
    [InlineData(12, 1.4142)]
    public void Pitch_factor_follows_the_slope(double rise, double factor)
    {
        Assert.Equal((decimal)factor, Ok(new RoofInput(1000m, (decimal)rise)).PitchFactor);
    }

    [Fact]
    public void A_typical_gable_roof()
    {
        // 2,000 sq ft footprint, 6/12, 10 % waste, 180 ft of eaves and rakes.
        var q = Ok(new RoofInput(2000m, 6m, 10m, 180m));

        Assert.Equal(2236.1m, q.RoofAreaSqFt);     // 2000 x 1.1180
        Assert.Equal(24.60m, q.Squares);           // 2236.07 x 1.10 / 100 = 24.597 -> up to 24.60
        Assert.Equal(74, q.ShingleBundles);        // 24.60 x 3 = 73.8 -> 74
        Assert.Equal(3, q.UnderlaymentRolls);      // 2.46 -> 3
        Assert.Equal(18, q.DripEdgePieces);        // 180 / 10
    }

    [Fact]
    public void Materials_are_always_rounded_up_to_whole_units()
    {
        var q = Ok(new RoofInput(1001m, 0m, 0m, 101m));   // flat, no waste: 10.01 squares
        Assert.Equal(10.01m, q.Squares);
        Assert.Equal(31, q.ShingleBundles);   // 30.03 -> 31
        Assert.Equal(2, q.UnderlaymentRolls); // 1.001 -> 2
        Assert.Equal(11, q.DripEdgePieces);   // 10.1 -> 11
    }

    [Fact]
    public void More_waste_means_more_material_never_less()
    {
        var simple = Ok(new RoofInput(1500m, 8m, 10m));
        var complex = Ok(new RoofInput(1500m, 8m, 15m));
        Assert.True(complex.ShingleBundles > simple.ShingleBundles);
        Assert.Equal(simple.RoofAreaSqFt, complex.RoofAreaSqFt);   // waste isn't roof area
    }

    [Fact]
    public void Unknown_edges_mean_no_drip_edge()
    {
        Assert.Equal(0, Ok(new RoofInput(1000m, 6m)).DripEdgePieces);
    }

    [Fact]
    public void Same_input_same_quantities()
    {
        var input = new RoofInput(1834.5m, 7m, 12m, 164m);
        Assert.Equal(Ok(input), Ok(input));
    }

    [Fact]
    public void Every_bad_input_is_reported()
    {
        var e = RoofEstimator.Estimate(new RoofInput(0m, 30m, 50m, -1m));
        Assert.False(e.Succeeded);
        Assert.Equal(["invalid_footprint", "invalid_pitch", "invalid_waste", "invalid_edges"], e.Errors.Select(x => x.Code));
    }
}
