using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class PaperPlayUseCasesTests : PaperPlayTestsValues
{

    [Fact]
    public void Play_inherits_immutable_mode_and_cannot_move_across_it()
    {
        var owner = Guid.NewGuid();
        var paper = PaperAccount(owner, Guid.NewGuid());
        var play = TestPlays.Create(paper, new("hyperliquid", "BTC"));
        Assert.Equal(TradingMode.Paper, play.TradingMode);
        Assert.Equal(TradingMode.Paper, PlayService.Summary(play).TradingMode);
        var real = new Account(Guid.NewGuid(), owner, "hyperliquid", "Real", TradingMode.Real);
        Assert.Throws<DomainRuleException>(() => play.MoveTo(real, new("hyperliquid", "BTC"), InstrumentSource.Venue, DateTimeOffset.UtcNow));
        Assert.Equal(paper.Id, play.AccountId);
    }
}
