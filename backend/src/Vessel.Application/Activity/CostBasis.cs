using System.Numerics;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Activity;

/// <summary>
/// Issue #90: the average cost of an operation's remaining quantity, by the average-cost method. A reduction removes
/// quantity at the current average and leaves it unchanged; an add re-averages; a flip starts a new operation and so a new
/// basis. The average price, in 10^-28 units, is the exact fraction <see cref="Notional"/> / <see cref="Quantity"/>, kept
/// in lowest terms and never rounded, so the result does not depend on the order of adds that commute, and only the final
/// unrealized figure rounds, once.
/// </summary>
internal readonly record struct CostBasis(BigInteger Notional, BigInteger Quantity)
{
    private static readonly BigInteger Factor = ExactAmount.Parse("1").Units;

    /// <summary>The basis after adding <paramref name="quantity"/> at <paramref name="price"/> to <paramref name="held"/>.</summary>
    public static CostBasis Add(CostBasis? basis, ExactAmount held, ExactAmount quantity, decimal price)
    {
        var added = quantity.Units * ExactAmount.From(price).Units;
        if (basis is not { } b || held.Units.Sign <= 0) return Lowest(added, quantity.Units);
        // (average × held + quantity × price) / (held + quantity), with average = Notional / Quantity.
        return Lowest(b.Notional * held.Units + added * b.Quantity, b.Quantity * (held + quantity).Units);
    }

    /// <summary>direction × remaining × (mark − average), rounded half away from zero to 28 places a single time.</summary>
    public string Unrealized(ExactAmount remaining, ExactAmount mark, bool shortPosition)
    {
        var numerator = remaining.Units * (mark.Units * Quantity - Notional);
        return ExactAmount.Average(shortPosition ? -numerator : numerator, new ExactAmount(Quantity * Factor))!;
    }

    private static CostBasis Lowest(BigInteger notional, BigInteger quantity)
    {
        var divisor = BigInteger.GreatestCommonDivisor(notional, quantity);
        return divisor.IsZero || divisor.IsOne ? new(notional, quantity) : new(notional / divisor, quantity / divisor);
    }
}
