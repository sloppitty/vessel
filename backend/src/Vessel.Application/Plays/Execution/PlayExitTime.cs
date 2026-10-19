using Vessel.Application.Sizing;
using Vessel.Domain.Plays;

namespace Vessel.Application.Plays.Execution;

/// <summary>
/// When linked executions closed a Play. A venue close is detected by a later check or refresh, which can find several
/// closures at once, so <see cref="Play.EndedAtUtc"/> (the detection time) cannot order outcomes. The outcome
/// calculator and the <c>play.closed</c> event read the latest valid exit instead; Review reads the unvalidated latest exit.
/// </summary>
public static class PlayExitTime
{
    /// <summary>The latest non-entry linked fill time, before any validation; null without an exit fill.</summary>
    public static DateTimeOffset? LatestExit(IEnumerable<LinkedFill> fills) =>
        fills.Where(f => f.Role != OrderLinkRole.Entry).Select(f => (DateTimeOffset?)f.Fill.OccurredAtUtc).Max();

    /// <summary>
    /// The latest valid linked exit: the latest exit fill of a Play its fills settle (<see cref="ExecutionIntegrity.Settled"/>),
    /// clamped to no later than <paramref name="detectedAt"/>, since a source timestamp after the check that saw it is no
    /// evidence that the Play closed later. Null when the fills cannot settle the Play; callers keep the detection time then.
    /// </summary>
    public static DateTimeOffset? LatestValidExit(ClosedPlayFacts facts, DateTimeOffset detectedAt) =>
        ExecutionIntegrity.Settled(facts) && LatestExit(facts.Fills) is { } exit ? exit <= detectedAt ? exit : detectedAt : null;
}
