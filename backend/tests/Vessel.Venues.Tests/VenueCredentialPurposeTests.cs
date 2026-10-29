using System.Text;
using Vessel.Application.Credentials;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

/// <summary>
/// The purpose is AES-GCM associated data: the shared helper must produce exactly the literal every existing
/// envelope was sealed with.
/// </summary>
public sealed class VenueCredentialPurposeTests
{
    [Theory]
    [InlineData("lighter", "lighter-read-token")]
    [InlineData("lighter-rh", "lighter-rh-read-token")]
    [InlineData("hyperliquid", "hyperliquid-read-token")]
    [InlineData("risex", "risex-read-token")]
    [InlineData("index-fixture", "index-fixture-read-token")]
    public void Helper_yields_the_historical_bytes(string venueId, string stored)
    {
        Assert.Equal(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(VenueCredentialPurpose.ReadToken(venueId)));
        // The pre-helper expression, kept here as the reference.
        Assert.Equal(venueId + "-read-token", VenueCredentialPurpose.ReadToken(venueId));
    }

    [Fact]
    public void Every_registered_venue_and_lighter_exchange_uses_the_helper()
    {
        Assert.Equal("lighter-read-token", LighterExchange.Mainnet.CredentialPurpose);
        Assert.Equal("lighter-rh-read-token", LighterExchange.Robinhood.CredentialPurpose);
        foreach (var id in new[] { LighterExchange.Mainnet.VenueId, LighterExchange.Robinhood.VenueId, HyperliquidPerpetualReader.Id, RisexReader.Id })
            Assert.Equal(id + "-read-token", VenueCredentialPurpose.ReadToken(id));
    }
}
