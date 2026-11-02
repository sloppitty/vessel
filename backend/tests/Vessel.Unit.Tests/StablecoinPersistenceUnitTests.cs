using Vessel.Domain.Numerics;

namespace Vessel.Tests;

public sealed class StablecoinPersistenceUnitTests
{
    [Theory]
    [InlineData("0.0000000000000000000000000001", "1", "1.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", "1", "79228162514264337593543950336")]
    [InlineData("1.5000", "2.50", "4")]
    public void Nominal_totals_do_not_round_or_overflow_exact_token_strings(string first, string second, string expected) =>
        Assert.Equal(expected, ExactDecimal.Sum([
            decimal.Parse(first, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(second, System.Globalization.CultureInfo.InvariantCulture)]));

    [Fact]
    public void Observed_empty_wallet_is_exact_zero() => Assert.Equal("0", ExactDecimal.Sum([]));
}
