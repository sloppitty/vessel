using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

internal static class EvidenceFixtures
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 5, 6];
    public static readonly byte[] WebP = [.. "RIFF"u8, 9, 0, 0, 0, .. "WEBP"u8, 7];

    public static Play PlayFor(Guid owner) =>
        TestPlays.Create(new Account(Guid.NewGuid(), owner, "hyperliquid", "Account"), new PerpetualInstrument("hyperliquid", "BTC"));
}
