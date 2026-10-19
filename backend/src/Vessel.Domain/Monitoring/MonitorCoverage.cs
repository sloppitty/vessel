using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

/// <summary>
/// Stop-coverage states. <see cref="Known"/> carries whether the stops cover the open quantity; <see cref="Shared"/> means a
/// whole-position stop covers this Play together with others on the instrument; <see cref="Unknown"/> and
/// <see cref="Unsupported"/> carry their reason. <see cref="Stale"/> is never stored: reads project it from the check time.
/// </summary>
public static class MonitorCoverageStates
{
    public const string Known = "known";
    public const string Shared = "shared";
    public const string Unknown = "unknown";
    public const string Unsupported = "unsupported";
    public const string Stale = "stale";
}

/// <summary>Evidence for one planned stop level, from the last check's current order book.</summary>
public static class MonitorStopEvidence
{
    /// <summary>
    /// A linked closing stop for the level rested in the current book at the observation time, at the planned price
    /// within the matcher's step.
    /// </summary>
    public const string Resting = "resting";
    /// <summary>A linked closing stop for the level rested in the current book, at another price than the planned one.</summary>
    public const string RestingElsewhere = "resting-elsewhere";
    /// <summary>A complete, fresh current book had no resting order linked to the level.</summary>
    public const string NotSeen = "not-seen";
    /// <summary>
    /// The book was incomplete, stale or inconsistent, or the resting linked order is not a recognized closing stop, so the
    /// level's stop could be neither confirmed nor ruled out.
    /// </summary>
    public const string Unknown = "unknown";
    /// <summary>The venue's order reader does not return the current book.</summary>
    public const string NotRead = "not-read";
}

/// <summary>
/// The last stop-coverage outcome the monitor recorded for one Open Play on its account and instrument, with the check
/// time and a reason. Replaced by every evaluated check of the account; never corrected, never a discrepancy by itself.
/// </summary>
public sealed class MonitorCoverage : IOwned
{
    public Guid PlayId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>The Play's canonical contract.</summary>
    public string ContractId { get; set; } = "";
    public string State { get; set; } = MonitorCoverageStates.Unknown;
    /// <summary>Whether the resting stops cover the open quantity; set only when <see cref="State"/> is known.</summary>
    public bool? Covered { get; set; }
    /// <summary>A short reason code, e.g. <c>stop-orders</c>, <c>opposing-plays</c> or <c>current-book-not-read</c>.</summary>
    public string Reason { get; set; } = "";
    public DateTimeOffset CheckedAtUtc { get; set; }
    /// <summary>Per planned stop level evidence as JSON: entry, level, evidence, order, observation time, acting and planned price.</summary>
    public string Stops { get; set; } = "[]";
}
