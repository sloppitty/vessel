using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Nethereum.Signer;
using Nethereum.Util;
using Vessel.Application.Common;

namespace Vessel.Identity;

// This port deliberately belongs to Identity, not to the journal's Application layer.
public interface IWalletSignatureVerifier
{
    void Verify(string message, string address, string? signature);
}

public sealed class WalletSignatures : IWalletSignatureVerifier
{
    private static readonly BigInteger Order = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private const string CounterfactualSuffix = "6492649264926492649264926492649264926492649264926492649264926492";

    public static string NormalizeAddress(string? address)
    {
        if (address is null || address.Length != 42 || !address.StartsWith("0x", StringComparison.Ordinal) ||
            !address.AsSpan(2).ContainsOnlyHex())
            throw IdentityErrors.Error(400, "address-invalid");
        var hex = address[2..];
        if (hex.Any(c => c is >= 'a' and <= 'f') && hex.Any(c => c is >= 'A' and <= 'F') &&
            !string.Equals(Checksum(address), address, StringComparison.Ordinal))
            throw IdentityErrors.Error(400, "address-invalid");
        return address.ToLowerInvariant();
    }

    public static string Checksum(string address) => AddressUtil.Current.ConvertToChecksumAddress(address);

    public void Verify(string message, string address, string? signature)
    {
        if (signature is null || !signature.StartsWith("0x", StringComparison.Ordinal) || signature.Length != 132 ||
            signature.EndsWith(CounterfactualSuffix, StringComparison.OrdinalIgnoreCase))
            throw IdentityErrors.Error(400, "signature-unsupported");
        byte[] bytes;
        try { bytes = Convert.FromHexString(signature[2..]); }
        catch (FormatException) { throw IdentityErrors.Error(400, "signature-invalid"); }
        var v = bytes[64];
        if (v is not (0 or 1 or 27 or 28)) throw IdentityErrors.Error(400, "signature-unsupported");
        var r = new BigInteger(bytes.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
        var s = new BigInteger(bytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (r < 1 || r >= Order || s < 1 || s >= Order) throw IdentityErrors.Error(400, "signature-invalid");
        var parity = v >= 27 ? v - 27 : v;
        if (s > Order / 2)
        {
            s = Order - s;
            bytes.AsSpan(32, 32).Clear();
            var canonical = s.ToByteArray(isUnsigned: true, isBigEndian: true);
            canonical.CopyTo(bytes.AsSpan(64 - canonical.Length));
            parity ^= 1;
        }
        bytes[64] = (byte)(parity + 27);
        string recovered;
        try { recovered = new EthereumMessageSigner().EncodeUTF8AndEcRecover(message, "0x" + Convert.ToHexString(bytes)); }
        catch (Exception)
        {
            // A bounded, structurally valid signature may still have no recoverable curve point.
            throw IdentityErrors.Error(400, "signature-invalid");
        }
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(recovered[2..]), Convert.FromHexString(address[2..])))
            throw IdentityErrors.Error(400, "signature-mismatch");
    }
}

internal static class HexCharacters
{
    internal static bool ContainsOnlyHex(this ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}

public static class SignInMessage
{
    public static string Create(string origin, string address, string nonce, DateTimeOffset issued, DateTimeOffset expires,
        string purpose = "sign-in", string? action = null, string? sessionWallet = null, string? targetWallet = null)
    {
        var domain = origin.StartsWith("http://", StringComparison.Ordinal) ? origin : new Uri(origin).Authority;
        var statement = purpose switch
        {
            "sign-in" => "Sign in to Vessel. This is not a transaction and costs no gas.",
            "link" when sessionWallet is not null =>
                $"Link this wallet to the Vessel journal signed in with {WalletSignatures.Checksum(sessionWallet)}. This is not a transaction.",
            "step-up" when action == "wallet.link" => "Confirm adding a sign-in wallet to your Vessel journal. This is not a transaction.",
            "step-up" when action == "device.pair" => "Confirm connecting a new device to your Vessel journal. This is not a transaction.",
            "step-up" when action == "wallet.remove" && targetWallet is not null =>
                $"Confirm removing sign-in wallet {WalletSignatures.Checksum(targetWallet)} from your Vessel journal. This is not a transaction.",
            _ => throw new ArgumentException("Invalid signature purpose.")
        };
        return $"{domain} wants you to sign in with your Ethereum account:\n{WalletSignatures.Checksum(address)}\n\n{statement}\n\n" +
            $"URI: {origin}/\nVersion: 1\nChain ID: 1\nNonce: {nonce}\nIssued At: {Format(issued)}\nExpiration Time: {Format(expires)}";
    }

    public static string Nonce()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        return string.Create(22, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

public static class IdentityErrors
{
    public static ProblemException StepUpRequired(string action)
    {
        var error = Error(403, "step-up-required");
        error.Data["action"] = action;
        return error;
    }

    public static ProblemException Error(int status, string code, int? retryAfter = null) =>
        new(status, code switch
        {
            "identity-unavailable" => "The identity service is unavailable. Try again later.",
            "rate-limited" => "Too many requests. Try again later.",
            "wallet-limit" => $"A user may have at most {IdentityLimits.MaxWalletsPerUser} linked wallets.",
            _ => "The identity request could not be completed."
        }, code, retryAfter);
}
