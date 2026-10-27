using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Vessel.Identity;

public static class IdentityTokens
{
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    public static bool Valid(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool Matches(byte[]? hash, string? value) => hash is not null && Valid(value) &&
        CryptographicOperations.FixedTimeEquals(hash, Hash(value!));
}

public static class InvitationCodes
{
    internal const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Crockford folding shared with pairing codes: dashes and whitespace dropped, upper case, I and L read as 1, O as 0.</summary>
    internal static string Fold(string code) =>
        new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray())
            .Replace('I', '1').Replace('L', '1').Replace('O', '0');
    public static string Create()
    {
        var number = new BigInteger(RandomNumberGenerator.GetBytes(16), isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(number % 32)];
            number /= 32;
        }
        var code = new string(chars);
        return $"VSL-{code[..5]}-{code[5..10]}-{code[10..15]}-{code[15..20]}-{code[20..]}";
    }

    public static string Normalize(string? code)
    {
        if (code is null) throw IdentityErrors.Error(403, "invitation-required");
        if (code.StartsWith("VSL-", StringComparison.OrdinalIgnoreCase)) code = code[4..];
        code = Fold(code);
        if (code.Length != 26 || code.Any(c => !Alphabet.Contains(c))) throw IdentityErrors.Error(403, "invitation-invalid");
        return code;
    }
}

public static class IdentityText
{
    public static string DisplayName(string? text) => Clean(text, 60, false, "display-name-invalid")!;
    public static string? Label(string? text) => Clean(text, 40, true, "label-invalid");
    public static string? Note(string? text) => Clean(text, 80, true, "request-invalid");

    private static string? Clean(string? text, int max, bool nullable, string code)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) && nullable) return null;
        if (string.IsNullOrEmpty(text) || text.Length > max || !Printable(text))
            throw IdentityErrors.Error(400, code);
        return text;
    }

    private static bool Printable(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune) || Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
                UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
            i += rune.Utf16SequenceLength;
        }
        return true;
    }

    public static string? UserAgent(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent) || agent.Length > 4096) return null;
        var browser = agent.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox" :
            agent.Contains("Edg/", StringComparison.Ordinal) ? "Edge" :
            agent.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome" :
            agent.Contains("Safari/", StringComparison.Ordinal) ? "Safari" : null;
        var os = agent.Contains("Android", StringComparison.Ordinal) ? "Android" :
            agent.Contains("iPhone", StringComparison.Ordinal) || agent.Contains("iPad", StringComparison.Ordinal) ? "iOS" :
            agent.Contains("Windows", StringComparison.Ordinal) ? "Windows" :
            agent.Contains("Macintosh", StringComparison.Ordinal) ? "macOS" :
            agent.Contains("Linux", StringComparison.Ordinal) ? "Linux" : null;
        return browser is not null && os is not null ? $"{browser} on {os}" : null;
    }
}
