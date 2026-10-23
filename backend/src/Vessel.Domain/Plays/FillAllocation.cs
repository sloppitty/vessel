using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Domain.Plays;

/// <summary>Why the owner split a fill (Phase 2, item 11): a shared close, a flip, or another reason the note gives.</summary>
public enum AllocationReason { SharedClose, Flip, Other }

/// <summary>What a line's quantity does for its Play: closes part of it, or opens it (a flip's second half).</summary>
public enum AllocationRole { Exit, Entry }

/// <summary>
/// One line the owner asks for: a Play with its direction (<c>long</c> or <c>short</c>), the role it takes, the plan entry an
/// entry line fills, and an exact quantity.
/// </summary>
public sealed record AllocationLineRequest(Guid PlayId, string Direction, AllocationRole Role, string? EntryId, decimal Quantity);

/// <summary>
/// The owner's split of one imported fill between Plays (Phase 2, item 11): a shared exit across the Plays it closed, or a
/// flip into one Play's exit and another Play's entry. Lines conserve the fill's quantity exactly, and its fee is shared pro
/// rata with a disclosed remainder. A fill has at most one allocation; each change is the next revision, kept in
/// <see cref="FillAllocationRevision"/>. It never changes the fill, an order or an order link.
/// </summary>
public sealed class FillAllocation : IOwned
{
    public const int MaxLines = 20, MaxNoteLength = 2000;
    /// <summary>Fee shares keep at least this many decimal places, the USDC unit, or the fee's own when it has more.</summary>
    public const int MinFeePlaces = 6;

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid AccountId { get; private set; }
    /// <summary>The internal ID of the allocated <see cref="ImportedFill"/>.</summary>
    public Guid FillId { get; private set; }
    public AllocationReason Reason { get; private set; }
    public string Note { get; private set; } = "";
    /// <summary>1 for the first allocation; every replacement or removal is the next.</summary>
    public int Revision { get; private set; }
    /// <summary>
    /// The rounding remainder of the fee split, which the line marked <see cref="FillAllocationLine.TookRemainder"/> holds
    /// inside its share; zero when the shares were exact, null when the fee is unknown or the allocation was removed.
    /// </summary>
    public decimal? FeeRemainder { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    /// <summary>The current lines; empty once removed.</summary>
    public List<FillAllocationLine> Lines { get; private set; } = [];
    private FillAllocation() { }

    /// <summary>The first revision of a fill's allocation. Throws when the lines break a rule; nothing is created then.</summary>
    public static (FillAllocation Allocation, FillAllocationRevision Revision) Create(ImportedFill fill, AllocationReason reason, string note,
        IReadOnlyList<AllocationLineRequest> lines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fill);
        var built = Build(fill, reason, note, lines);
        var allocation = new FillAllocation
        {
            Id = Guid.NewGuid(), OwnerId = fill.OwnerId, AccountId = fill.AccountId, FillId = fill.Id, CreatedAtUtc = now,
        };
        return (allocation, allocation.Apply(built, reason, note, now));
    }

    /// <summary>The next revision with new lines. Throws when the lines break a rule; nothing changes then.</summary>
    public FillAllocationRevision Replace(ImportedFill fill, AllocationReason reason, string note, IReadOnlyList<AllocationLineRequest> lines,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (fill.Id != FillId || fill.OwnerId != OwnerId) throw new DomainRuleException("This allocation belongs to another fill.");
        return Apply(Build(fill, reason, note, lines), reason, note, now);
    }

    /// <summary>The next revision without lines: the fill counts through order links again.</summary>
    public FillAllocationRevision Remove(AllocationReason reason, string note, DateTimeOffset now)
    {
        if (Lines.Count == 0) throw new DomainRuleException("This allocation was already removed.");
        if (!Enum.IsDefined(reason)) throw new DomainValidationException(ReasonRule);
        return Apply(new Built([], null), reason, CheckedNote(note), now);
    }

    public const string ReasonRule = "Reason must be shared-close, flip or other.";

    /// <summary>
    /// The lines a revision would have, detached and without changing anything, so the caller can check the final ledger
    /// first. It refuses what a first revision or a replacement refuses.
    /// </summary>
    public static IReadOnlyList<FillAllocationLine> Preview(ImportedFill fill, AllocationReason reason, string note,
        IReadOnlyList<AllocationLineRequest> lines)
    {
        ArgumentNullException.ThrowIfNull(fill);
        return Build(fill, reason, note, lines).Lines;
    }

