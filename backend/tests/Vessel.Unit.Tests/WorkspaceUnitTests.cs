using Vessel.Domain.Numerics;

namespace Vessel.Tests;

public sealed class WorkspaceUnitTests
{
    [Theory]
    [InlineData("0", true)]
    [InlineData("001.2300", true)]
    [InlineData("79228162514264337593543950335", true)]
    [InlineData("0.0000000000000000000000000001", true)]
    [InlineData("1.00000000000000000000000000001", false)]
    [InlineData("79228162514264337593543950336", false)]
    [InlineData("-1", false)]
    [InlineData("1e3", false)]
    [InlineData("1,234", false)]
    [InlineData("NaN", false)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData(" 1", false)]
    public void Manual_decimal_is_exact_and_invariant(string input, bool valid) =>
        Assert.Equal(valid, ExactDecimal.TryParseOwnerAmount(input, out _));
}
