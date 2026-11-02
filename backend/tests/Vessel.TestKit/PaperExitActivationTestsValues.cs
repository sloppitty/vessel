using System.Globalization;
using Vessel.Application.Paper;
using Vessel.Application.Plays;

namespace Vessel.Tests;

public partial class PaperExitActivationTestsValues
{
    internal static readonly DateTimeOffset Saved = new(2026, 10, 21, 12, 0, 5, TimeSpan.Zero);
    internal static readonly DateTimeOffset First = Saved.AddSeconds(55);
    internal static PaperCandle C(int minute, decimal open, decimal low, decimal high, decimal? close = null, int trades = 1) =>
        new(First.AddMinutes(minute), First.AddMinutes(minute + 1), open, high, low, close ?? open, trades);
    internal static PlanExit X(string id, decimal price, string share = "50") => new(id, "price", price.ToString(CultureInfo.InvariantCulture), share);
    internal static PlanEntry E(string id, string share, IReadOnlyList<PlanExit>? stops = null, IReadOnlyList<PlanExit>? targets = null) =>
        new(id, id, "#ffffff", share, "100", stops ?? [], targets ?? []);
    internal static PlayPlanDocument P(bool shortSide, IReadOnlyList<PlanEntry> entries) =>
        new(shortSide ? "short" : "long", "quantity", "10", "1", null, entries, new("", "", "", ""));
    internal static (PlayPlanDocument Old, PlayPlanDocument Current, PaperCandle[] Prefix, PaperCandle Last, decimal ExpectedPnl) Dormant(
        string role, bool shortSide, bool farFirst)
    {
        var down = role == "stop" != shortSide;
        var near = X("near", down ? 95 : 105);
        var far = X("far", down ? 90 : 110);
        if (role == "target") { near = X("near", down ? 90 : 110); far = X("far", down ? 80 : 120); }
        var prior = X("prior", role == "stop" ? shortSide ? 90 : 110 : shortSide ? 110 : 90);
        PlanExit[] exits = farFirst ? [far, near] : [near, far];
        var current = P(shortSide, [E("e", "100", role == "stop" ? exits : [prior], role == "stop" ? [prior] : exits)]);
        var old = current with { Entries = [current.Entries[0] with
        {
            Stops = current.Entries[0].Stops.Select(x => x.Id == "near" ? x with { Share = "0" } : x).ToArray(),
            Targets = current.Entries[0].Targets.Select(x => x.Id == "near" ? x with { Share = "0" } : x).ToArray()
        }] };
        var origin = decimal.Parse(prior.Value, CultureInfo.InvariantCulture);
        var end = decimal.Parse(far.Value, CultureInfo.InvariantCulture) + (down ? -5 : 5);
        var nearPrice = decimal.Parse(near.Value, CultureInfo.InvariantCulture);
        return (old, current, [C(0, 100, 99, 101), C(1, origin, origin - 1, origin + 1)],
            C(2, end, Math.Min(end, nearPrice) - 1, Math.Max(end, nearPrice) + 1, nearPrice), role == "stop" ? 0 : 50);
    }
}
