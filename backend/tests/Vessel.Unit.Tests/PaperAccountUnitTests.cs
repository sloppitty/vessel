using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class PaperAccountUnitTests : PaperAccountTestsValues
{

    [Fact]
    public void Existing_constructors_default_to_real_and_paper_capital_defaults_without_losing_zero()
    {
        Assert.Equal("real", new Account(Guid.NewGuid(), Owner, "manual", "Real").TradingMode);
        Assert.Equal("real", new Portfolio(Guid.NewGuid(), Owner, "Real").TradingMode);
        var paper = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", "paper");
        Assert.Equal(4000m, paper.StartingCapitalUsd);
        Assert.Equal(0m, new Account(Guid.NewGuid(), Owner, "hyperliquid", "Zero", "paper", 0m).StartingCapitalUsd);
        Assert.Throws<DomainValidationException>(() => new Account(Guid.NewGuid(), Owner, "manual", "Real", startingCapitalUsd: 1m));
        Assert.Throws<DomainValidationException>(() => new Portfolio(Guid.NewGuid(), Owner, "Unknown", tradingMode: "unknown"));
    }

    [Fact]
    public void Paper_cannot_become_unassigned_accept_real_facts_or_sync()
    {
        var paper = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", "paper");
        var portfolio = Guid.NewGuid();
        paper.Configure(portfolio, null, null);
        Assert.Throws<DomainValidationException>(() => paper.Configure(null, null, null));
        Assert.Throws<DomainValidationException>(() => paper.Configure(portfolio, "0x" + new string('a', 40), null));
        Assert.Throws<DomainValidationException>(() => paper.Configure(portfolio, null, 0));
        Assert.Throws<DomainValidationException>(() => paper.Configure(portfolio, null, null, "0"));
        Assert.Throws<DomainValidationException>(() => paper.UpdateSettings("Unassigned", null, true));
        Assert.Throws<DomainValidationException>(() => paper.RecordSync(DateTimeOffset.UtcNow, "Real"));
        Assert.Throws<DomainValidationException>(() => paper.RecordSyncFailure());
        Assert.Throws<DomainValidationException>(() => paper.RecordOrderHistoryNotice("Real"));
        Assert.Equal(portfolio, paper.PortfolioId);
        Assert.Equal(4000m, paper.StartingCapitalUsd);
    }
}
