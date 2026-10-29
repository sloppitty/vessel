using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class HyperliquidCanonicalMigrationVenuesTests : HyperliquidCanonicalMigrationTestsSupport
{

    [Fact]
    public void Canonical_migration_aliases_cover_the_runtime_allowlist()
    {
        foreach (var (native, canonical) in HyperliquidInstruments.CanonicalKeys)
            Assert.Contains($"('{native}', '{canonical}')", UpSql);
    }
}
