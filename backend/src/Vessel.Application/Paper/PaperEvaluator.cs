using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Numerics;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;

namespace Vessel.Application.Paper;

public sealed record PaperCandle(DateTimeOffset OpenAtUtc, DateTimeOffset CloseAtUtc,
    decimal Open, decimal High, decimal Low, decimal Close);

public sealed record PaperEvaluation(IReadOnlyList<PaperExecution> Executions, DateTimeOffset? CheckpointUtc,
    decimal? PreviousClose, string? Issue, bool Ambiguous);

/// <summary>Forward-only, closed-minute simulation. The caller owns admission, persistence and window bounds.</summary>
public static class PaperEvaluator
{
    /// <summary>Entry shares allocate, rather than normalize, the editor's total position quantity.</summary>
    public static IReadOnlyDictionary<string, (decimal Quantity, decimal Margin)> Allocations(PlayPlanDocument plan)
    {
        try
        {
            if (plan.Direction is not ("long" or "short") || plan.SizingMode is not ("margin" or "quantity") ||
                !int.TryParse(plan.Leverage, NumberStyles.None, CultureInfo.InvariantCulture, out var leverage) ||
                leverage is < 1 or > 100 || plan.Entries is not { Count: > 0 and <= PlayDocuments.MaxEntries })
                throw Incomplete();
            var size = Positive(plan.Size);
            var shares = 0m;
            var weightedPrice = 0m;
            var entries = new List<(string Id, decimal Share, decimal Price)>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in plan.Entries)
            {
                if (string.IsNullOrEmpty(entry.Id) || !ids.Add(entry.Id)) throw Incomplete();
                var share = Share(entry.Share);
                var price = share > 0 ? Positive(entry.Price) : 0;
                shares += share;
                if (plan.SizingMode == "margin") weightedPrice += price * share;
                entries.Add((entry.Id, share, price));
            }
            if (shares is <= 0 or > 100) throw Incomplete();
            var quantity = plan.SizingMode == "quantity" ? size : size * leverage / (weightedPrice / shares);
            var allocations = new Dictionary<string, (decimal Quantity, decimal Margin)>(StringComparer.Ordinal);
            var totalMargin = 0m;
            foreach (var entry in entries)
            {
                var entryQuantity = quantity * entry.Share / 100;
                var margin = entryQuantity * entry.Price / leverage;
                if (entry.Share > 0 && (entryQuantity <= 0 || margin <= 0)) throw Incomplete();
                totalMargin += margin;
                allocations.Add(entry.Id, (entryQuantity, margin));
            }
            return allocations;
        }
        catch (ArithmeticException)
        {
            throw Incomplete("Paper position allocation exceeds decimal precision.");
        }
    }

    /// <summary>Returns new facts only. Blocked admission can retain protective exits without advancing its candle frontier.</summary>
    public static PaperEvaluation Evaluate(Guid ownerId, Guid accountId, Guid playId,
        IReadOnlyList<PaperPlanActivation> activations, IReadOnlyList<PaperExecution> retained,
        DateTimeOffset? checkpointUtc, decimal? previousClose, IReadOnlyList<PaperCandle> candles,
        DateTimeOffset throughUtc, decimal? entryMarginBudget = null, string? entryBlockReason = null,
        IReadOnlyDictionary<string, decimal>? reservedEntryMargins = null)
    {
        var executions = new List<PaperExecution>();
        PaperEvaluation Result(string? issue = null, bool ambiguous = false) =>
            new(executions.ToArray(), checkpointUtc, previousClose, issue, ambiguous);
        if (activations.Count == 0) return Result();
        try
        {
            if (ownerId == Guid.Empty || accountId == Guid.Empty || playId == Guid.Empty ||
                activations.Any(a => a.OwnerId != ownerId || a.AccountId != accountId || a.PlayId != playId) ||
                retained.Any(e => e.OwnerId != ownerId || e.AccountId != accountId || e.PlayId != playId))
                return Result("Paper facts do not belong to this owner, account and Play.");
            if (reservedEntryMargins?.Any(a => a.Value < 0) == true)
                return Result("Paper entry reservations cannot be negative.");
            var revisions = activations.OrderBy(a => a.EffectiveAtUtc).ThenBy(a => a.PlanVersion).ToArray();
            var open = checkpointUtc ?? revisions[0].EffectiveAtUtc;
            // Activations are already prospective full-minute boundaries, not raw plan-save timestamps.
            if (!Minute(open) || revisions.Any(a => !Minute(a.EffectiveAtUtc)))
                return Result("Paper activation and checkpoint times must be full-minute boundaries.");
            var plans = new Dictionary<int, PlayPlanDocument>();
            PlayPlanDocument Plan(int index)
            {
                if (!plans.TryGetValue(index, out var plan))
                {
                    plan = PlayDocuments.Read(revisions[index].PlanJson);
                    if (plan is null) throw Incomplete();
                    plans.Add(index, plan);
                }
                return plan;
            }
            int Revision(DateTimeOffset at) => Array.FindLastIndex(revisions, a => a.EffectiveAtUtc <= at);
            var positions = new Dictionary<string, Position>(StringComparer.Ordinal);
            foreach (var entry in retained.Where(e => e.Role == "entry"))
            {
                var revision = Array.FindLastIndex(revisions, a => a.PlanVersion == entry.PlanVersion &&
                    a.EffectiveAtUtc <= entry.CandleOpenAtUtc);
                if (revision < 0 || entry.Quantity <= 0 || entry.Price <= 0 || entry.MarginUsd <= 0 ||
                    !positions.TryAdd(entry.EntryId, new(entry, Plan(revision).Direction)))
                    return Result("Retained paper entry evidence is inconsistent.");
            }
            var used = new HashSet<(string Entry, string Role, string? Level)>();
            foreach (var execution in retained)
            {
                if (!used.Add((execution.EntryId, execution.Role, execution.LevelId)))
                    return Result("Retained paper levels have duplicate executions.");
                if (execution.Role == "entry") continue;
                if (!positions.TryGetValue(execution.EntryId, out var position) ||
                    execution.Quantity <= 0 || execution.MarginUsd > 0)
                    return Result("Retained paper exit evidence is inconsistent.");
                position.Quantity -= execution.Quantity;
                position.Margin += execution.MarginUsd;
                if (position.Quantity < 0 || position.Margin < 0)
                    return Result("Retained paper exits exceed their entry.");
            }
            if (positions.Count > 0 && positions.Values.All(p => p.Quantity == 0)) return Result();
            var window = candles.Where(c => c.OpenAtUtc >= open).GroupBy(c => c.OpenAtUtc)
                .ToDictionary(g => g.Key, g => g.ToArray());
            var priorRevision = checkpointUtc is null ? -1 : Revision(open.AddMinutes(-1));
            while (open.AddMinutes(1) <= throughUtc)
            {
                if (!window.TryGetValue(open, out var rows))
                    return Result($"Missing paper candle at {open:O}; evaluation has not skipped the gap.");
                if (rows.Length != 1) return Result($"Duplicate paper candles at {open:O}.");
                var candle = rows[0];
                if (candle.CloseAtUtc != open.AddMinutes(1) || candle.Open <= 0 || candle.Close <= 0 ||
                    candle.Low <= 0 || candle.High < candle.Low || candle.Open < candle.Low ||
                    candle.Open > candle.High || candle.Close < candle.Low || candle.Close > candle.High)
                    return Result($"Invalid one-minute paper candle at {open:O}.");
                var revision = Revision(open);
                if (revision < 0) return Result("No paper plan is effective at the evaluation frontier.");
                var plan = Plan(revision);
                var allocations = Allocations(plan);
                var previous = priorRevision == revision ? previousClose : null;
                bool Touched(decimal price) => price >= candle.Low && price <= candle.High ||
                    previous is { } close && price >= Math.Min(close, candle.Open) && price <= Math.Max(close, candle.Open);
                var pendingExits = new List<PaperExecution>();
                var pendingEntries = new List<PaperExecution>();
                var ambiguousEntry = false;
                foreach (var entry in plan.Entries)
                {
                    positions.TryGetValue(entry.Id, out var position);
                    if (position is { Quantity: 0 } || position is null && allocations[entry.Id].Quantity == 0) continue;
                    // Revised size, entry price or leverage must not rewrite already committed entry evidence.
                    var levelPlan = plan with
                    {
                        Direction = position?.Direction ?? plan.Direction,
                        Entries = [position is null ? entry : entry with { Price = Text(position.Entry.Price) }]
                    };
                    var levels = ExecutionMatcher.Levels(levelPlan).ToArray();
                    var enters = position is null && levels[0].Price is { } entryPrice && Touched(entryPrice);
                    var exits = levels.Where(l => l.Role != OrderLinkRole.Entry && l.Price is > 0 &&
                            !used.Contains((entry.Id, Role(l.Role), l.LevelId)))
                        .Where(l => Share(Exit(entry, l).Share) > 0 && Touched(l.Price!.Value)).ToArray();
                    ambiguousEntry |= enters && exits.Length > 0;
                    if (position is not null &&
                        exits.Any(l => l.Role == OrderLinkRole.Stop) && exits.Any(l => l.Role == OrderLinkRole.Target))
                        return Result($"Ambiguous paper touches at {open:O}; no execution from this candle was applied.", true);
                    if (enters)
                    {
                        var allocation = allocations[entry.Id];
                        pendingEntries.Add(Fact(entry.Id, null, "entry", levels[0].Price!.Value,
                            allocation.Quantity, 0, allocation.Margin));
                    }
                    if (position is null) continue;
                    var remaining = position.Quantity;
                    var margin = position.Margin;
                    foreach (var level in exits)
                    {
                        if (remaining == 0) break;
                        var quantity = Math.Min(remaining, position.Entry.Quantity * Share(Exit(entry, level).Share) / 100);
                        if (quantity == 0) continue;
                        var released = quantity == remaining ? margin :
                            Math.Min(margin, position.Entry.MarginUsd * (quantity / position.Entry.Quantity));
                        var pnl = (level.Price!.Value - position.Entry.Price) * quantity *
                            (position.Direction == "long" ? 1 : -1);
                        pendingExits.Add(Fact(entry.Id, level.LevelId, Role(level.Role), level.Price.Value, quantity, pnl, -released));
                        remaining -= quantity;
                        margin -= released;
                    }
                }
                var budgetAfterExits = entryMarginBudget;
                if (budgetAfterExits.HasValue)
                    foreach (var exit in pendingExits) budgetAfterExits += exit.PnlUsd - exit.MarginUsd;
                string? entryIssue = null;
                if (pendingEntries.Count > 0)
                {
                    entryIssue = string.IsNullOrWhiteSpace(entryBlockReason) ? null : entryBlockReason;
                    // Untouched tranches still need their reserved capital; a cheap touch cannot spend their backing.
                    var unenteredMargin = reservedEntryMargins is null
                        ? allocations.Where(a => !positions.ContainsKey(a.Key)).Sum(a => a.Value.Margin)
                        : reservedEntryMargins.Where(a => !positions.ContainsKey(a.Key)).Sum(a => a.Value);
                    if (entryIssue is null && budgetAfterExits is { } budget &&
                        (unenteredMargin > budget || pendingEntries.Sum(e => e.MarginUsd) > budget))
                        entryIssue = "Insufficient paper margin for the remaining planned entry allocations.";
                    if (entryIssue is null && ambiguousEntry)
                        return Result($"Ambiguous paper touches at {open:O}; no execution from this candle was applied.", true);
                }
                var pending = pendingExits.Concat(entryIssue is null ? pendingEntries : []).ToArray();
                foreach (var fact in pending)
                {
                    if (fact.Role == "entry") positions.Add(fact.EntryId, new(fact, plan.Direction));
                    else
                    {
                        positions[fact.EntryId].Quantity -= fact.Quantity;
                        positions[fact.EntryId].Margin += fact.MarginUsd;
                    }
                    used.Add((fact.EntryId, fact.Role, fact.LevelId));
                }
                executions.AddRange(pending);
                // Protective exits survive a blocked admission; replaying this minute deduplicates them from retained facts.
                if (entryIssue is not null) return Result(entryIssue);
                entryMarginBudget = budgetAfterExits;
                if (entryMarginBudget.HasValue)
                    foreach (var entry in pendingEntries) entryMarginBudget -= entry.MarginUsd;
                checkpointUtc = candle.CloseAtUtc;
                previousClose = candle.Close;
                priorRevision = revision;
                open = candle.CloseAtUtc;
                if (positions.Count > 0 && positions.Values.All(p => p.Quantity == 0)) return Result();

                PaperExecution Fact(string entryId, string? levelId, string role, decimal price,
                    decimal quantity, decimal pnl, decimal margin)
                {
                    var key = string.Create(CultureInfo.InvariantCulture,
                        $"{ownerId:D}|{accountId:D}|{playId:D}|{entryId}|{levelId}|{role}|{revisions[revision].PlanVersion}|{candle.OpenAtUtc.UtcTicks}");
                    return new()
                    {
                        Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)),
                        OwnerId = ownerId, AccountId = accountId, PlayId = playId,
                        EntryId = entryId, LevelId = levelId, Role = role, Price = price, Quantity = quantity,
                        PnlUsd = pnl, MarginUsd = margin, PlanVersion = revisions[revision].PlanVersion,
                        OccurredAtUtc = candle.CloseAtUtc, CandleOpenAtUtc = candle.OpenAtUtc,
                        CandleCloseAtUtc = candle.CloseAtUtc
                    };
                }
            }
            return Result();
        }
        catch (ProblemException ex) { return Result(ex.Message); }
        catch (JsonException) { return Result("The effective paper plan cannot be read."); }
        catch (ArithmeticException) { return Result("Paper evaluation exceeds decimal precision."); }
    }

    private sealed class Position(PaperExecution entry, string direction)
    {
        public PaperExecution Entry { get; } = entry;
        public string Direction { get; } = direction;
        public decimal Quantity { get; set; } = entry.Quantity;
        public decimal Margin { get; set; } = entry.MarginUsd;
    }

    private static PlanExit Exit(PlanEntry entry, PlanLevelTarget level) =>
        (level.Role == OrderLinkRole.Stop ? entry.Stops : entry.Targets).Single(e => e.Id == level.LevelId);

    private static string Role(OrderLinkRole role) => role == OrderLinkRole.Stop ? "stop" : "target";
    private static bool Minute(DateTimeOffset at) => at.UtcTicks % TimeSpan.TicksPerMinute == 0;
    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal Positive(string text) =>
        ExactDecimal.TryParseVenue(text, out var value) && value > 0 ? value : throw Incomplete();
    private static decimal Share(string text) =>
        ExactDecimal.TryParseVenue(text, out var value) && value is >= 0 and <= 100 ? value : throw Incomplete();
    private static ProblemException Incomplete(string detail = "Paper execution needs complete positive sizing, entry prices and valid allocation shares.") =>
        new(409, detail);
}
