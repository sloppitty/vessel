using System.Globalization;
using System.Numerics;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Risk;

/// <summary>
/// An exact value as a fraction of integers, like the browser's <c>Exact</c>: a plan's divisions (a percentage over the
/// leverage, a margin over the average entry, an average fill) stay exact until the one rounding for the response.
/// The denominator is positive and the fraction is in lowest terms.
/// </summary>
public readonly record struct Ratio
{
    public BigInteger N { get; }
    public BigInteger D { get; }

    private Ratio(BigInteger n, BigInteger d)
    {
        if (d.IsZero) throw new DivideByZeroException();
        if (d.Sign < 0) { n = -n; d = -d; }
        var divisor = BigInteger.GreatestCommonDivisor(n, d);
        if (divisor.IsZero) divisor = BigInteger.One;
        N = n / divisor;
        D = d / divisor;
    }

    public static readonly Ratio Zero = new(BigInteger.Zero, BigInteger.One);
    public static Ratio Of(BigInteger value) => new(value, BigInteger.One);
    public static Ratio From(decimal value) => new(ExactDecimal.Units(value), BigInteger.Pow(10, 28));

    /// <summary>Plan text as the editor reads it: trimmed plain decimal text (<see cref="ExactDecimal.IsPlanText"/>), else null.</summary>
    public static Ratio? Plan(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0 || !ExactDecimal.IsPlanText(trimmed)) return null;
        var parts = trimmed.Split('.');
        var fraction = parts.Length > 1 ? parts[1] : "";
        return new(BigInteger.Parse(parts[0] + fraction, CultureInfo.InvariantCulture), BigInteger.Pow(10, fraction.Length));
    }

    public int Sign => N.Sign;
    public static Ratio operator +(Ratio a, Ratio b) => new(a.N * b.D + b.N * a.D, a.D * b.D);
    public static Ratio operator -(Ratio a, Ratio b) => new(a.N * b.D - b.N * a.D, a.D * b.D);
    public static Ratio operator *(Ratio a, Ratio b) => new(a.N * b.N, a.D * b.D);
    public static Ratio? Divide(Ratio a, Ratio b) => b.N.IsZero ? null : new(a.N * b.D, a.D * b.N);
    public static bool operator <(Ratio a, Ratio b) => (a - b).Sign < 0;
    public static bool operator >(Ratio a, Ratio b) => (a - b).Sign > 0;
    public static bool operator <=(Ratio a, Ratio b) => (a - b).Sign <= 0;
    public static bool operator >=(Ratio a, Ratio b) => (a - b).Sign >= 0;
    public static Ratio Max(Ratio a, Ratio b) => a >= b ? a : b;

    /// <summary>Whether the value has a finite decimal form within 28 places.</summary>
    public bool Terminates
    {
        get
        {
            var d = D;
            var places = 0;
            while ((d % 2).IsZero) { d /= 2; places++; }
            var fives = 0;
            while ((d % 5).IsZero) { d /= 5; fives++; }
            return d.IsOne && Math.Max(places, fives) <= 28;
        }
    }

    /// <summary>
    /// A plain decimal string: exact when <see cref="Terminates"/>, otherwise rounded half away from zero to 28 places
    /// (the caller says so).
    /// </summary>
    public override string ToString()
    {
        var scale = BigInteger.Pow(10, 28);
        var quotient = BigInteger.DivRem(BigInteger.Abs(N) * scale, D, out var remainder);
        if (remainder * 2 >= D) quotient++;
        return new ExactAmount(N.Sign < 0 ? -quotient : quotient).ToString();
    }
}