    /// <summary>
    /// The fill as one line counts it: the line's quantity and fee share, and for a flip the closing or opening half's
    /// effect. The venue's closed P&amp;L belongs to the whole fill and cannot be split, so it is not carried (zero); every
    /// reader treats a Play with an allocated fill as having no venue result.
    /// </summary>
    public static ImportedFill Portion(ImportedFill fill, FillAllocationLine line)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(line);
        return new ImportedFill
        {
            Id = fill.Id, OwnerId = fill.OwnerId, AccountId = fill.AccountId, ContractId = fill.ContractId, VenueContractId = fill.VenueContractId,
            SourceFillId = fill.SourceFillId, Side = fill.Side, Direction = fill.Direction,
            PositionEffect = fill.PositionEffect == ExecutionFacts.Flip
                ? line.Role == AllocationRole.Exit ? ExecutionFacts.Close : ExecutionFacts.Open : fill.PositionEffect,
            Price = fill.Price, Quantity = line.Quantity, Fee = line.Fee ?? 0, FeeToken = fill.FeeToken, FeeBasis = fill.FeeBasis,
            ClosedPnlUsd = 0, PnlBasis = fill.PnlBasis, OccurredAtUtc = fill.OccurredAtUtc, ExternalOrderId = fill.ExternalOrderId,
            TransactionHash = fill.TransactionHash, Leverage = fill.Leverage, Backfilled = fill.Backfilled,
        };
    }

    /// <summary>Whether the fill's fee can be split: it has a known fee basis. Otherwise every line's share is unknown.</summary>
    public static bool FeeKnown(ImportedFill fill) => ExecutionFacts.IsFeeBasis(fill.FeeBasis);

    /// <summary>
    /// The fee split: each share is fee × quantity ÷ the fill's quantity, truncated toward zero to the fee's precision
    /// (its own decimals, at least <see cref="MinFeePlaces"/>). The remainder goes to the line with the largest quantity,
    /// ties to the lowest Play ID as lowercase text, so the shares sum exactly to the fee. Null shares for an unknown fee.
    /// </summary>
    public static (IReadOnlyList<decimal?> Shares, int? RemainderLine, decimal? Remainder) ShareFee(ImportedFill fill,
        IReadOnlyList<(Guid PlayId, decimal Quantity)> lines)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (!FeeKnown(fill)) return (lines.Select(_ => (decimal?)null).ToList(), null, null);
        var total = ExactDecimal.Units(fill.Quantity);
        if (total.Sign <= 0) throw new DomainRuleException("The fill has no positive quantity to allocate.");
        var fee = ExactDecimal.Units(fill.Fee);
        var step = BigInteger.Pow(10, 28 - FeePlaces(fill.Fee));
        // BigInteger division truncates toward zero, so a rebate (a negative fee) is truncated toward zero too.
        var shares = lines.Select(l => fee * ExactDecimal.Units(l.Quantity) / total / step * step).ToList();
        var remainder = fee - shares.Aggregate(BigInteger.Zero, (sum, share) => sum + share);
        var target = lines.Select((l, i) => (l, i)).OrderByDescending(x => ExactDecimal.Units(x.l.Quantity))
            .ThenBy(x => x.l.PlayId.ToString("D"), StringComparer.Ordinal).First().i;
        shares[target] += remainder;
        return (shares.Select(s => (decimal?)Decimal(s)).ToList(), remainder.IsZero ? null : target, Decimal(remainder));
    }

    /// <summary>The fee's precision in decimal places: as reported (trailing zeros aside), and at least <see cref="MinFeePlaces"/>.</summary>
    public static int FeePlaces(decimal fee)
    {
        var text = ExactDecimal.Format(fee);
        var dot = text.IndexOf('.');
        return Math.Clamp(dot < 0 ? 0 : text.Length - dot - 1, MinFeePlaces, 28);
    }

    private sealed record Built(IReadOnlyList<FillAllocationLine> Lines, decimal? Remainder);

    private FillAllocationRevision Apply(Built built, AllocationReason reason, string note, DateTimeOffset now)
    {
        // Lines are kept by Play: a Play still on the lines keeps its row with new values, so no unique key is reused.
        foreach (var gone in Lines.Where(l => built.Lines.All(n => n.PlayId != l.PlayId)).ToList()) Lines.Remove(gone);
        foreach (var line in built.Lines)
        {
            if (Lines.FirstOrDefault(l => l.PlayId == line.PlayId) is { } kept) kept.Set(line);
            else Lines.Add(new FillAllocationLine(this, line));
        }
        Reason = reason;
        Note = note;
        FeeRemainder = built.Remainder;
        Revision = checked(Revision + 1);
        UpdatedAtUtc = now;
        return new FillAllocationRevision(this, now);
    }

    private static string CheckedNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new DomainValidationException("Say why the fill is split. The note is kept with the allocation.");
        note = note.Trim();
        if (note.Length > MaxNoteLength || note.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new DomainValidationException($"Notes are plain text of at most {MaxNoteLength} characters.");
        return note;
    }

    /// <summary>
    /// The rules every revision with lines follows, before anything changes: one line per Play, positive quantities that
    /// sum exactly to the fill, the shared-exit or flip shape the fill's effect allows, each Play's direction against the
    /// fill's side, and a reason that fits the shape.
    /// </summary>
    private static Built Build(ImportedFill fill, AllocationReason reason, string note, IReadOnlyList<AllocationLineRequest> lines)
    {
        if (!Enum.IsDefined(reason)) throw new DomainValidationException(ReasonRule);
        note = CheckedNote(note);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count is 0 or > MaxLines) throw new DomainValidationException($"An allocation has 1 to {MaxLines} lines.");
        if (lines.Any(l => l is null) || lines.Select(l => l.PlayId).Distinct().Count() != lines.Count)
            throw new DomainValidationException("An allocation names each Play once.");
        if (lines.Any(l => !Enum.IsDefined(l.Role) || l.Direction is not ("long" or "short")))
            throw new DomainValidationException("Each line is an exit or an entry of a long or short Play.");
        if (lines.Any(l => l.Quantity <= 0)) throw new DomainValidationException("Each line's quantity is above zero.");
        if (fill.Quantity <= 0) throw new DomainRuleException("The fill has no positive quantity to allocate.");
        // Conservation, exactly: 28-place integers, so no rounding can hide a difference.
        var sum = lines.Aggregate(default(ExactAmount), (total, l) => total + ExactAmount.From(l.Quantity));
        if (sum != ExactAmount.From(fill.Quantity))
            throw new DomainValidationException($"The lines total {sum}, but the fill's quantity is {ExactDecimal.Format(fill.Quantity)}. They must match exactly.");

        var flip = lines.Any(l => l.Role == AllocationRole.Entry);
        if (flip)
        {
            if (lines.Count != 2 || lines.Count(l => l.Role == AllocationRole.Exit) != 1)
                throw new DomainRuleException("A flip is split into exactly one exit and one entry.");
            if (fill.PositionEffect != ExecutionFacts.Flip) throw new DomainRuleException("Only a fill the venue reports as a flip splits into an exit and an entry.");
        }
        else if (fill.PositionEffect == ExecutionFacts.Flip) throw new DomainRuleException("A flip fill splits into one exit and one entry.");
        else if (fill.PositionEffect == ExecutionFacts.Open) throw new DomainRuleException("This fill opened a position, so it is no Play's exit.");
        if (reason == AllocationReason.Flip && !flip) throw new DomainValidationException("The reason flip needs one exit and one entry.");
        if (reason == AllocationReason.SharedClose && flip) throw new DomainValidationException("The reason shared-close needs exit lines only.");

        // A sell closes longs and opens shorts; a buy closes shorts and opens longs.
        var closes = fill.Side switch
        {
            ExecutionFacts.Sell => "long", ExecutionFacts.Buy => "short",
            _ => throw new DomainRuleException("The fill has no normalized buy or sell side."),
        };
        foreach (var line in lines)
        {
            if (line.Role == AllocationRole.Exit && line.Direction != closes)
                throw new DomainRuleException($"A {fill.Side} fill cannot close a {line.Direction} Play.");
            if (line.Role == AllocationRole.Entry && line.Direction == closes)
                throw new DomainRuleException($"A {fill.Side} fill cannot open a {line.Direction} Play.");
            if ((line.Role == AllocationRole.Entry) != !string.IsNullOrWhiteSpace(line.EntryId))
                throw new DomainValidationException("An entry line names one entry of its Play's plan; an exit line names none.");
        }

        var (shares, remainderLine, remainder) = ShareFee(fill, lines.Select(l => (l.PlayId, l.Quantity)).ToList());
        return new(lines.Select((l, i) => new FillAllocationLine(l, shares[i], i == remainderLine)).ToList(), remainder);
    }

    private static decimal Decimal(BigInteger units)
    {
        var text = new ExactAmount(units).ToString();
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            ExactDecimal.Units(value) != units)
            throw new DomainRuleException("A fee share needs more precision than an exact decimal holds.");
        return value;
    }
}

