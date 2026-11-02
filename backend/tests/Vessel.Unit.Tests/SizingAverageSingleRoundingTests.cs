using System.Globalization;
using System.Numerics;
using Vessel.Domain.Numerics;
using Vessel.Domain.Sizing;

namespace Vessel.Tests;

/// <summary>
/// An overflowing track-record mean is the exact sum over the count rounded once, half to even like decimal division,
/// never rounded to 28 places first and then again to decimal precision.
/// </summary>
public sealed class SizingAverageSingleRoundingTests
{
    private static readonly BigInteger Factor = BigInteger.Pow(10, 28);
    private static readonly BigInteger MaxCoefficient = (BigInteger.One << 96) - 1;

    [Fact]
    public void An_overflowing_mean_just_above_a_midpoint_rounds_up()
    {
        decimal[] values = [70000000000000000000000000000m, 70000000000000000000000000000m, 2m, 0.0000000000000000000000000001m];
        var outcomes = values.Select((value, i) => new PlayOutcome(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddDays(i), value, value, 1, true)).ToList();
        Assert.Throws<OverflowException>(() => values.Average());
        // The exact mean is 35000000000000000000000000000.500000000000000000000000000025.
        Assert.Equal(35000000000000000000000000001m, TrackRecord.From(outcomes).AverageWinR);
    }

    [Fact]
    public void Decimal_division_rounds_half_to_even()
    {
        // The rule the overflow path matches: ties go to the even neighbour.
        var tiny = 0.0000000000000000000000000001m;
        Assert.Equal(0m, tiny / 2);
        Assert.Equal(2 * tiny, 3 * tiny / 2);
        Assert.Equal(39614081257132168796771975166m, 79228162514264337593543950333m / 2);
        var exact = ExactAmount.From(tiny);
        Assert.Equal(0m, exact.ToDecimal(2));
        Assert.Equal(2 * tiny, ExactAmount.From(3 * tiny).ToDecimal(2));
        Assert.Equal(39614081257132168796771975166m, ExactAmount.From(79228162514264337593543950333m).ToDecimal(2));
    }

    [Fact]
    public void Where_the_sum_fits_the_exact_quotient_matches_decimal_division_bit_for_bit()
    {
        var random = new Random(20261017);
        for (var i = 0; i < 5000; i++)
        {
            var sum = RandomDecimal(random);
            var count = random.Next(1, 40);
            var expected = sum / count;
            var actual = ExactAmount.From(sum).ToDecimal(count);
            Assert.True(expected == actual, $"{sum} / {count}: {expected} != {actual}");
            Assert.Equal(ExactDecimal.Format(expected), ExactDecimal.Format(actual));
        }
    }

    [Fact]
    public void Overflowing_means_match_a_rational_reference()
    {
        var random = new Random(16102026);
        var overflowing = 0;
        for (var i = 0; i < 3000; i++)
        {
            var values = Enumerable.Range(0, random.Next(2, 12)).Select(_ => RandomDecimal(random, large: random.Next(4) > 0))
                .Where(value => value != 0).ToList();
            if (values.Count == 0) continue;
            var units = values.Aggregate(BigInteger.Zero, (total, value) => total + ExactDecimal.Units(value));
            Assert.Equal(Reference(units, values.Count), new ExactAmount(units).ToDecimal(values.Count));
            try { _ = values.Average(); } catch (OverflowException) { overflowing++; }

            // Through the record: winners' R multiples (planned risk 1) and losers' magnitudes.
            var outcomes = values.Select((value, day) => new PlayOutcome(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddDays(day),
                value, Math.Abs(value), 1, true)).ToList();
            var record = TrackRecord.From(outcomes, window: values.Count);
            // The record takes the latest first; decimal sums depend on order, so the expectation sums in that order too.
            var winners = values.Where(value => value > 0).Reverse().ToList();
            var losers = values.Where(value => value < 0).Reverse().ToList();
            Assert.Equal(winners.Count > 0 ? Expected(winners) : null, record.AverageWinR);
            Assert.Equal(losers.Count > 0 ? Expected(losers) : null, record.AverageLossR);
        }
        Assert.True(overflowing > 500, $"only {overflowing} overflowing samples");
    }

    /// <summary>The unchanged decimal mean where its sum fits; the single-rounded exact mean where it overflows.</summary>
    private static decimal Expected(List<decimal> values)
    {
        try { return values.Average(); }
        catch (OverflowException) { return Reference(Sum(values), values.Count); }
    }

    private static BigInteger Sum(IEnumerable<decimal> values) =>
        values.Aggregate(BigInteger.Zero, (total, value) => total + ExactDecimal.Units(value));

    /// <summary>
    /// An independent reference: the nearest decimal at the finest scale whose coefficient fits, ties to even, chosen by
    /// comparing exact rationals of the two neighbouring candidates rather than by rounding a remainder.
    /// </summary>
    private static decimal Reference(BigInteger units, int count)
    {
        var sign = units.Sign;
        var p = BigInteger.Abs(units);
        var q = Factor * count;
        for (var scale = 28; scale >= 0; scale--)
        {
            var ten = BigInteger.Pow(10, scale);
            var low = p * ten / q;
            var high = low + 1;
            // Distances scaled by q: |x*10^s - c| * q.
            var toLow = p * ten - low * q;
            var toHigh = high * q - p * ten;
            var pick = toLow < toHigh ? low : toHigh < toLow ? high : low.IsEven ? low : high;
            if (pick > MaxCoefficient) continue;
            var text = pick.ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
            var formatted = scale == 0 ? text : text[..^scale] + "." + text[^scale..];
            var value = decimal.Parse(formatted, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            return sign < 0 ? -value : value;
        }
        throw new OverflowException();
    }

    private static decimal RandomDecimal(Random random, bool large = false)
    {
        var scale = large ? (byte)random.Next(0, 3) : (byte)random.Next(0, 29);
        var hi = large ? random.Next(int.MinValue, int.MaxValue) : random.Next(0, 3) switch { 0 => 0, 1 => random.Next(0, 1000), _ => random.Next() };
        return new decimal(random.Next(int.MinValue, int.MaxValue), random.Next(int.MinValue, int.MaxValue), hi, random.Next(2) == 0, scale);
    }
}
