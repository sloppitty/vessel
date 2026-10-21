using System.Numerics;
using Vessel.Application.Common;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Paper;

/// <summary>
/// Model 1 precision: 12-place quantities (down), margin (up), released margin (down), P&amp;L (floor).
/// Facts are bounded, not their exact ledger sums. Prices remain the defined plan prices.
/// </summary>
internal static class PaperPrecision
{
    private static readonly BigInteger Factor = BigInteger.Pow(10, 28);
    private static readonly BigInteger Scale = BigInteger.Pow(10, 12);
    public const decimal MaxQuantity = 1_000_000_000_000m;
    public const decimal MaxPrice = 1_000_000_000_000m;
    public const decimal MaxUsd = 1_000_000_000_000_000m;
    public static BigInteger Units(decimal value) => ExactAmount.From(value).Units;

    public static decimal Quantity(BigInteger numerator, BigInteger denominator) =>
        Quantize(numerator, denominator, false, MaxQuantity);
    public static decimal Share(decimal quantity, decimal share) =>
        Quantity(Units(quantity) * Units(share), 100 * Factor * Factor);
    public static decimal Margin(decimal quantity, decimal price, int leverage) =>
        Quantize(Units(quantity) * Units(price), Factor * Factor * leverage, true, MaxUsd);
    public static decimal ReleasedMargin(decimal margin, decimal quantity, decimal originalQuantity) =>
        Quantize(Units(margin) * Units(quantity), Factor * Units(originalQuantity), false, MaxUsd);
    public static decimal Pnl(decimal entry, decimal exit, decimal quantity, bool isLong) =>
        Quantize((Units(exit) - Units(entry)) * Units(quantity) * (isLong ? 1 : -1),
            Factor * Factor, false, MaxUsd);

    private static decimal Quantize(BigInteger numerator, BigInteger denominator, bool ceiling, decimal maximum)
    {
        var value = BigInteger.DivRem(numerator * Scale, denominator, out var remainder);
        if (!remainder.IsZero && (ceiling && numerator.Sign > 0 || !ceiling && numerator.Sign < 0))
            value += numerator.Sign;
        var exact = new ExactAmount(value * (Factor / Scale));
        if (exact.Abs() > ExactAmount.From(maximum) || !ExactDecimal.TryParseVenue(exact.ToString(), out var result))
            throw Unsupported();
        return result;
    }

    public static ProblemException Unsupported() =>
        new(409, "Paper model 1 supports 12-place quantity/USD allocations, quantity and prices up to 10^12, and per-fact USD up to 10^15. Positive allocations must fit these bounds.");
}