/// <summary>
/// One Play's part of an allocated fill: its role, the plan entry an entry line fills, the exact quantity and fee share
/// (null when the fee is unknown), and whether it took the split's rounding remainder.
/// </summary>
public sealed class FillAllocationLine : IOwned
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid AllocationId { get; private set; }
    public Guid PlayId { get; private set; }
    public AllocationRole Role { get; private set; }
    public string? EntryId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal? Fee { get; private set; }
    public bool TookRemainder { get; private set; }
    private FillAllocationLine() { }

    /// <summary>A line not yet attached to an allocation, as <see cref="FillAllocation"/> builds it before any change.</summary>
    internal FillAllocationLine(AllocationLineRequest line, decimal? fee, bool tookRemainder)
    {
        PlayId = line.PlayId; Role = line.Role; EntryId = line.Role == AllocationRole.Entry ? line.EntryId!.Trim() : null;
        Quantity = line.Quantity; Fee = fee; TookRemainder = tookRemainder;
    }

    internal FillAllocationLine(FillAllocation allocation, FillAllocationLine line)
    {
        Id = Guid.NewGuid(); OwnerId = allocation.OwnerId; AllocationId = allocation.Id;
        PlayId = line.PlayId; Set(line);
    }

    internal void Set(FillAllocationLine line)
    {
        Role = line.Role; EntryId = line.EntryId; Quantity = line.Quantity; Fee = line.Fee; TookRemainder = line.TookRemainder;
    }

    /// <summary>The same Play, role, entry, quantity, fee share and remainder flag: a Closed Play's line must stay so.</summary>
    public bool SameAs(FillAllocationLine other) => PlayId == other.PlayId && Role == other.Role && EntryId == other.EntryId &&
        Quantity == other.Quantity && Fee == other.Fee && TookRemainder == other.TookRemainder;

    /// <summary>A detached copy, so a projection can be compared with the stored lines before anything changes.</summary>
    public FillAllocationLine Copy() => new() { PlayId = PlayId, Role = Role, EntryId = EntryId, Quantity = Quantity, Fee = Fee, TookRemainder = TookRemainder };
}

