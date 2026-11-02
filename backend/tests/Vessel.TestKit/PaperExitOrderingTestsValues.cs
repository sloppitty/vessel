using Vessel.Application.Paper;
using Vessel.Application.Plays;

namespace Vessel.Tests;

public partial class PaperExitOrderingTestsValues
{
    internal static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 5, TimeSpan.Zero);
    internal static readonly DateTimeOffset First = At.AddSeconds(55);
    internal static PaperCandle Candle(int minute, decimal open, decimal low, decimal high, decimal? close = null) =>
        new(First.AddMinutes(minute), First.AddMinutes(minute + 1), open, high, low, close ?? open);

    internal static (PlayPlanDocument Plan, PaperCandle[] Candles, decimal Pnl) Scenario(string role, bool shortSide, bool farFirst)
    {
        var near = new PlanExit("near", "price", role == "stop" ? shortSide ? "105" : "95" : shortSide ? "90" : "110", "50");
        var far = new PlanExit("far", "price", role == "stop" ? shortSide ? "110" : "90" : shortSide ? "80" : "120", "50");
        PlanExit[] levels = farFirst ? [far, near] : [near, far];
        var prior = new PlanExit("prior", "price", role == "stop" ? shortSide ? "90" : "110" : shortSide ? "110" : "90", "50");
        var plan = new PlayPlanDocument(shortSide ? "short" : "long", "quantity", "10", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", role == "stop" ? levels : [prior],
                role == "stop" ? [prior] : levels)], new("", "", "", ""));
        var origin = role == "stop" ? shortSide ? 90m : 110m : shortSide ? 110m : 90m;
        var final = role == "stop" ? shortSide ? 110m : 90m : shortSide ? 80m : 120m;
        return (plan, [Candle(0, 100, 99, 101), Candle(1, origin, origin - 1, origin + 1),
            Candle(2, origin, Math.Min(origin, final) - 1, Math.Max(origin, final) + 1, final)], role == "stop" ? 25 : 0);
    }
}
