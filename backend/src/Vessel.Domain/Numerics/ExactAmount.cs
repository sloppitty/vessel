using System.Globalization;
using System.Numerics;

namespace Vessel.Domain.Numerics;

/// <summary>
/// A signed amount as a whole number of 10^-28 units. Fixed 28-place integer arithmetic preserves every retained
/// decimal, even across differing scales, and sums never round or overflow. Products keep 56 places until
/// <see cref="Average"/> divides them; only a non-terminating weighted average needs rounding.
/// </summary>
public readonly record struct ExactAmount(BigInteger Units) : IComparable<ExactAmount>
{
    private static readonly BigInteger Factor = BigInteger.Pow(10, 28);
    public static ExactAmount From(decimal value) => new(ExactDecimal.Units(value));
    /// <summary>
    /// Trusted exact DTO strings, which can exceed decimal's coefficient range after summing. This does not validate:
    /// pass only text this type or <see cref="ExactDecimal.Format(decimal)"/> printed.
    /// </summary>
    public static ExactAmount Parse(string value)
    {
        var negative = value.StartsWith('-');
        var parts = (negative ? value[1..] : value).Split('.');
        var fraction = parts.Length == 2 ? parts[1] : "";
        var units = BigInteger.Parse(parts[0] + fraction.PadRight(28, '0'), CultureInfo.InvariantCulture);
        return new(negative ? -units : units);
    }
    public static ExactAmount operator +(ExactAmount a, ExactAmount b) => new(a.Units + b.Units);
    public static ExactAmount operator -(ExactAmount a, ExactAmount b) => new(a.Units - b.Units);
    public static ExactAmount operator -(ExactAmount a) => new(-a.Units);
    public ExactAmount Abs() => new(BigInteger.Abs(Units));
    /// <summary>The exact 56-place product of two decimals' units, such as quantity × price; decimal multiplication would round it.</summary>
    public static BigInteger Product(decimal a, decimal b) => ExactDecimal.Units(a) * ExactDecimal.Units(b);
    public int CompareTo(ExactAmount other) => Units.CompareTo(other.Units);
    public static bool operator <(ExactAmount a, ExactAmount b) => a.Units < b.Units;
    public static bool operator <=(ExactAmount a, ExactAmount b) => a.Units <= b.Units;
    public static bool operator >(ExactAmount a, ExactAmount b) => a.Units > b.Units;
    public static bool operator >=(ExactAmount a, ExactAmount b) => a.Units >= b.Units;
    /// <summary>A 56-place notional over a quantity, rounded half away from zero to 28 places; null for a zero quantity.</summary>
    public static string? Average(BigInteger notional, ExactAmount quantity)
    {
        if (quantity.Units.IsZero) return null;
        var quotient = BigInteger.DivRem(notional, quantity.Units, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= BigInteger.Abs(quantity.Units)) quotient += notional.Sign * quantity.Units.Sign;
        return new ExactAmount(quotient).ToString();
    }
    private static readonly BigInteger MaxCoefficient = (BigInteger.One << 96) - 1;

    /// <summary>
    /// This amount over a positive count, rounded a single time to the most precise decimal (96-bit coefficient,
    /// scale 0–28). Rounding is half to even, as decimal division (<c>/</c>, and so <c>Enumerable.Average</c>) rounds,
    /// and the exact remainder is kept until that one rounding. Throws when the quotient is outside decimal range.
    /// </summary>
    public decimal ToDecimal(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var numerator = BigInteger.Abs(Units);
        var denominator = Factor * count;
        for (var scale = 28; scale >= 0; scale--)
        {
            var quotient = BigInteger.DivRem(numerator * BigInteger.Pow(10, scale), denominator, out var remainder);
            var twice = remainder * 2;
            if (twice > denominator || twice == denominator && !quotient.IsEven) quotient++;
            if (quotient > MaxCoefficient) continue;
            // An exact quotient drops trailing zeros; a rounded one keeps full precision like decimal division. Only the
            // representation differs, never the value.
            if (remainder.IsZero)
                while (scale > 0 && !quotient.IsZero && (quotient % 10).IsZero) { quotient /= 10; scale--; }
            if (quotient.IsZero) scale = 0;
            var bytes = quotient.ToByteArray(isUnsigned: true, isBigEndian: false);
            Array.Resize(ref bytes, 12);
            return new decimal(BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4), BitConverter.ToInt32(bytes, 8),
                Units.Sign < 0 && !quotient.IsZero, (byte)scale);
        }
        throw new OverflowException("The quotient is outside decimal range.");
    }
    public override string ToString()
    {
        var whole = BigInteger.DivRem(BigInteger.Abs(Units), Factor, out var remainder);
        var fraction = remainder.ToString(CultureInfo.InvariantCulture).PadLeft(28, '0').TrimEnd('0');
        return (Units.Sign < 0 ? "-" : "") + whole.ToString(CultureInfo.InvariantCulture) + (fraction.Length == 0 ? "" : "." + fraction);
    }
}
