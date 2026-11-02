using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class PaperReportingTestsValues
{
    internal static readonly DateTimeOffset At = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    internal readonly Guid owner = Guid.NewGuid();

    internal static (Account Account, Portfolio Portfolio, ClosedPlayFacts Facts) Paper(Guid owner, decimal net = 20,
        string state = "ready", string instrument = "BTC", string[]? issues = null, decimal quantity = 1, bool revise = false)
    {
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper account", "paper", 4000);
        account.Configure(portfolio.Id, null, null);
        var plan = new PlayPlanDocument("long", "margin", "100", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "95", "100")], [])], new("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", instrument),
            InstrumentSource.Venue, PlayDocuments.Serialize(plan), At.AddHours(-3));
        var revision = play.MarkPlanned(At.AddHours(-3)).Revision;
        play.ApplyPaperExecutionState(true, false, At.AddHours(-2));
        List<PlayPlanRevision> revisions = [revision];
        if (revise)
        {
            var revised = plan with { Entries = [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "80", "100")], [])] };
            revisions.Add(play.ChangePlan(PlayDocuments.Serialize(revised), "Later risk", At.AddHours(-1))!);
        }
        play.ApplyPaperExecutionState(true, true, At.AddHours(1));
        PaperExecution Execution(string role, DateTimeOffset at) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, PlayId = play.Id, EntryId = "e",
            LevelId = role == "entry" ? null : "target", Role = role, Price = role == "entry" ? 100 : 100 + net,
            Quantity = quantity, PnlUsd = role == "entry" ? 0 : net * quantity, MarginUsd = role == "entry" ? 100 : -100,
            PlanVersion = revision.Number, OccurredAtUtc = at, CandleOpenAtUtc = at.AddMinutes(-1), CandleCloseAtUtc = at
        };
        var paper = new PaperPlayFacts(play, revisions, [Execution("entry", At.AddHours(-2)), Execution("target", At)], state, issues ?? []);
        return (account, portfolio, new(play, revisions, []) { Paper = paper });
    }
}
