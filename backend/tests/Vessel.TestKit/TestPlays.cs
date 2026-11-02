using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

internal static class TestPlays
{
    public static PlayPlanDocument Plan(string price = "100", string stop = "95") => new("long", "margin", "250", "3", null,
        [new PlanEntry("entry-1", "Entry 1", "#b9c9e4", "100", price, [new PlanExit("stop-1", "price", stop, "100")],
            [new PlanExit("target-1", "price", "110", "100")])],
        new PlanNotes("Breakout retest", "Close below 94", "", ""));

    public static Play Create(Account account, PerpetualInstrument? instrument) => new(Guid.NewGuid(), account, instrument,
        account.VenueId == "manual" ? InstrumentSource.Manual : InstrumentSource.Venue, PlayDocuments.Serialize(Plan()), DateTimeOffset.UnixEpoch);

    /// <summary>
    /// A plan an earlier server stored before plan numbers had to be plain decimals: an exponent entry price, a
    /// leading-dot size and a negative percentage stop (which then resolved to no stop).
    /// </summary>
    public static PlayPlanDocument LegacyPlan() => Plan("1e2") with
    {
        Size = ".5",
        Entries = [Plan("1e2").Entries[0] with { Stops = [new PlanExit("stop-1", "percent", "-5", "100")] }]
    };
}
