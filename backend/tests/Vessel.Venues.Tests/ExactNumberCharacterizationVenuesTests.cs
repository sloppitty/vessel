using System.Globalization;
using System.Numerics;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Tests;

/// <summary>
/// Characterization of every exact-number entry point (backend review S1): the text each one accepts and the
/// strings it prints, quirks included. The tables are the contract. When an implementation moves, only the
/// bindings in the first section may change; a changed expectation is a behaviour change.
/// </summary>
public sealed class ExactNumberCharacterizationVenuesTests : ExactNumberCharacterizationTestsSupport
{

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

    /// <summary>Amount strings an account-discovery adapter returns.</summary>
    private static string AdapterText(string text) => ExactDecimal.IsSignedAmountText(text) ? "ok" : "x";

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
}
