using Vessel.Domain.Common;

namespace Vessel.Domain.Plays;

/// <summary>
/// The owner's one active "If …, then …" rule per trading mode (Phase 2, item 3), promoted from a next action or written.
/// Each change is a new version and archiving leaves none active; an earlier version, and every Play planned under it,
/// keeps the text it had.
/// </summary>
public sealed class PlayRule : IOwned
{
    public const int MaxPartLength = 500;

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string TradingMode { get; private set; } = null!;
    /// <summary>1 for the owner's first rule in this mode, then one more per change.</summary>
    public int Version { get; private set; }
    public string If { get; private set; } = null!;
    public string Then { get; private set; } = null!;
    /// <summary>The Play whose next action the rule was promoted from, in the same mode; null when written.</summary>
    public Guid? SourcePlayId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    /// <summary>When a newer version replaced it or the owner archived it; null while it is the active rule.</summary>
    public DateTimeOffset? ArchivedAtUtc { get; private set; }
    private PlayRule() { }

    public PlayRule(Guid ownerId, string tradingMode, int version, string @if, string then, Guid? sourcePlayId, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner ID must be nonempty.");
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        TradingMode = tradingMode;
        Version = version;
        If = Part(@if);
        Then = Part(then);
        SourcePlayId = sourcePlayId;
        CreatedAtUtc = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (ArchivedAtUtc is null) ArchivedAtUtc = now;
    }

    /// <summary>Each part is trimmed plain text of 1 to 500 characters; line breaks are kept.</summary>
    private static string Part(string text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length is 0 or > MaxPartLength || value.Any(c => char.IsControl(c) && c is not ('\r' or '\n')))
            throw new DomainValidationException($"A rule's If and Then are each plain text of 1 to {MaxPartLength} characters.");
        return value;
    }
}

/// <summary>The active rule as the owner saw it at Plan it, and the snapshot JSON kept with revision 1 (Phase 2, item 3).</summary>
public sealed record PlanRuleSeen(int Version, string If, string Then, string Snapshot);
