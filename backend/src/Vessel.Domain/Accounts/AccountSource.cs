using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Vessel.Domain.Accounts;

public static class AccountSource
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    public static bool IsAccountIndex(string? value) =>
        value is { Length: > 0 and <= 19 } &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
        index >= 0 && value == index.ToString(CultureInfo.InvariantCulture);

    public static bool IsEvmAddress([NotNullWhen(true)] string? value) =>
        value is { Length: 42 } && value.StartsWith("0x", StringComparison.Ordinal) &&
        value.AsSpan(2).IndexOfAnyExcept(HexDigits) < 0;

    /// <summary>Solana public identities are case-sensitive base58 encodings of exactly 32 bytes, not EVM addresses.</summary>
    public static bool IsSolanaAddress([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: >= 32 and <= 44 }) return false;
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var number = BigInteger.Zero;
        foreach (var character in value)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0) return false;
            number = number * 58 + digit;
        }
        var zeros = value.TakeWhile(c => c == '1').Count();
        return zeros + (number.IsZero ? 0 : number.GetByteCount(isUnsigned: true)) == 32;
    }
}
