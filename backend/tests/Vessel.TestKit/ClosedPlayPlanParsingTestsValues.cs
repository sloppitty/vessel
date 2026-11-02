using System.Globalization;
using System.Text.Json;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class ClosedPlayPlanParsingTestsValues
{
    internal static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z", CultureInfo.InvariantCulture);
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static PlayPlanDocument Plan(string stop, string direction = "long") => new(direction, "margin", "250", "3", null,
        [new PlanEntry("entry-1", "entry-1", "#b9c9e4", "100", "100", [new PlanExit("stop-1", "price", stop, "100")],
            [new PlanExit("t", "price", direction == "long" ? "120" : "80", "100")])], new PlanNotes("", "", "", ""));

    internal static ImportedFill Fill(string side, decimal price, decimal pnl, int minutes) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = 2, ClosedPnlUsd = pnl, Fee = 0.1m, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
        OccurredAtUtc = Planned.AddMinutes(minutes), ExternalOrderId = "o" + minutes, ContractId = "BTC", SourceFillId = "f" + minutes, Side = side,
        PositionEffect = ExecutionFacts.Unknown, Direction = "", TransactionHash = "",
    };

    /// <summary>Revision 1 stops at 95; revision 2, two hours after planning, at 90; the current plan then moves to 85.</summary>
    internal static ClosedPlayFacts Facts(int entryMinutes, bool retrospective = false)
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(Plan("95")), Planned);
        List<PlayPlanRevision> revisions = [play.MarkPlanned(Planned).Revision];
        revisions.Add(play.ChangePlan(PlayDocuments.Serialize(Plan("90")), "Moved stop", Planned.AddHours(2))!);
        play.MarkOpen("Filled.", Planned.AddHours(3));
        play.ChangePlan(PlayDocuments.Serialize(Plan("85")), "Trailed", Planned.AddHours(4));
        play.MarkClosed("Exited.", Planned.AddDays(1));
        if (retrospective) typeof(Play).GetProperty(nameof(Play.Origin))!.SetValue(play, PlayOrigin.Retrospective);
        return new ClosedPlayFacts(play, revisions,
            [new(OrderLinkRole.Entry, Fill(ExecutionFacts.Buy, 100, 0, entryMinutes)), new(OrderLinkRole.Target, Fill(ExecutionFacts.Sell, 110, 20, entryMinutes + 600))]);
    }

    internal static string Json(object? value) => JsonSerializer.Serialize(value, Web);
}
