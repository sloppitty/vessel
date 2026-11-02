using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class PaperPlayTests : PaperPlayTestsValues
{

    [Fact]
    public void Paper_lifecycle_has_explicit_provenance_and_rejects_actual_execution_paths()
    {
        var account = PaperAccount(Guid.NewGuid(), Guid.NewGuid());
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        play.MarkPlanned(now);
        Assert.Throws<DomainRuleException>(() => play.MarkOpen("venue", now));
        Assert.Throws<DomainRuleException>(() => play.RecordManualOpen(now));
        var opened = play.ApplyPaperExecutionState(true, false, now.AddMinutes(1));
        Assert.Equal(StatusChangeSource.Paper, opened!.Source);
        Assert.Equal(PlayStatus.Open, play.Status);
        Assert.Throws<DomainRuleException>(() => play.RecordManualClose(now, now));
        var closed = play.ApplyPaperExecutionState(true, true, now.AddMinutes(2));
        Assert.Equal(StatusChangeSource.Paper, closed!.Source);
        Assert.Equal(now.AddMinutes(2), play.EndedAtUtc);
        Assert.Null(play.ApplyPaperExecutionState(true, true, now.AddMinutes(3)));
        Assert.Equal(now.AddMinutes(2), play.EndedAtUtc);
    }

    [Fact]
    public void Real_lifecycle_does_not_accept_paper_proof()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Real");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        play.MarkPlanned(DateTimeOffset.UnixEpoch);
        Assert.Throws<DomainRuleException>(() => play.ApplyPaperExecutionState(true, false, DateTimeOffset.UnixEpoch));
        Assert.Equal(PlayStatus.Planned, play.Status);
    }

    [Fact]
    public void Paper_cannot_create_retrospective_actual_execution_provenance()
    {
        var account = PaperAccount(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<DomainRuleException>(() => Play.Retrospective(Guid.NewGuid(), account, new("hyperliquid", "BTC"),
            PlayDocuments.Serialize(TestPlays.Plan()), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Fake actual"));
    }
}
