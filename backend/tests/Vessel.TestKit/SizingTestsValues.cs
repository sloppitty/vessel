using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class SizingServiceTestsValues
{
    internal static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    internal readonly Guid owner = Guid.NewGuid();

    internal static PlayPlanDocument Plan(string leverage = "3", params PlanEntry[] entries) => new("long", "margin", "250", leverage, null,
        entries.Length > 0 ? entries : [Entry("entry-1", "100", "100", new PlanExit("stop-1", "price", "95", "100"))],
        new PlanNotes("", "", "", ""));

    internal static PlanEntry Entry(string id, string share, string price, params PlanExit[] stops) =>
        new(id, id, "#b9c9e4", share, price, stops, [new PlanExit(id + "-target", "price", "120", "100")]);

    internal static ImportedFill Fill(decimal price, decimal quantity, decimal pnl = 0, decimal fee = 0.1m, string token = "USDC", int minutes = 0,
        string pnlBasis = ExecutionFacts.PnlGross, string side = ExecutionFacts.Buy, string effect = ExecutionFacts.Unknown) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = quantity, ClosedPnlUsd = pnl, Fee = fee, FeeToken = token, PnlBasis = pnlBasis,
        OccurredAtUtc = Planned.AddMinutes(30 + minutes), ExternalOrderId = "o", ContractId = "BTC", SourceFillId = "f", Side = side, PositionEffect = effect,
        Direction = "", TransactionHash = "",
    };

    internal ClosedPlayFacts Closed(PlayPlanDocument plan, IEnumerable<LinkedFill> fills, PlayPlanDocument? revised = null, int closedDay = 2)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), Planned);
        List<PlayPlanRevision> revisions = [play.MarkPlanned(Planned).Revision];
        if (revised is not null) revisions.Add(play.ChangePlan(PlayDocuments.Serialize(revised), "Moved stop", Planned.AddHours(2))!);
        play.MarkOpen("Filled.", Planned.AddHours(3));
        play.MarkClosed("Exited.", Planned.AddDays(closedDay));
        return new ClosedPlayFacts(play, revisions, fills.ToList());
    }
}
