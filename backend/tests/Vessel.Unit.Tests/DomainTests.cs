using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Distinct_active_plays_can_share_an_account_and_perpetual_contract()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Trading");
        var instrument = new PerpetualInstrument("hyperliquid", "BTC-PERP");
        var first = TestPlays.Create(account, instrument);
        var second = TestPlays.Create(account, instrument);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Instrument, second.Instrument);
        Assert.Equal(account.OwnerId, first.OwnerId);
        Assert.Equal(PlayStatus.Draft, first.Status);
        first.MarkPlanned(DateTimeOffset.UnixEpoch);
        second.MarkPlanned(DateTimeOffset.UnixEpoch);
        Assert.Equal(PlayStatus.Planned, first.Status);
        Assert.Equal(PlayStatus.Planned, second.Status);
    }

    [Fact]
    public void Venue_is_part_of_contract_identity()
    {
        Assert.NotEqual(new PerpetualInstrument("hyperliquid", "BTC"), new PerpetualInstrument("lighter", "BTC"));
    }

    [Fact]
    public void Play_rejects_a_contract_from_another_venue()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Trading");
        Assert.Throws<ArgumentException>(() => TestPlays.Create(account,
            new PerpetualInstrument("hyperliquid", "BTC")));
    }

    [Theory]
    [InlineData("", "BTC")]
    [InlineData("manual", " ")]
    public void Instrument_requires_both_identity_parts(string venue, string contract)
    {
        Assert.Throws<ArgumentException>(() => new PerpetualInstrument(venue, contract));
    }

    [Fact]
    public void Account_requires_owner_identity()
    {
        Assert.Throws<ArgumentException>(() => new Account(Guid.NewGuid(), Guid.Empty, "manual", "Trading"));
    }

    [Fact]
    public void Play_requires_distinct_identity()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Trading");
        Assert.Throws<ArgumentException>(() => new Play(Guid.Empty, account, new PerpetualInstrument("manual", "BTC"), InstrumentSource.Manual, "{}", DateTimeOffset.UnixEpoch));
    }
}
