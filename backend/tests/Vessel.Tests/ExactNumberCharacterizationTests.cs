using System.Globalization;
using System.Numerics;
using System.Reflection;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Tests;

/// <summary>
/// Characterization of every exact-number entry point (backend review S1): the text each one accepts and the
/// strings it prints, quirks included. The tables are the contract. When an implementation moves, only the
/// bindings in the first section may change; a changed expectation is a behaviour change.
/// </summary>
public sealed class ExactNumberCharacterizationTests
{
    // ---- Bindings: the code each entry point runs. ----

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;

    private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, Hidden | BindingFlags.Public, parameters) ?? throw new MissingMethodException(type.Name, name);

    private static object? Call(MethodInfo method, params object?[] arguments) =>
        method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, arguments, null);

    /// <summary>Venue JSON amounts (<c>StrictJson.ParseDecimal</c>).</summary>
    private static string Venue(string text)
    {
        try { return Describe(new StrictJson("invalid").ParseDecimal(text)); }
        catch (VenueReadException) { return "x"; }
    }

    /// <summary>The manual account value of a new manual account.</summary>
    private static async Task<string> ManualAccountValue(string text)
    {
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner);
        try { await new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.None).CreateAccountAsync(new(null, "Manual", "manual", ManualAccountValueUsd: text), default); }
        catch (ProblemException e) when (e.Message == "Manual account value must be an exact nonnegative decimal string.") { return "x"; }
        return Describe(store.Accounts.Single().ManualAccountValueUsd!.Value);
    }

    /// <summary>Manual execution quantity and price (<paramref name="positive"/>) or fee.</summary>
    private static string ManualFact(string text, bool positive)
    {
        try { return Describe(ManualLedger.Number(text, positive)); }
        catch (ProblemException e) when (e.StatusCode == 400)
        {
            return e.Message switch
            {
                "Quantity and price must be positive decimal strings." when positive => "x",
                "Fees must be nonnegative decimal strings." when !positive => "x",
                "Decimal is outside the supported range." => "range",
                "Decimal must be exactly representable, without rounding." => "inexact",
                _ => "unexpected: " + e.Message
            };
        }
    }

    /// <summary>Amount strings an account-discovery adapter returns.</summary>
    private static string AdapterText(string text) => ExactDecimal.IsSignedAmountText(text) ? "ok" : "x";

    /// <summary>Plan number fields on write (a new Play), kept as typed. Stored text is read tolerantly and may be saved unchanged.</summary>
    private static string PlanText(string? text)
    {
        try { return PlayDocuments.Normalize(TestPlays.Plan() with { Size = text! }).Size == text ? "ok" : "changed"; }
        catch (ProblemException e) when (e.Message == $"Size: {PlayDocumentTests.PlainDecimalRule}") { return "x"; }
    }

    /// <summary>The sizing risk setting; "rule" means it parsed but the Domain range rejected it.</summary>
    private static async Task<string> RiskPercent(string? text)
    {
        var store = new MemorySizingStore();
        try { await new SizingService(store, new CoreOwner(Guid.NewGuid()), TimeProvider.System, TestUnitOfWork.None).UpdateSettingsAsync(new(text!), default); }
        catch (ProblemException e) when (e.Message == "Risk per trade must be a decimal percentage.") { return "x"; }
        catch (DomainValidationException) { return "rule"; }
        return Describe(store.Settings!.RiskPercent);
    }

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

    // ---- Helpers: unchanged by refactoring. ----

    /// <summary>Invariant text with its scale; a zero with the sign bit set is marked with a leading "-".</summary>
    private static string Describe(decimal value) =>
        (decimal.IsNegative(value) && value == 0 ? "-" : "") + value.ToString(CultureInfo.InvariantCulture);

    private static decimal Value(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>"1." padded with zeros to <paramref name="length"/> characters: the value one, at any length.</summary>
    private static string One(int length) => "1." + new string('0', length - 2);

    // ---- Parse profiles ----

    /// <summary>
    /// One input per row, then what each entry point makes of it: the parsed value (with scale), "x" for the entry
    /// point's own rejection, "range"/"inexact" for the manual ledger's later checks, "ok" for text-only checks and
    /// "rule" when the risk setting parsed but its Domain range refused it.
    /// </summary>
    public static TheoryData<string, string, string, string, string, string, string, string> Profiles => new()
    {
        // input, venue JSON, manual account value, manual quantity/price, manual fee, adapter text, plan text, risk percent
        { "0", "0", "0", "x", "0", "ok", "ok", "rule" },
        { "1", "1", "1", "1", "1", "ok", "ok", "1" },
        { "-1", "-1", "x", "x", "x", "ok", "x", "x" },
        { "+1", "x", "x", "1", "1", "x", "x", "x" },
        { "-0", "-0", "x", "x", "x", "ok", "x", "x" },
        { "+0", "x", "x", "x", "0", "x", "x", "x" },
        { "00", "0", "0", "x", "0", "ok", "ok", "rule" },
        { "007", "7", "7", "7", "7", "ok", "ok", "rule" },
        { "1.5", "1.5", "1.5", "1.5", "1.5", "ok", "ok", "1.5" },
        { "-1.5", "-1.5", "x", "x", "x", "ok", "x", "x" },
        { "+1.5", "x", "x", "1.5", "1.5", "x", "x", "x" },
        { "1.50", "1.50", "1.50", "1.50", "1.50", "ok", "ok", "1.50" },
        { "01.10", "1.10", "1.10", "1.10", "1.10", "ok", "ok", "1.10" },
        { "0.000", "0.000", "0.000", "x", "0.000", "ok", "ok", "rule" },
        // A leading or trailing dot.
        { ".5", "x", "0.5", "x", "x", "x", "x", "0.5" },
        { "-.5", "x", "x", "x", "x", "x", "x", "x" },
        { "+.5", "x", "x", "x", "x", "x", "x", "x" },
        { "5.", "x", "5", "x", "x", "x", "x", "5" },
        { "-5.", "x", "x", "x", "x", "x", "x", "x" },
        { ".", "x", "x", "x", "x", "x", "x", "x" },
        { "-", "x", "x", "x", "x", "x", "x", "x" },
        { "+", "x", "x", "x", "x", "x", "x", "x" },
        { "--1", "x", "x", "x", "x", "x", "x", "x" },
        { "-+1", "x", "x", "x", "x", "x", "x", "x" },
        { "1.2.3", "x", "x", "x", "x", "x", "x", "x" },
        { "1..2", "x", "x", "x", "x", "x", "x", "x" },
        // Empty, whitespace and control characters. A regex "$" also matches before a final "\n".
        { "", "x", "x", "x", "x", "x", "ok", "x" },
        { " ", "x", "x", "x", "x", "x", "x", "x" },
        { " 1", "x", "x", "x", "x", "x", "x", "x" },
        { "1 ", "x", "x", "x", "x", "x", "x", "x" },
        { "\t1", "x", "x", "x", "x", "x", "x", "x" },
        { "1\n", "x", "x", "1", "1", "x", "x", "x" },
        { "1.5\n", "x", "x", "inexact", "inexact", "x", "x", "x" },
        { "1e5\n", "x", "x", "100000", "100000", "x", "x", "x" },
        { "1\r\n", "x", "x", "x", "x", "x", "x", "x" },
        { "1\0", "x", "x", "x", "x", "x", "x", "1" },
        { "1.5\0", "x", "x", "x", "x", "x", "x", "1.5" },
        // Exponents.
        { "1e5", "x", "x", "100000", "100000", "x", "x", "x" },
        { "1E5", "x", "x", "100000", "100000", "x", "x", "x" },
        { "1e+5", "x", "x", "100000", "100000", "x", "x", "x" },
        { "1e-5", "x", "x", "0.00001", "0.00001", "x", "x", "x" },
        { "1.5e3", "x", "x", "1500", "1500", "x", "x", "x" },
        { "1.230e-5", "x", "x", "0.00001230", "0.00001230", "x", "x", "x" },
        { "-1e5", "x", "x", "x", "x", "x", "x", "x" },
        { "+1e5", "x", "x", "100000", "100000", "x", "x", "x" },
        { "1e", "x", "x", "x", "x", "x", "x", "x" },
        { "e5", "x", "x", "x", "x", "x", "x", "x" },
        { ".5e1", "x", "x", "x", "x", "x", "x", "x" },
        { "1.e1", "x", "x", "x", "x", "x", "x", "x" },
        { "1e05", "x", "x", "100000", "100000", "x", "x", "x" },
        { "1e005", "x", "x", "x", "x", "x", "x", "x" },
        { "1e99", "x", "x", "x", "x", "x", "x", "x" },
        { "1e100", "x", "x", "x", "x", "x", "x", "x" },
        { "1e999999", "x", "x", "x", "x", "x", "x", "x" },
        { "1e-28", "x", "x", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "x", "x", "x" },
        { "1e-29", "x", "x", "x", "inexact", "x", "x", "x" },
        { "1e-96", "x", "x", "x", "inexact", "x", "x", "x" },
        { "0e99", "x", "x", "x", "range", "x", "x", "x" },
        { "0e-97", "x", "x", "x", "range", "x", "x", "x" },
        // Other notations. "\d" in a .NET regex also matches non-ASCII digits.
        { "1,234", "x", "x", "x", "x", "x", "x", "x" },
        { "1_000", "x", "x", "x", "x", "x", "x", "x" },
        { "NaN", "x", "x", "x", "x", "x", "x", "x" },
        { "Infinity", "x", "x", "x", "x", "x", "x", "x" },
        { "0x10", "x", "x", "x", "x", "x", "x", "x" },
        { "١", "x", "x", "x", "x", "x", "x", "x" },
        { "１", "x", "x", "x", "x", "x", "x", "x" },
        // 28 fractional digits and beyond.
        { "0.0000000000000000000000000001", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "ok", "ok", "x" },
        { "0.00000000000000000000000000001", "x", "x", "x", "inexact", "ok", "x", "x" },
        { "0.00000000000000000000000000010", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "ok", "ok", "x" },
        { "0.00000000000000000000000000000", "0.0000000000000000000000000000", "0.0000000000000000000000000000", "x", "0.0000000000000000000000000000", "ok", "ok", "x" },
        { "1.0000000000000000000000000001", "1.0000000000000000000000000001", "1.0000000000000000000000000001", "1.0000000000000000000000000001", "1.0000000000000000000000000001", "ok", "ok", "x" },
        { "1.00000000000000000000000000001", "x", "x", "inexact", "inexact", "ok", "x", "x" },
        { "1.00000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "ok", "ok", "x" },
        { "0.1234567890123456789012345678", "0.1234567890123456789012345678", "0.1234567890123456789012345678", "0.1234567890123456789012345678", "0.1234567890123456789012345678", "ok", "ok", "x" },
        { "9.9999999999999999999999999999", "x", "x", "inexact", "inexact", "ok", "x", "x" },
        // The 96-bit coefficient.
        { "79228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "ok", "ok", "x" },
        { "79228162514264337593543950336", "x", "x", "x", "x", "ok", "x", "x" },
        { "-79228162514264337593543950335", "-79228162514264337593543950335", "x", "x", "x", "ok", "x", "x" },
        { "0079228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "ok", "ok", "x" },
        { "79228162514264337593543950335.0", "79228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "79228162514264337593543950335", "ok", "ok", "x" },
        { "7922816251426433759354395033.5", "7922816251426433759354395033.5", "7922816251426433759354395033.5", "7922816251426433759354395033.5", "7922816251426433759354395033.5", "ok", "ok", "x" },
        { "7922816251426433759354395033.6", "x", "x", "inexact", "inexact", "ok", "x", "x" },
        { "1234567890123456789012345678.9", "1234567890123456789012345678.9", "1234567890123456789012345678.9", "1234567890123456789012345678.9", "1234567890123456789012345678.9", "ok", "ok", "x" },
        { "12345678901234567890123456789.1", "x", "x", "inexact", "inexact", "ok", "x", "x" },
        // The risk setting's range and length.
        { "0.1", "0.1", "0.1", "0.1", "0.1", "ok", "ok", "0.1" },
        { "0.09", "0.09", "0.09", "0.09", "0.09", "ok", "ok", "rule" },
        { "1.25", "1.25", "1.25", "1.25", "1.25", "ok", "ok", "1.25" },
        { "1.250", "1.250", "1.250", "1.250", "1.250", "ok", "ok", "1.250" },
        { "1.255", "1.255", "1.255", "1.255", "1.255", "ok", "ok", "rule" },
        { "5", "5", "5", "5", "5", "ok", "ok", "5" },
        { "5.00", "5.00", "5.00", "5.00", "5.00", "ok", "ok", "5.00" },
        { "5.01", "5.01", "5.01", "5.01", "5.01", "ok", "ok", "rule" },
        { "1234567890123456", "1234567890123456", "1234567890123456", "1234567890123456", "1234567890123456", "ok", "ok", "rule" },
        { "12345678901234567", "12345678901234567", "12345678901234567", "12345678901234567", "12345678901234567", "ok", "ok", "x" },
        { "0000000000001.25", "1.25", "1.25", "1.25", "1.25", "ok", "ok", "1.25" },
        { "00000000000001.25", "1.25", "1.25", "1.25", "1.25", "ok", "ok", "x" },
        // Length limits: risk 16, plan 64, manual facts 96, manual account value 100, adapter text 128, venue none.
        { One(16), "1.00000000000000", "1.00000000000000", "1.00000000000000", "1.00000000000000", "ok", "ok", "1.00000000000000" },
        { One(17), "1.000000000000000", "1.000000000000000", "1.000000000000000", "1.000000000000000", "ok", "ok", "x" },
        { One(64), "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "ok", "ok", "x" },
        { One(65), "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "ok", "x", "x" },
        { One(96), "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "1.0000000000000000000000000000", "ok", "x", "x" },
        { One(97), "1.0000000000000000000000000000", "1.0000000000000000000000000000", "x", "x", "ok", "x", "x" },
        { One(100), "1.0000000000000000000000000000", "1.0000000000000000000000000000", "x", "x", "ok", "x", "x" },
        { One(101), "1.0000000000000000000000000000", "x", "x", "x", "ok", "x", "x" },
        { One(128), "1.0000000000000000000000000000", "x", "x", "x", "ok", "x", "x" },
        { One(129), "1.0000000000000000000000000000", "x", "x", "x", "x", "x", "x" },
        { One(1000), "1.0000000000000000000000000000", "x", "x", "x", "x", "x", "x" },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Each_entry_point_accepts_exactly_todays_text(string input, string venue, string manualAccountValue,
        string manualQuantity, string manualFee, string adapterText, string planText, string riskPercent)
    {
        var expected = string.Join(" | ", venue, manualAccountValue, manualQuantity, manualFee, adapterText, planText, riskPercent);
        var actual = string.Join(" | ", Venue(input), await ManualAccountValue(input), ManualFact(input, positive: true),
            ManualFact(input, positive: false), AdapterText(input), PlanText(input), await RiskPercent(input));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Missing_text_is_rejected_where_it_can_arrive()
    {
        Assert.Equal("x", PlanText(null));
        Assert.Equal("x", ManualFact(null!, positive: true));
        Assert.Equal("x", ManualFact(null!, positive: false));
        Assert.Equal("x", await RiskPercent(null));
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

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("1", "10000000000000000000000000000")]
    [InlineData("1.50", "15000000000000000000000000000")]
    [InlineData("0.0000000000000000000000000001", "1")]
    [InlineData("79228162514264337593543950335", "792281625142643375935439503350000000000000000000000000000")]
    // Venue units are the magnitude: the sign is dropped.
    [InlineData("-1", "10000000000000000000000000000")]
    [InlineData("-0.5", "5000000000000000000000000000")]
    [InlineData("-79228162514264337593543950335", "792281625142643375935439503350000000000000000000000000000")]
    public void Venue_units_are_the_magnitude_at_28_places(string value, string units) =>
        Assert.Equal(BigInteger.Parse(units, CultureInfo.InvariantCulture), StrictJson.DecimalUnits(Value(value)));

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
