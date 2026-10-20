using System.Linq.Expressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

/// <summary>
/// One current discrepancy per account/kind/subject. Every change goes through <see cref="DiscrepancyLifecycle"/>.
/// Three independent aspects: the lifecycle <see cref="State"/> (candidate, open, resolved, dismissed); outstanding
/// confirmation work (<see cref="PendingFingerprint"/>); and the last public announcement (<see cref="PublishedTransition"/>).
/// </summary>
public sealed class PlayDiscrepancy : IOwned
{
    public const int MaxNoteLength = 2000;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public Guid? PlayId { get; set; }
    public Guid[] InvolvedPlayIds { get; set; } = [];
    public string ContractId { get; set; } = "";
    public string NativeContractId { get; set; } = "";
    public string SubjectKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "";
    public string State { get; set; } = DiscrepancyLifecycle.Candidate;
    /// <summary>The evidence baseline: of the confirmed, cleared or dismissed condition, or a candidate's latest observation.</summary>
    public string Fingerprint { get; set; } = "";
    public string Details { get; set; } = "{}";
    public DateTimeOffset OpenedAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public DateTimeOffset? DismissedAtUtc { get; set; }
    public string? Note { get; set; }
    public long Revision { get; set; }
    /// <summary>The last decisive (fresh present or absent) observation; the unobservable lifetime counts from here.</summary>
    public DateTimeOffset LastObservedAtUtc { get; set; }
    /// <summary>
    /// The condition awaiting (re-)confirmation, whatever the public state. It is durable monitoring work: it keeps the
    /// account checked until a fresh observation confirms or clears it.
    /// </summary>
    public string? PendingFingerprint { get; set; }
    /// <summary>Start of the current uninterrupted confirmation sequence; null after an unknown observation.</summary>
    public DateTimeOffset? PendingSinceUtc { get; set; }
    /// <summary>
    /// Set when outstanding work stayed unobservable beyond the monitor's lifetime: verification ended without a decision.
    /// Not a resolution. Cleared by the next fresh decision.
    /// </summary>
    public DateTimeOffset? UnverifiableAtUtc { get; set; }
    /// <summary>The last published transition (opened, resolved, dismissed, unverifiable); null if never announced.</summary>
    public string? PublishedTransition { get; set; }
    public long? PublishedRevision { get; set; }

    /// <summary>Anything ever announced stays visible.</summary>
    public bool Visible => PublishedTransition is not null;

    /// <summary>
    /// An outstanding announcement, the browser's incident: an announced record that is open, or that became unverifiable
    /// and was not dismissed since. The one predicate for the monitor summary's membership (translated by EF) and for
    /// listing incidents ahead of historical records.
    /// </summary>
    public static readonly Expression<Func<PlayDiscrepancy, bool>> IsOutstanding = d => d.PublishedTransition != null &&
        (d.State == DiscrepancyLifecycle.Open ||
         d.UnverifiableAtUtc != null && (d.DismissedAtUtc == null || d.DismissedAtUtc < d.UnverifiableAtUtc));
    private static readonly Func<PlayDiscrepancy, bool> OutstandingOf = IsOutstanding.Compile();
    /// <summary>See <see cref="IsOutstanding"/>.</summary>
    public bool Outstanding => OutstandingOf(this);
    /// <summary>The last announcement (opened or unverifiable) still awaits its matching close.</summary>
    public bool PublicOpen => PublishedTransition is DiscrepancyLifecycle.Opened or DiscrepancyLifecycle.Unverifiable;
    /// <summary>Scheduling work: a candidate, an open warning or a pending (re-)confirmation not yet unverifiable.</summary>
    public bool HasWork => UnverifiableAtUtc is null &&
        (State is DiscrepancyLifecycle.Candidate or DiscrepancyLifecycle.Open || PendingFingerprint is not null);
    /// <summary>Evaluation membership: its subject is still evaluated whenever the account is checked.</summary>
    public bool Tracked => State is DiscrepancyLifecycle.Candidate or DiscrepancyLifecycle.Open ||
        PendingFingerprint is not null || PublicOpen;
}