/// <summary>An append-only snapshot of one allocation revision: its reason, note, fee remainder and lines.</summary>
public sealed class FillAllocationRevision : IOwned
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid AllocationId { get; private set; }
    public int Revision { get; private set; }
    public AllocationReason Reason { get; private set; }
    public string Note { get; private set; } = "";
    public decimal? FeeRemainder { get; private set; }
    /// <summary>The lines as JSON: Play, role, entry, quantity, fee share and remainder flag, as exact strings.</summary>
    public string Lines { get; private set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    private FillAllocationRevision() { }

    internal FillAllocationRevision(FillAllocation allocation, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); OwnerId = allocation.OwnerId; AllocationId = allocation.Id; Revision = allocation.Revision;
        Reason = allocation.Reason; Note = allocation.Note; FeeRemainder = allocation.FeeRemainder; CreatedAtUtc = now;
        Lines = JsonSerializer.Serialize(allocation.Lines.OrderBy(l => l.PlayId.ToString("D"), StringComparer.Ordinal).Select(l => new
        {
            l.PlayId, Role = l.Role == AllocationRole.Entry ? "entry" : "exit", l.EntryId, Quantity = ExactDecimal.Format(l.Quantity),
            Fee = ExactDecimal.Format(l.Fee), l.TookRemainder,
        }), Json);
    }
}
