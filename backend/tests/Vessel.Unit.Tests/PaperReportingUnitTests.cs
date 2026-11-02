using Vessel.Domain.Paper;

namespace Vessel.Tests;

public sealed class PaperReportingUnitTests : PaperReportingTestsValues
{

    [Fact]
    public void Initial_risk_uses_first_execution_revision_not_latest_or_detection_time()
    {
        var paper = Paper(owner, revise: true);
        var original = paper.Facts.Paper!;
        Assert.Equal(2, paper.Facts.Play.PlanRevision);
        var outcome = original.Outcome(At.AddDays(1), out _)!;
        Assert.Equal((At, 20m, 5m, 4m), (outcome.ClosedAtUtc, outcome.NetResult, outcome.PlannedRisk, outcome.RMultiple));
    }

    [Theory]
    [InlineData("exit-before-entry")]
    [InlineData("over-close")]
    [InlineData("foreign-owner")]
    [InlineData("duplicate")]
    [InlineData("future")]
    [InlineData("same-candle")]
    public void Invalid_paper_execution_proof_is_never_scored(string invalid)
    {
        var paper = Paper(owner).Facts.Paper!;
        var entry = paper.Executions[0]; var exit = paper.Executions[1];
        if (invalid == "exit-before-entry") entry.OccurredAtUtc = At.AddMinutes(5);
        if (invalid == "over-close") exit.Quantity = 2;
        if (invalid == "foreign-owner") exit.OwnerId = Guid.NewGuid();
        if (invalid == "future") exit.OccurredAtUtc = At.AddDays(5);
        if (invalid == "same-candle")
        {
            exit.OccurredAtUtc = entry.OccurredAtUtc; exit.CandleOpenAtUtc = entry.CandleOpenAtUtc; exit.CandleCloseAtUtc = entry.CandleCloseAtUtc;
        }
        if (invalid == "duplicate") exit.Id = entry.Id;
        Assert.Null(paper.Outcome(At.AddDays(1), out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Opposing_stop_and_target_in_one_candle_cannot_become_a_scored_win()
    {
        var paper = Paper(owner).Facts.Paper!;
        var exit = paper.Executions[1]; exit.Quantity = .5m; exit.PnlUsd = 10; exit.MarginUsd = -50;
        var stop = new PaperExecution
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = paper.Play.AccountId, PlayId = paper.Play.Id,
            EntryId = exit.EntryId, LevelId = "stop", Role = "stop", Price = 95, Quantity = .5m, PnlUsd = -2.5m,
            MarginUsd = -50, PlanVersion = exit.PlanVersion, OccurredAtUtc = exit.OccurredAtUtc,
            CandleOpenAtUtc = exit.CandleOpenAtUtc, CandleCloseAtUtc = exit.CandleCloseAtUtc
        };
        Assert.Null((paper with { Executions = paper.Executions.Append(stop).ToList() }).Outcome(At.AddDays(1), out var problem));
        Assert.Contains("opposing", problem);
    }
}
