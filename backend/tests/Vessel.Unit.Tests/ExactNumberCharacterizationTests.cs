using System.Globalization;
using System.Numerics;
using System.Reflection;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Numerics;

namespace Vessel.Tests;

/// <summary>
/// Characterization of every exact-number entry point (backend review S1): the text each one accepts and the
/// strings it prints, quirks included. The tables are the contract. When an implementation moves, only the
/// bindings in the first section may change; a changed expectation is a behaviour change.
/// </summary>
public sealed class ExactNumberCharacterizationTests : ExactNumberCharacterizationTestsValues
{
    // ---- Bindings: the code each entry point runs. ----

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;

    private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, Hidden | BindingFlags.Public, parameters) ?? throw new MissingMethodException(type.Name, name);

    private static object? Call(MethodInfo method, params object?[] arguments) =>
        method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, arguments, null);

    /// <summary>The API formatter of each module that prints exact amounts.</summary>
    private static readonly Dictionary<string, Func<decimal, string>> Formatters = new()
    {
        ["shared"] = ExactDecimal.Format,
        ["nullable"] = value => ExactDecimal.Format((decimal?)value)!,
        ["manual ledger"] = ManualLedger.Text,
    };

    /// <summary>Sizing estimates, rounded to eight places.</summary>
    private static string SizingFormat(decimal value) => ExactDecimal.FormatEightPlaces(value);

    private static string Text(ExactAmount amount) => amount.ToString();
    private static BigInteger Units(ExactAmount amount) => amount.Units;
    private static ExactAmount AmountFrom(decimal value) => ExactAmount.From(value);
    private static ExactAmount AmountParse(string text) => ExactAmount.Parse(text);
    private static ExactAmount AmountDefault() => default;
    private static string? AmountAverage(BigInteger notional, BigInteger quantityUnits) => ExactAmount.Average(notional, new(quantityUnits));

    /// <summary>a + b | a - b | -a | |a| | sign of a.CompareTo(b) | a == b, through the 28-place accumulator.</summary>
    private static string AmountArithmetic(string first, string second)
    {
        var (a, b) = (ExactAmount.Parse(first), ExactAmount.Parse(second));
        return $"{a + b} | {a - b} | {-a} | {a.Abs()} | {Math.Sign(a.CompareTo(b))} | {a == b}";
    }

    private static string Sum(params decimal[] values) => ExactDecimal.Sum(values);

    /// <summary>The manual ledger's checked addition: the sum, "overflow" or "inexact" (decimal rounded).</summary>
    private static string LedgerAdd(decimal a, decimal b)
    {
        try { return Describe((decimal)Call(Method(typeof(ManualLedger), "Add", typeof(decimal), typeof(decimal)), a, b)!); }
        catch (ProblemException e) when (e.Message == "Manual ledger exceeds the supported decimal range.") { return "overflow"; }
        catch (ProblemException e) when (e.Message == "Manual ledger amounts exceed exact decimal precision.") { return "inexact"; }
    }

    /// <summary>The manual ledger's checked multiplication: the product, "overflow" or "inexact" (decimal rounded).</summary>
    private static string LedgerMultiply(decimal a, decimal b)
    {
        try { return Describe((decimal)Call(Method(typeof(ManualLedger), "Multiply", typeof(decimal), typeof(decimal)), a, b)!); }
        catch (OverflowException) { return "overflow"; }
        catch (ProblemException e) when (e.Message == "Manual ledger notional exceeds exact decimal precision.") { return "inexact"; }
    }

    // ---- Formatting ----

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("0.000", "0")]
    [InlineData("-0.000", "0")]
    [InlineData("1", "1")]
    [InlineData("-1", "-1")]
    [InlineData("1.500", "1.5")]
    [InlineData("-1.500", "-1.5")]
    [InlineData("100.00", "100")]
    [InlineData("1000000", "1000000")]
    [InlineData("1.0000000000000000000000000000", "1")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("-0.0000000000000000000000000001", "-0.0000000000000000000000000001")]
    [InlineData("7.9228162514264337593543950335", "7.9228162514264337593543950335")]
    [InlineData("123456789.123456789", "123456789.123456789")]
    [InlineData("1e-8", "0.00000001")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335")]
    public void Every_module_prints_amounts_as_exact_invariant_text(string value, string expected)
    {
        foreach (var (module, format) in Formatters)
            Assert.True(format(Value(value)) == expected, $"{module} printed {format(Value(value))}");
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("1.25", "1.25")]
    [InlineData("-1.500", "-1.5")]
    [InlineData("0.00000001", "0.00000001")]
    [InlineData("0.000000005", "0")]
    [InlineData("0.000000015", "0.00000002")]
    [InlineData("0.000000025", "0.00000002")]
    [InlineData("-0.000000004", "0")]
    [InlineData("-0.000000005", "0")]
    [InlineData("-0.000000006", "-0.00000001")]
    [InlineData("0.123456775", "0.12345678")]
    [InlineData("0.123456785", "0.12345678")]
    [InlineData("99999999.999999995", "100000000")]
    [InlineData("0.0000000000000000000000000001", "0")]
    [InlineData("7.9228162514264337593543950335", "7.92281625")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void Sizing_rounds_half_to_even_at_eight_places(string value, string expected) =>
        Assert.Equal(expected, SizingFormat(Value(value)));

    // ---- The 28-place accumulator ----

    [Theory]
    [InlineData("0", "0", "0")]
    [InlineData("-0", "0", "0")]
    [InlineData("1", "1", "10000000000000000000000000000")]
    [InlineData("-1.50", "-1.5", "-15000000000000000000000000000")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001", "1")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335", "792281625142643375935439503350000000000000000000000000000")]
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335", "-792281625142643375935439503350000000000000000000000000000")]
    public void Amounts_from_decimals_keep_sign_and_every_digit(string value, string text, string units)
    {
        var amount = AmountFrom(Value(value));
        Assert.Equal(text, Text(amount));
        Assert.Equal(BigInteger.Parse(units, CultureInfo.InvariantCulture), Units(amount));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("1.50", "1.5")]
    [InlineData("-0", "0")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("158456325028528675187087900670.0000000000000000000000000001", "158456325028528675187087900670.0000000000000000000000000001")]
    [InlineData("-79228162514264337593543950336", "-79228162514264337593543950336")]
    // Parse trusts its input: these quirks are unreachable from exact DTO strings, but pinned.
    [InlineData("0.00000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("0.123456789012345678901234567891", "12.3456789012345678901234567891")]
    [InlineData("", "0")]
    [InlineData("-", "0")]
    [InlineData("1.2.3", "1")]
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5")]
    [InlineData("+1", "1")]
    [InlineData(" 1", "1")]
    [InlineData("--1", "1")]
    public void Amounts_parse_trusted_text(string text, string expected) => Assert.Equal(expected, Text(AmountParse(text)));

    [Theory]
    [InlineData("1 ")]
    [InlineData("1.5 ")]
    [InlineData("1e5")]
    [InlineData("abc")]
    [InlineData("1.-5")]
    public void Amount_parsing_throws_on_other_text(string text) => Assert.Throws<FormatException>(() => AmountParse(text));

    [Theory]
    [InlineData("1.5", "-2.25", "-0.75 | 3.75 | -1.5 | 1.5 | 1 | False")]
    [InlineData("1.5", "1.50", "3 | 0 | -1.5 | 1.5 | 0 | True")]
    [InlineData("-0.0000000000000000000000000001", "0", "-0.0000000000000000000000000001 | -0.0000000000000000000000000001 | 0.0000000000000000000000000001 | 0.0000000000000000000000000001 | -1 | False")]
    [InlineData("79228162514264337593543950335", "-79228162514264337593543950335", "0 | 158456325028528675187087900670 | -79228162514264337593543950335 | 79228162514264337593543950335 | 1 | False")]
    public void Amount_arithmetic_is_exact(string a, string b, string expected) => Assert.Equal(expected, AmountArithmetic(a, b));

    [Fact]
    public void The_default_amount_is_zero()
    {
        Assert.Equal("0", Text(AmountDefault()));
        Assert.Equal(AmountParse("0"), AmountDefault());
    }

    [Theory]
    // Notional units (quantity units × price units) over quantity units; ties round away from zero at 28 places.
    [InlineData("5", "10", "0.0000000000000000000000000001")]
    [InlineData("15", "10", "0.0000000000000000000000000002")]
    [InlineData("25", "10", "0.0000000000000000000000000003")]
    [InlineData("-5", "10", "-0.0000000000000000000000000001")]
    [InlineData("-15", "10", "-0.0000000000000000000000000002")]
    [InlineData("5", "-10", "-0.0000000000000000000000000001")]
    [InlineData("4", "10", "0")]
    [InlineData("6", "10", "0.0000000000000000000000000001")]
    [InlineData("1", "3", "0")]
    [InlineData("2", "3", "0.0000000000000000000000000001")]
    [InlineData("0", "5", "0")]
    [InlineData("1", "0", null)]
    // 1 at 100 and 2 at 101: 302 / 3.
    [InlineData("30200000000000000000000000000000000000000000000000000000000", "30000000000000000000000000000", "100.6666666666666666666666666667")]
    [InlineData("-30200000000000000000000000000000000000000000000000000000000", "30000000000000000000000000000", "-100.6666666666666666666666666667")]
    public void Weighted_averages_round_only_the_division(string notional, string quantityUnits, string? expected) =>
        Assert.Equal(expected, AmountAverage(BigInteger.Parse(notional, CultureInfo.InvariantCulture), BigInteger.Parse(quantityUnits, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0")]
    [InlineData("0", "-0")]
    [InlineData("4", "1.5000", "2.50")]
    [InlineData("0", "1.5", "-1.5")]
    [InlineData("1.0000000000000000000000000001", "0.0000000000000000000000000001", "1")]
    [InlineData("79228162514264337593543950336", "79228162514264337593543950335", "1")]
    [InlineData("158456325028528675187087900670.0000000000000000000000000001", "79228162514264337593543950335", "79228162514264337593543950335", "0.0000000000000000000000000001")]
    [InlineData("-79228162514264337593543950336", "-79228162514264337593543950335", "-1")]
    [InlineData("-0.0000000000000000000000000001", "-0.0000000000000000000000000001")]
    public void Sums_never_round_or_overflow(string expected, params string[] values) =>
        Assert.Equal(expected, Sum(values.Select(Value).ToArray()));

    // ---- Checked ledger arithmetic ----

    [Theory]
    [InlineData("1", "2", "3", "2")]
    [InlineData("1.10", "2.00", "3.10", "2.2000")]
    [InlineData("1.5", "-0.5", "1.0", "-0.75")]
    [InlineData("-3", "2", "-1", "-6")]
    [InlineData("0.1", "0.1", "0.2", "0.01")]
    [InlineData("0.0000000000000000000000000001", "1", "1.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("0.00000000000001", "0.00000000000001", "0.00000000000002", "0.0000000000000000000000000001")]
    [InlineData("0.000000000000001", "0.00000000000001", "0.000000000000011", "inexact")]
    [InlineData("10000000000000000000000000000", "0.1", "inexact", "1000000000000000000000000000.0")]
    [InlineData("7922816251426433759354395033.5", "0.01", "inexact", "79228162514264337593543950.335")]
    [InlineData("12345678901234567890.123456789", "2", "12345678901234567892.123456789", "24691357802469135780.246913578")]
    [InlineData("79228162514264337593543950335", "1", "overflow", "79228162514264337593543950335")]
    [InlineData("79228162514264337593543950335", "0.5", "overflow", "inexact")]
    [InlineData("79228162514264337593543950335", "2", "overflow", "overflow")]
    [InlineData("79228162514264337593543950335", "-79228162514264337593543950335", "0", "overflow")]
    public void Ledger_arithmetic_refuses_rounding_and_overflow(string a, string b, string sum, string product)
    {
        Assert.Equal(sum, LedgerAdd(Value(a), Value(b)));
        Assert.Equal(product, LedgerMultiply(Value(a), Value(b)));
    }
}
