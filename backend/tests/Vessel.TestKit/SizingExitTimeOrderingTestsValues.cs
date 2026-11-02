using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class SizingExitTimeOrderingTestsValues
{
    internal static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    /// <summary>The one refresh that found every closure below.</summary>
    internal static readonly DateTimeOffset Detected = Planned.AddDays(1);
    internal readonly Guid owner = Guid.NewGuid();

    /// <summary>A long of one unit at 100 that exits <paramref name="exitMinutes"/> after planning with <paramref name="pnl"/>.</summary>
    internal ClosedPlayFacts Closed(decimal pnl, int exitMinutes, DateTimeOffset? detected = null)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        var plan = new PlayPlanDocument("long", "margin", "250", "3", null,
            [new PlanEntry("e", "e", "#b9c9e4", "100", "100", [new PlanExit("s", "price", "95", "100")], [new PlanExit("t", "price", "120", "100")])],
            new PlanNotes("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), Planned);
        var revision = play.MarkPlanned(Planned).Revision;
        play.MarkOpen("Filled.", Planned.AddMinutes(1));
        play.MarkClosed("Exited.", detected ?? Detected);
        ImportedFill Fill(string side, decimal price, decimal closed, int minutes) => new()
        {
            Id = Guid.NewGuid(), Price = price, Quantity = 1, ClosedPnlUsd = closed, Fee = 0, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
            OccurredAtUtc = Planned.AddMinutes(minutes), ExternalOrderId = "o" + minutes, ContractId = "BTC", SourceFillId = "f" + minutes, Side = side,
            PositionEffect = ExecutionFacts.Unknown, Direction = "", TransactionHash = "",
        };
        return new ClosedPlayFacts(play, [revision], [new(OrderLinkRole.Entry, Fill(ExecutionFacts.Buy, 100, 0, 1)),
            new(pnl > 0 ? OrderLinkRole.Target : OrderLinkRole.Stop, Fill(ExecutionFacts.Sell, 100 + pnl, pnl, exitMinutes))]);
    }
}
