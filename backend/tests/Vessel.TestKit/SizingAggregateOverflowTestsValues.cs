using System.Globalization;

namespace Vessel.Tests;

public partial class SizingAggregateOverflowTestsValues
{
    internal static readonly decimal E28 = decimal.Parse("1e28", NumberStyles.Float, CultureInfo.InvariantCulture);
}
