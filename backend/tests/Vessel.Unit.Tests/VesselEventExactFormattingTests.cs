using System.Globalization;
using Vessel.Application.Events;
using Vessel.Domain.Numerics;

namespace Vessel.Tests;

/// <summary>Event payload amounts use the shared exact formatter and stay byte-identical to the former copy.</summary>
public sealed class VesselEventExactFormattingTests
{
    // The formatter VesselEventValues.Exact used before it delegated to ExactDecimal.
    private static string Former(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    public static TheoryData<string> Edges => new()
    {
        "0", "0.0", "0.0000000000000000000000000000", "-0", "-0.000", "1", "1.0", "1.5000", "-1.5000", "100", "100.00",
        "0.5000", "123.4500", "-0.00000001", "0.0000000000000000000000000001", "-0.0000000000000000000000000001",
        "79228162514264337593543950335", "-79228162514264337593543950335", "7.9228162514264337593543950335",
        "-7.9228162514264337593543950335", "0.1000000000000000000000000000", "1000000000000000000000000000",
        "12345678901234567890.12345678", "0.9999999999999999999999999999"
    };

    [Theory]
    [MemberData(nameof(Edges))]
    public void Edge_values_format_identically(string text)
    {
        var value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        Assert.Equal(Former(value), VesselEventValues.Exact(value));
        Assert.Equal(ExactDecimal.Format(value), VesselEventValues.Exact(value));
    }

    [Fact]
    public void Negative_zero_and_trailing_zero_scales_format_identically()
    {
        for (byte scale = 0; scale <= 28; scale++)
            foreach (var negative in new[] { false, true })
                foreach (var (lo, mid, hi) in new[] { (0, 0, 0), (1, 0, 0), (10, 0, 0), (-1, -1, -1), (1000, 0, 0) })
                {
                    var value = new decimal(lo, mid, hi, negative, scale);
                    Assert.Equal(Former(value), VesselEventValues.Exact(value));
                }
    }

    [Fact]
    public void Random_coefficients_at_every_scale_format_identically()
    {
        var random = new Random(20261016);
        for (var i = 0; i < 20_000; i++)
        {
            var value = new decimal(random.Next(int.MinValue, int.MaxValue), random.Next(int.MinValue, int.MaxValue),
                random.Next(0, 4) == 0 ? 0 : random.Next(int.MinValue, int.MaxValue), random.Next(2) == 0, (byte)random.Next(0, 29));
            Assert.Equal(Former(value), VesselEventValues.Exact(value));
        }
    }

    [Fact]
    public void Sums_match_the_shared_exact_sum()
    {
        decimal[][] cases =
        [
            [], [0m], [0.5000m, 0.2500m], [-1.10m, 1.1m], [decimal.MaxValue, decimal.MaxValue],
            [0.0000000000000000000000000001m, 79228162514264337593543950335m], [-0.000m, 0.000m]
        ];
        foreach (var values in cases)
            Assert.Equal(ExactDecimal.Sum(values), VesselEventValues.Sum(values));
        Assert.Equal("79228162514264337593543950335.0000000000000000000000000001",
            VesselEventValues.Sum([0.0000000000000000000000000001m, 79228162514264337593543950335m]));
    }
}
