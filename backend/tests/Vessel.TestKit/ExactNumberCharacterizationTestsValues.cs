using System.Globalization;

namespace Vessel.Tests;

public partial class ExactNumberCharacterizationTestsValues
{

    // ---- Helpers: unchanged by refactoring. ----

    /// <summary>Invariant text with its scale; a zero with the sign bit set is marked with a leading "-".</summary>
    internal static string Describe(decimal value) =>
        (decimal.IsNegative(value) && value == 0 ? "-" : "") + value.ToString(CultureInfo.InvariantCulture);

    internal static decimal Value(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
