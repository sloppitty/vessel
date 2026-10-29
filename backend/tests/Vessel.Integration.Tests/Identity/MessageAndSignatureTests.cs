using System.Globalization;
using System.Numerics;
using System.Text;
using System.Security.Cryptography;
using Nethereum.Signer;
using Vessel.Application.Common;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class MessageAndSignatureTests
{
    private const string Address = "0x52908400098527886E0F7030069857D2E4169EE7";
    private const string Nonce = "0123456789ABCDEFGHIJKL";

    [Theory]
    [InlineData("https://vessel.test", "vessel.test")]
    [InlineData("https://vessel.test:8443", "vessel.test:8443")]
    [InlineData("http://100.123.45.42:5180", "http://100.123.45.42:5180")]
    [InlineData("http://localhost", "http://localhost")]
    public void Sign_in_message_is_exact_UTF8_LF_without_trailing_newline(string origin, string domain)
    {
        var issued = DateTimeOffset.Parse("2026-10-17T09:41:12.123Z", CultureInfo.InvariantCulture);
        var expected = $"{domain} wants you to sign in with your Ethereum account:\n{Address}\n\n" +
            "Sign in to Vessel. This is not a transaction and costs no gas.\n\n" +
            $"URI: {origin}/\nVersion: 1\nChain ID: 1\nNonce: {Nonce}\n" +
            "Issued At: 2026-10-17T09:41:12.123Z\nExpiration Time: 2026-10-17T09:46:12.123Z";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(
            SignInMessage.Create(origin, Address, Nonce, issued, issued.AddMinutes(5))));
    }

    [Theory]
    [InlineData("link", null, "Link this wallet to the Vessel journal signed in with " + Address + ". This is not a transaction.")]
    [InlineData("step-up", "wallet.link", "Confirm adding a sign-in wallet to your Vessel journal. This is not a transaction.")]
    [InlineData("step-up", "wallet.remove", "Confirm removing sign-in wallet " + Address + " from your Vessel journal. This is not a transaction.")]
    public void Other_purposes_have_exact_statements(string purpose, string? action, string statement)
    {
        var now = DateTimeOffset.Parse("2026-10-17T09:41:12.000Z", CultureInfo.InvariantCulture);
        var expected = $"vessel.test wants you to sign in with your Ethereum account:\n{Address}\n\n{statement}\n\n" +
            "URI: https://vessel.test/\nVersion: 1\nChain ID: 1\nNonce: " + Nonce +
            "\nIssued At: 2026-10-17T09:41:12.000Z\nExpiration Time: 2026-10-17T09:46:12.000Z";
        Assert.Equal(expected, SignInMessage.Create("https://vessel.test", Address, Nonce, now, now.AddMinutes(5),
            purpose, action, Address, Address));
    }

    [Fact]
    public void Recovery_accepts_both_parities_and_all_v_encodings_and_high_s()
    {
        var verifier = new WalletSignatures();
        var key = EthECKey.GenerateKey();
        var found = new HashSet<byte>();
        for (var i = 0; found.Count < 2 && i < 100; i++)
        {
            var message = "Synthetic message " + i;
            var signature = IdentityDatabase.Sign(key, message);
            var bytes = Convert.FromHexString(signature[2..]);
            var parity = (byte)(bytes[64] >= 27 ? bytes[64] - 27 : bytes[64]);
            found.Add(parity);
            foreach (var v in new[] { parity, (byte)(parity + 27) })
            {
                bytes[64] = v;
                verifier.Verify(message, key.GetPublicAddress(), "0x" + Convert.ToHexString(bytes));
            }
            var n = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", NumberStyles.HexNumber);
            var s = new BigInteger(bytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
            var high = (n - s).ToByteArray(isUnsigned: true, isBigEndian: true);
            bytes.AsSpan(32, 32).Clear();
            high.CopyTo(bytes.AsSpan(64 - high.Length));
            bytes[64] = (byte)((parity ^ 1) + 27);
            verifier.Verify(message, key.GetPublicAddress(), "0x" + Convert.ToHexString(bytes));
        }
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Signature_error_codes_describe_format_recovery_and_address_separately()
    {
        var key = EthECKey.GenerateKey();
        var signature = IdentityDatabase.Sign(key, "synthetic");
        var verifier = new WalletSignatures();
        void Error(string value, string code, string? address = null) =>
            Assert.Equal(code, Assert.Throws<ProblemException>(() => verifier.Verify("synthetic", address ?? key.GetPublicAddress(), value)).Code);
        Error(signature + string.Concat(Enumerable.Repeat("6492", 16)), "signature-unsupported");
        Error("0x1234", "signature-unsupported");
        Error(signature[..^2] + "02", "signature-unsupported");
        Error("0x" + new string('z', 130), "signature-invalid");
        Error("0x" + new string('0', 128) + "1b", "signature-invalid");
        Error(signature, "signature-mismatch", EthECKey.GenerateKey().GetPublicAddress());
    }

    [Fact]
    public void Unrecoverable_curve_points_never_escape_the_signature_boundary()
    {
        var verifier = new WalletSignatures();
        var address = EthECKey.GenerateKey().GetPublicAddress();
        for (var i = 0; i < 100; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(65);
            bytes[64] = 27;
            var error = Assert.Throws<ProblemException>(() => verifier.Verify("Synthetic", address, "0x" + Convert.ToHexString(bytes)));
            Assert.Contains(error.Code, new[] { "signature-invalid", "signature-mismatch" });
        }
    }

    [Theory]
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7")]
    [InlineData("0x52908400098527886e0f7030069857d2e4169ee7")]
    public void All_upper_and_lower_addresses_are_accepted(string address) => Assert.Equal(Address.ToLowerInvariant(), WalletSignatures.NormalizeAddress(address));

    [Fact]
    public void Bad_mixed_case_checksum_is_refused()
    {
        Assert.Equal("address-invalid", Assert.Throws<ProblemException>(() => WalletSignatures.NormalizeAddress(
            "0x52908400098527886E0F7030069857D2E4169Ee7")).Code);
        var nonces = Enumerable.Range(0, 100).Select(_ => SignInMessage.Nonce()).ToArray();
        Assert.All(nonces, nonce => Assert.Matches("^[a-zA-Z0-9]{22}$", nonce));
        Assert.Equal(100, nonces.Distinct().Count());
    }

    [Fact]
    public void Invitation_codes_have_128_bits_and_normalize_aliases()
    {
        var code = InvitationCodes.Create();
        Assert.Matches("^VSL-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{6}$", code);
        Assert.Equal(26, InvitationCodes.Normalize(code).Length);
        Assert.Equal(InvitationCodes.Normalize(code), InvitationCodes.Normalize(code.ToLowerInvariant()));
        Assert.Equal("110" + new string('A', 23), InvitationCodes.Normalize("vsl-i l-o" + new string('a', 23)));
    }

    [Theory]
    [InlineData(" \u200bname")]
    [InlineData("name\nvalue")]
    [InlineData("")]
    public void Nonprintable_or_blank_names_are_rejected(string value) =>
        Assert.Equal("display-name-invalid", Assert.Throws<ProblemException>(() => IdentityText.DisplayName(value)).Code);
}
