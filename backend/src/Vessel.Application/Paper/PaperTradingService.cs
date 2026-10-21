using System.Globalization;
using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;

namespace Vessel.Application.Paper;

public sealed class PaperTradingService(IPaperStore store, IUnitOfWork work, IJournalOwnerDirectory owners,
    IVenueRegistry venues, TimeProvider time) : IPaperTradingService
{
    // Product recovery tolerance, not a claim about venue publication/finality.
    public static readonly TimeSpan SourceFailureRecoveryGrace = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CandlePublicationGrace = TimeSpan.FromMinutes(5);

    public async Task<PaperSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var s = await store.SettingsAsync(ct);
        return s is null ? new(false, 60) : new(s.Enabled, s.CheckIntervalMinutes);
    }

    public Task<PaperSettingsDto> UpdateSettingsAsync(PaperSettingsDto request, CancellationToken ct)
    {
        if (request.CheckIntervalMinutes is < 1 or > 1440)
            throw new ProblemException(400, "Paper check interval must be a whole number from 1 to 1440 minutes.");
        return work.ExecuteAsync(() => store.WithSettingsLockAsync(async () =>
        {
            var settings = await store.SettingsAsync(ct);
            if (settings is null) store.Add(settings = new() { OwnerId = store.OwnerId });
            if (settings.Enabled && !request.Enabled)
            {
                foreach (var state in await store.ActiveSimulationsAsync(ct))
                    state.Suspend("Paper trading was disabled; the skipped window is not simulated and this outcome remains unscored.", time.GetUtcNow());
            }
            else if (!settings.Enabled && request.Enabled)
            {
                foreach (var state in await store.ActiveSimulationsAsync(ct))
                    if ((await store.AccountAsync(state.AccountId, ct))?.IsEnabled == true &&
                        (await store.PlayAsync(state.PlayId, ct))?.Status is PlayStatus.Planned or PlayStatus.Open)
                        state.Resume(NextMinute(time.GetUtcNow()), request.CheckIntervalMinutes);
            }
            else if (settings.Enabled && request.Enabled && settings.CheckIntervalMinutes != request.CheckIntervalMinutes)
            {
                foreach (var state in await store.ActiveSimulationsAsync(ct))
                    if (state.State is "waiting" or "ready")
                        state.NextCheckAtUtc = (state.CheckedAtUtc ?? state.CheckpointUtc ?? time.GetUtcNow()).AddMinutes(request.CheckIntervalMinutes);
            }
            settings.Enabled = request.Enabled;
            settings.CheckIntervalMinutes = request.CheckIntervalMinutes;
            return new PaperSettingsDto(settings.Enabled, settings.CheckIntervalMinutes);
        }, ct), ct);
    }

    public Task ArmAsync(Play play, CancellationToken ct) => ActivateAsync(play, true, ct);
    public Task ActivateRevisionAsync(Play play, CancellationToken ct) => ActivateAsync(play, false, ct);

    private async Task ActivateAsync(Play play, bool arming, CancellationToken ct)
    {
        store.RequireActive();
        if (play.TradingMode != TradingMode.Paper) return;
        if (!arming && play.Status is PlayStatus.Draft or PlayStatus.Paused) return;
        if (play.Status is not (PlayStatus.Planned or PlayStatus.Open))
            throw new ProblemException(409, "Only a planned or open paper Play can be armed.");
        // The caller already holds the account row. Settings writes hold only their owner advisory lock,
        // so this order cannot invert the account lock ordering used by real execution work.
        await store.WithSettingsLockAsync(async () =>
        {
            var settings = await store.SettingsAsync(ct);
            var account = await store.AccountAsync(play.AccountId, ct);
            if (settings is not { Enabled: true })
                throw new ProblemException(409, "Enable paper trading before arming a Play.");
            if (await owners.StatusAsync(store.OwnerId, ct) != JournalOwnerStatus.Active)
                throw new ProblemException(403, "This journal owner cannot run simulations.");
            if (account is not { IsEnabled: true, TradingMode: TradingMode.Paper } ||
                venues.Descriptor(account.VenueId)?.Capabilities.PaperExecution != true ||
                play.InstrumentSource != InstrumentSource.Venue || play.ContractId is null)
                throw new ProblemException(409, "Paper execution needs an enabled supported paper account and venue instrument.");
            var simulation = await store.SimulationAsync(play.Id, ct);
            if (simulation?.State is "gap" or "ambiguous")
                throw new ProblemException(409, "This simulation has unresolved coverage or ambiguous touches; its capital remains held.");
            var plan = PlayDocuments.Read(play.Plan);
            var executions = await store.ExecutionsAsync(play.Id, ct);
            _ = PaperEvaluator.Allocations(plan, executions);
            var entered = executions.Where(x => x.Role == "entry").Select(x => x.EntryId).ToHashSet(StringComparer.Ordinal);
            if (simulation is not null) entered.UnionWith(simulation.DeclinedEntries().Select(x => x.EntryId));
            var capital = await store.CapitalAsync(play.AccountId, play.Id, true, ct);
            var reservation = await store.ReservationAsync(play.Id, ct);
            var activations = await store.ActivationsAsync(play.Id, ct);
            // Different tranches can execute on either side of a revision boundary. Cover each entry's largest
            // still-effective allocation, not merely the larger total of two mutually incomplete plans.
            var priorPlans = !arming && simulation is not null
                ? PendingPlans(activations, simulation.CheckpointUtc ?? time.GetUtcNow()).ToArray() : [];
            var plans = priorPlans.Append(plan).ToArray();
            var reserve = PendingReservation(plans, entered);
            var prior = PendingAllocations(priorPlans, entered);
            var nonAdmitting = !arming && simulation is not null && reservation is not null &&
                executions.Sum(e => e.Role == "entry" ? e.Quantity : -e.Quantity) > 0 &&
                reserve <= reservation.AmountUsd && PendingAllocations(plans, entered).All(next =>
                    prior.TryGetValue(next.Key, out var old) && next.Value.Quantity <= old.Quantity && next.Value.Margin <= old.Margin);
            if (capital.Unresolved && !nonAdmitting)
                throw new ProblemException(409, "Unresolved paper execution holds this account's capital; no new entries can be admitted.");
            var available = capital.CashUsd - capital.OpenMarginUsd - capital.ReservedUsd;
            if (!nonAdmitting && ExactAmount.From(reserve) > (available.Units.Sign < 0 ? default : available))
                throw new ProblemException(409, "Insufficient paper available margin for the unentered plan allocations.");
            var effective = NextMinute(time.GetUtcNow());
            // A pause/resume before the same next-minute open needs no duplicate immutable snapshot.
            if (!activations.Any(x => x.EffectiveAtUtc == effective && x.PlanVersion == play.PlanRevision))
                store.Add(new PaperPlanActivation { Id = Guid.NewGuid(), OwnerId = store.OwnerId, AccountId = play.AccountId,
                    PlayId = play.Id, PlanVersion = play.PlanRevision, PlanJson = play.Plan, EffectiveAtUtc = effective });
            if (simulation is null)
                store.Add(simulation = new() { OwnerId = store.OwnerId, AccountId = play.AccountId, PlayId = play.Id });
            if (arming)
            {
                // A paused unfilled simulation resumes prospectively, never through its suspended candles.
                simulation.Resume(effective, settings.CheckIntervalMinutes);
                simulation.CheckpointUtc = effective;
                simulation.PreviousClose = null;
                simulation.PreviousCloseAtUtc = null;
                simulation.IssuesJson = "[]";
                simulation.SourceFailureSinceUtc = null;
            }
            simulation.State = "waiting";
            var next = effective.AddMinutes(settings.CheckIntervalMinutes);
            simulation.NextCheckAtUtc = !arming && simulation.NextCheckAtUtc is { } due && due < next ? due : next;
            if (reservation is null)
                store.Add(reservation = new() { OwnerId = store.OwnerId, AccountId = play.AccountId, PlayId = play.Id });
            reservation.AmountUsd = reserve;
            return true;
        }, ct);
    }

    public async Task SuspendAsync(Play play, CancellationToken ct)
    {
        store.RequireActive();
        if (play.TradingMode != TradingMode.Paper) return;
        var proof = await GetExecutionProofAsync(play.Id, ct);
        if (proof.OpenQuantity > 0)
            throw new ProblemException(409, "Open simulated quantity cannot be cancelled or paused to erase its result.");
        if (await store.SimulationAsync(play.Id, ct) is { } state)
        {
            if (state.State is "gap" or "ambiguous") return; // Cancelling cannot make unresolved capital spendable.
            var now = time.GetUtcNow();
            var frontier = state.CheckpointUtc ?? (await store.ActivationsAsync(play.Id, ct)).FirstOrDefault()?.EffectiveAtUtc;
            // Include unsettled/partial minutes: prospective rearming skips those too, not only settled candles.
            if (frontier < now)
                state.Suspend($"The owner {play.Status.ToString().ToLowerInvariant()} this paper Play; " +
                    $"the unprocessed pre-action interval [{frontier:O}, {now:O}) was skipped and is not simulated. " +
                    "This outcome remains unscored.", now);
            state.State = "disabled"; state.NextCheckAtUtc = null;
            // No open quantity remains, and terminal evidence was excluded above. A retry issue is not
            // unresolved execution evidence once the owner has stopped this work; keep suspension history.
            state.IssuesJson = "[]";
            state.SourceFailureSinceUtc = null;
        }
        if (await store.ReservationAsync(play.Id, ct) is { } reserve) reserve.AmountUsd = 0;
    }

    public async Task SuspendAccountAsync(Guid accountId, CancellationToken ct)
    {
        store.RequireActive();
        foreach (var state in await store.AccountSimulationsAsync(accountId, ct))
        {
            if (state.NextCheckAtUtc is null || state.State is "gap" or "ambiguous") continue;
            state.Suspend("The paper account was disabled; the skipped window is not simulated and this outcome remains unscored.", time.GetUtcNow());
        }
    }

    public async Task<PaperExecutionProof> GetExecutionProofAsync(Guid playId, CancellationToken ct)
    {
        var executions = await store.ExecutionsAsync(playId, ct);
        var first = executions.Where(x => x.Role == "entry").OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).FirstOrDefault();
        var entered = executions.Where(x => x.Role == "entry").Sum(x => x.Quantity);
        var exited = executions.Where(x => x.Role != "entry").Sum(x => x.Quantity);
        var state = await store.SimulationAsync(playId, ct);
        return new(first?.OccurredAtUtc, first?.PlanVersion, entered, exited, entered - exited,
            executions.Aggregate(default(ExactAmount), (sum, x) => sum + ExactAmount.From(x.PnlUsd)),
            state?.State is "gap" or "ambiguous" || state?.Suspensions().Count > 0,
            executions.Where(x => x.Role == "entry").Select(x => x.EntryId).ToHashSet(StringComparer.Ordinal));
    }

    public Task RecordReadFailureAsync(PaperDuePlay candidate, CancellationToken ct) =>
        work.ExecuteAsync(() => store.WithAccountLockAsync(candidate.AccountId, async () =>
        {
            var play = await store.PlayAsync(candidate.PlayId, ct);
            var state = await store.SimulationAsync(candidate.PlayId, ct);
            if (play?.Version != candidate.Version || state is null || state.CheckpointUtc != candidate.CheckpointUtc ||
                state.State is "gap" or "ambiguous" or "disabled") return false;
            // Transient transport failure retains the frontier and capital; a later complete read can recover.
            state.CheckedAtUtc = time.GetUtcNow();
            state.NextCheckAtUtc = time.GetUtcNow().AddMinutes(1);
            state.IssuesJson = JsonSerializer.Serialize(new[] { "The public candle read failed. The unevaluated window and reserved capital are retained." });
            return true;
        }, ct), ct);

    public async Task<PaperPlayState> StateAsync(Guid id, CancellationToken ct)
    {
        var play = await store.PlayAsync(id, ct);
        if (play?.TradingMode != TradingMode.Paper) throw new ProblemException(404, "Paper Play not found.");
        var state = await store.SimulationAsync(id, ct);
        var executions = await store.ExecutionsAsync(id, ct);
        var entered = executions.Where(x => x.Role == "entry").Sum(x => x.Quantity);
        var exited = executions.Where(x => x.Role != "entry").Sum(x => x.Quantity);
        var enabled = (await store.SettingsAsync(ct))?.Enabled == true && (await store.AccountAsync(play.AccountId, ct))?.IsEnabled == true;
        var declined = state?.DeclinedEntries() ?? [];
        var suspensions = state?.Suspensions() ?? [];
        var status = state?.State == "suspended" ? "suspended" : !enabled ? "disabled" :
            declined.Count > 0 && state?.State is "ready" or "waiting" &&
                play.Status is PlayStatus.Planned or PlayStatus.Open ? "blocked" : state?.State ?? "not-armed";
        return new(id, status,
            state?.CheckedAtUtc, enabled ? state?.NextCheckAtUtc : null, Text(entered), Text(exited), Text(entered - exited),
            executions.Aggregate(default(ExactAmount), (sum, x) => sum + ExactAmount.From(x.PnlUsd)).ToString(),
            state is null ? [] : JsonSerializer.Deserialize<string[]>(state.IssuesJson) ?? [],
            executions.OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).Select(x => new PaperExecutionDto(x.Id, x.EntryId,
                x.LevelId, x.Role, Text(x.Price), Text(x.Quantity), Text(x.PnlUsd), x.OccurredAtUtc, x.CandleOpenAtUtc, x.CandleCloseAtUtc)).ToArray(),
            declined, suspensions, suspensions.Count > 0);
    }

    public const string ExportNotice = "Simulated executions: full eligible allocations at touched planned prices with zero fees and " +
        "slippage, by the stated model version. Never actual fills; a touch is never an actual fill.";

    /// <summary>The simulation record of one paper Play for its bundle (Phase 2, item 5c), read through the paper store.</summary>
    public async Task<PaperPlayExport> ExportAsync(Guid id, CancellationToken ct)
    {
        var state = await StateAsync(id, ct);
        var executions = await store.ExecutionsAsync(id, ct);
        var activations = await store.ActivationsAsync(id, ct);
        return new(state, executions.OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).Select(x => new PaperExecutionRecordDto(x.Id,
                x.ModelVersion, x.PlanVersion, x.EntryId, x.LevelId, x.Role, Text(x.Price), Text(x.Quantity), Text(x.PnlUsd), Text(x.MarginUsd),
                x.OccurredAtUtc, x.CandleOpenAtUtc, x.CandleCloseAtUtc)).ToList(),
            activations.OrderBy(a => a.PlanVersion).ThenBy(a => a.EffectiveAtUtc).Select(a =>
            {
                using var plan = JsonDocument.Parse(a.PlanJson);
                return new PaperActivationDto(a.PlanVersion, a.EffectiveAtUtc, plan.RootElement.Clone());
            }).ToList(), ExportNotice);
    }

    /// <summary>Discovery uses bounded keyset reads but includes every active sibling, even when not yet due.</summary>
    public async Task<PaperAccountCandidate> PrepareAccountAsync(Guid accountId, CancellationToken ct) =>
        new(store.OwnerId, accountId, await AccountCandidatesAsync(accountId, ct));

    private async Task<List<PaperDuePlay>> AccountCandidatesAsync(Guid accountId, CancellationToken ct)
    {
        var result = new List<PaperDuePlay>();
        Guid? after = null;
        while (true)
        {
            var page = await store.AccountCandidatesAsync(accountId, after, 200, ct);
            result.AddRange(page);
            if (page.Count < 200) return result;
            after = page[^1].PlayId;
        }
    }

    /// <summary>
    /// Compatibility entry point for a shared single-instrument account window. It never commits one sibling's
    /// whole window in isolation; multi-instrument callers must supply the complete account's public windows.
    /// </summary>
    public async Task<bool> EvaluateAsync(PaperDuePlay candidate, IReadOnlyList<PaperCandle> candles,
        DateTimeOffset throughUtc, CancellationToken ct)
    {
        var account = await PrepareAccountAsync(candidate.AccountId, ct);
        if (!account.Plays.Contains(candidate) || account.Plays.Any(p => p.FromUtc < throughUtc && p.ContractId != candidate.ContractId)) return false;
        return await EvaluateAccountAsync(account, new Dictionary<string, IReadOnlyList<PaperCandle>>(StringComparer.Ordinal)
            { [candidate.ContractId] = candles }, throughUtc, ct);
    }

    public Task RecordAccountReadFailureAsync(PaperAccountCandidate candidate, CancellationToken ct,
        IReadOnlySet<string>? deterministicContracts = null) =>
        RecordAccountFailureAsync(candidate,
            "The complete account candle read failed. No sibling execution frontier was advanced; reserved capital is retained.", ct,
            deterministicContracts);

    public Task RecordAccountEvaluationFailureAsync(PaperAccountCandidate candidate, CancellationToken ct) =>
        RecordAccountFailureAsync(candidate,
            "Simulation evaluation failed; no account frontier advanced. Reserved capital is retained; evaluation will retry.", ct);

    private Task<bool> RecordAccountFailureAsync(PaperAccountCandidate candidate, string issue, CancellationToken ct,
        IReadOnlySet<string>? deterministicContracts = null) =>
        work.ExecuteAsync(() => store.WithAccountLockAsync(candidate.AccountId, () => store.WithSettingsLockAsync(async () =>
        {
            if (candidate.OwnerId != store.OwnerId || await owners.StatusAsync(store.OwnerId, ct) != JournalOwnerStatus.Active ||
                (await store.SettingsAsync(ct)) is not { Enabled: true } ||
                (await store.AccountAsync(candidate.AccountId, ct)) is not { IsEnabled: true, TradingMode: TradingMode.Paper } ||
                !SameAccountSet(candidate.Plays, await AccountCandidatesAsync(candidate.AccountId, ct))) return false;
            await store.DeferAccountAsync(candidate.AccountId, JsonSerializer.Serialize(new[] { issue }), time.GetUtcNow(), ct);
            foreach (var item in candidate.Plays.Where(p => deterministicContracts?.Contains(p.ContractId) == true))
            {
                var state = (await store.SimulationAsync(item.PlayId, ct))!;
                state.SourceFailureSinceUtc ??= time.GetUtcNow();
                if (time.GetUtcNow() - state.SourceFailureSinceUtc >= SourceFailureRecoveryGrace)
                {
                    state.State = "gap";
                    state.NextCheckAtUtc = null;
                    state.IssuesJson = JsonSerializer.Serialize(new[]
                    {
                        "The public candle source remained invalid or rejected for five minutes. Coverage is unresolved; reserved capital is retained."
                    });
                }
            }
            return true;
        }, ct), ct), ct);

    /// <summary>
    /// One account transaction, one minute at a time across all instruments. Every same-minute decision sees the
    /// same pre-minute cash/margin/reservations; neither Play IDs nor public-read group order can spend future P&L.
    /// </summary>
    public Task<bool> EvaluateAccountAsync(PaperAccountCandidate candidate,
        IReadOnlyDictionary<string, IReadOnlyList<PaperCandle>> candles, DateTimeOffset throughUtc, CancellationToken ct) =>
        work.ExecuteAsync(() => store.WithAccountLockAsync(candidate.AccountId, () => store.WithSettingsLockAsync(async () =>
        {
            if (candidate.OwnerId != store.OwnerId || await owners.StatusAsync(store.OwnerId, ct) != JournalOwnerStatus.Active ||
                (await store.SettingsAsync(ct)) is not { Enabled: true } settings ||
                (await store.AccountAsync(candidate.AccountId, ct)) is not { IsEnabled: true, TradingMode: TradingMode.Paper }) return false;
            var current = await AccountCandidatesAsync(candidate.AccountId, ct);
            // New arming, pause/resume, changed due times and any sibling revision invalidate the whole fetched set.
            if (current.Count == 0 || !SameAccountSet(candidate.Plays, current) ||
                !current.Any(p => p.NextCheckAtUtc <= time.GetUtcNow())) return false;
            var from = current.Min(p => p.FromUtc);
            if (throughUtc <= from || throughUtc > FloorMinute(time.GetUtcNow()) || throughUtc > from.AddMinutes(4000) ||
                current.Any(p => p.FromUtc < throughUtc && !candles.ContainsKey(p.ContractId))) return false;
            var runs = new List<PaperRun>();
            foreach (var item in current)
            {
                var play = await store.PlayAsync(item.PlayId, ct);
                var state = await store.SimulationAsync(item.PlayId, ct);
                if (play is null || state is null || play.Version != item.Version || state.CheckpointUtc != item.CheckpointUtc) return false;
                runs.Add(new(item, play, state, await store.ActivationsAsync(item.PlayId, ct),
                    await store.ExecutionsAsync(item.PlayId, ct), await store.ReservationAsync(item.PlayId, ct)));
            }
            // Earlier versions could leave siblings at unequal committed frontiers. Current cash would then include
            // a later execution (including a CLOSED Play's loss). Refuse instead of retroactively guessing its order.
            var incompatible = await store.HasExecutionsAfterAsync(candidate.AccountId, from, ct);
            if (incompatible || runs.Any(r => r.Reservation is null))
            {
                foreach (var run in runs)
                {
                    run.State.State = "gap"; run.State.NextCheckAtUtc = null; run.State.CheckedAtUtc = time.GetUtcNow();
                    run.State.IssuesJson = JsonSerializer.Serialize(new[] { incompatible
                        ? "Incompatible paper account checkpoints contain executions after the earliest pending candle. Historical capital was not guessed; the account needs explicit reconciliation."
                        : "The active paper account timeline is missing an admission reservation. Capital was not guessed; the account needs explicit reconciliation." });
                }
                return true;
            }
            var capital = await store.TimelineCapitalAsync(candidate.AccountId, current.Select(x => x.PlayId).ToArray(), ct);
            var externalReserved = capital.ReservedUsd -
                runs.Aggregate(default(ExactAmount), (sum, run) => sum + ExactAmount.From(run.Reservation?.AmountUsd ?? 0));
            var cash = capital.CashUsd;
            var margin = capital.OpenMarginUsd;
            var unresolved = capital.Unresolved;
            var windows = candles.ToDictionary(x => x.Key, x => x.Value.GroupBy(c => c.OpenAtUtc)
                .ToDictionary(g => g.Key, g => g.ToArray()), StringComparer.Ordinal);
            for (var minute = from; minute.AddMinutes(1) <= throughUtc; minute = minute.AddMinutes(1))
            {
                var active = runs.Where(r => r.CanEvaluate && (r.State.CheckpointUtc ?? r.Candidate.FromUtc) == minute).ToArray();
                if (active.Length == 0) continue;
                var reserved = externalReserved + runs.Aggregate(default(ExactAmount),
                    (sum, run) => sum + ExactAmount.From(run.Reservation?.AmountUsd ?? 0));
                var headroom = cash - margin - reserved;
                var block = unresolved ? "An unresolved paper account window holds capital; new entries are suspended." :
                    headroom.Units.Sign < 0 ? "Insufficient paper margin for the remaining planned entry allocations." : null;
                var missing = active.Where(r => !windows[r.Candidate.ContractId].ContainsKey(minute)).ToArray();
                var awaitingPublication = missing.Where(r =>
                    time.GetUtcNow() < minute.AddMinutes(1) + CandlePublicationGrace &&
                    !windows[r.Candidate.ContractId].Keys.Any(open => open > minute)).ToHashSet();
                if (awaitingPublication.Count > 0)
                {
                    // A recent contiguous tail may not have been published yet. Commit only the proven account
                    // prefix, never advance a sibling across it or invent empty candles. Known interior/old holes
                    // still become gaps now, even if another symbol has a genuinely recent trailing omission.
                    foreach (var run in missing.Where(r => !awaitingPublication.Contains(r)))
                        ApplyMinute(run, EvaluateMinute(run, block));
                    foreach (var run in active.Where(r => r.CanEvaluate))
                    {
                        run.Touched = true;
                        run.State.CheckedAtUtc = time.GetUtcNow();
                        run.State.IssuesJson = JsonSerializer.Serialize(new[]
                        {
                            "Awaiting recent trailing candle publication. The complete account frontier is retained and will retry."
                        });
                    }
                    break;
                }
                var results = active.Select(run => (Run: run, Result: EvaluateMinute(run, block))).ToArray();
                // A newly missing/ambiguous source minute cannot finance another sibling's new admission. Retain
                // unambiguous protective exits, but do not choose a cross-instrument intraminute execution order.
                if (block is null && results.Any(x => x.Result.Issue is not null))
                    results = results.Select(x => x.Result.Issue is not null ? x : (x.Run, EvaluateMinute(x.Run,
                        "An unresolved paper account candle holds capital; new entries are suspended."))).ToArray();
                foreach (var (run, result) in results)
                {
                    ApplyMinute(run, result);
                    cash += result.Executions.Aggregate(default(ExactAmount), (sum, e) => sum + ExactAmount.From(e.PnlUsd));
                    margin += result.Executions.Aggregate(default(ExactAmount), (sum, e) => sum + ExactAmount.From(e.MarginUsd));
                }
                unresolved |= results.Any(x => x.Result.Issue is not null);

                PaperEvaluation EvaluateMinute(PaperRun run, string? reason)
                {
                    var entered = run.Executions.Where(e => e.Role == "entry").Select(e => e.EntryId).ToHashSet(StringComparer.Ordinal);
                    entered.UnionWith(run.State.DeclinedEntries().Select(e => e.EntryId));
                    var pending = PendingMargins(PendingPlans(run.Activations, minute), entered);
                    var rows = windows[run.Candidate.ContractId].GetValueOrDefault(minute) ?? [];
                    try
                    {
                        return PaperEvaluator.Evaluate(store.OwnerId, candidate.AccountId, run.Play.Id, run.Activations,
                            run.Executions, run.State.CheckpointUtc, run.State.PreviousClose, rows, minute.AddMinutes(1),
                            reason is not null ? default(ExactAmount) :
                                headroom + ExactAmount.From(run.Reservation?.AmountUsd ?? 0), reason, pending,
                            run.State.DeclinedEntries(), run.State.PreviousCloseAtUtc);
                    }
                    catch (OverflowException)
                    {
                        return new([], run.State.CheckpointUtc, run.State.PreviousClose,
                            "Simulated arithmetic exceeds supported decimal precision.", false);
                    }
                }
            }
            foreach (var run in runs.Where(r => r.Touched && r.CanEvaluate))
                run.State.NextCheckAtUtc = time.GetUtcNow().AddMinutes(run.State.CheckpointUtc >= SettledThrough(time.GetUtcNow())
                    ? settings.CheckIntervalMinutes : 1);
            return runs.Any(r => r.Touched);
        }, ct), ct), ct);

    private static bool SameAccountSet(IReadOnlyList<PaperDuePlay> expected, List<PaperDuePlay> actual) =>
        expected.Count == actual.Count && expected.ToHashSet().SetEquals(actual);

    private void ApplyMinute(PaperRun run, PaperEvaluation result)
    {
        run.Touched = true;
        foreach (var execution in result.Executions)
        {
            store.Add(execution);
            if (execution.Role != "entry") store.Add(PaperLedgerEntry.Realized(execution));
        }
        run.Executions.AddRange(result.Executions);
        var entered = run.Executions.Where(x => x.Role == "entry").Sum(x => x.Quantity);
        var exited = run.Executions.Where(x => x.Role != "entry").Sum(x => x.Quantity);
        if (entered > 0)
        {
            if (run.Play.ApplyPaperExecutionState(true, false, run.Executions.Where(x => x.Role == "entry").Min(x => x.OccurredAtUtc)) is { } opened)
                store.Add(opened);
            if (result.Issue is null && entered == exited && run.Play.ApplyPaperExecutionState(true, true,
                run.Executions.Where(x => x.Role != "entry").Max(x => x.OccurredAtUtc)) is { } closed) store.Add(closed);
        }
        run.State.CheckedAtUtc = time.GetUtcNow();
        run.State.CheckpointUtc = result.CheckpointUtc;
        run.State.PreviousClose = result.PreviousClose;
        run.State.PreviousCloseAtUtc = result.PreviousCloseAtUtc;
        if (result.Issue is null) run.State.SourceFailureSinceUtc = null;
        if (result.DeclinedEntries is not null) run.State.DeclinedEntriesJson = JsonSerializer.Serialize(result.DeclinedEntries);
        run.State.State = result.Issue is null ? "ready" : result.Ambiguous ? "ambiguous" : "gap";
        run.State.IssuesJson = JsonSerializer.Serialize(result.Issue is null ? Array.Empty<string>() : [result.Issue]);
        if (result.Issue is not null || run.Play.Status == PlayStatus.Closed) run.State.NextCheckAtUtc = null;
        if (run.Reservation is not null && result.Issue is null)
        {
            var enteredIds = run.Executions.Where(x => x.Role == "entry").Select(x => x.EntryId).ToHashSet(StringComparer.Ordinal);
            enteredIds.UnionWith(run.State.DeclinedEntries().Select(x => x.EntryId));
            run.Reservation.AmountUsd = run.Play.Status == PlayStatus.Closed ? 0 : PendingReservation(
                PendingPlans(run.Activations, result.CheckpointUtc ?? run.Candidate.FromUtc), enteredIds);
        }
    }

    private sealed class PaperRun(PaperDuePlay candidate, Play play, PaperSimulation state,
        List<PaperPlanActivation> activations, List<PaperExecution> executions, PaperReservation? reservation)
    {
        public PaperDuePlay Candidate { get; } = candidate;
        public Play Play { get; } = play;
        public PaperSimulation State { get; } = state;
        public List<PaperPlanActivation> Activations { get; } = activations;
        public List<PaperExecution> Executions { get; } = executions;
        public PaperReservation? Reservation { get; } = reservation;
        public bool Touched { get; set; }
        public bool CanEvaluate => State.State is "ready" or "waiting" && Play.Status is PlayStatus.Planned or PlayStatus.Open;
    }

    public static DateTimeOffset FloorMinute(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds() / 60 * 60);
    public static DateTimeOffset SettledThrough(DateTimeOffset value) => FloorMinute(value.AddSeconds(-10));
    public static DateTimeOffset NextMinute(DateTimeOffset value) => FloorMinute(value).AddMinutes(1);
    private static IEnumerable<PlayPlanDocument> PendingPlans(IReadOnlyList<PaperPlanActivation> activations, DateTimeOffset checkpoint)
    {
        var current = activations.Where(x => x.EffectiveAtUtc <= checkpoint).OrderBy(x => x.EffectiveAtUtc).ThenBy(x => x.PlanVersion).LastOrDefault();
        return activations.Where(x => x.EffectiveAtUtc > checkpoint).Concat(current is null ? [] : [current])
            .Select(x => PlayDocuments.Read(x.PlanJson));
    }
    private static decimal PendingReservation(IEnumerable<PlayPlanDocument> plans, HashSet<string> entered)
    {
        var exact = PendingMargins(plans, entered).Aggregate(default(ExactAmount),
            (sum, x) => sum + ExactAmount.From(x.Value));
        return ExactDecimal.TryParseVenue(exact.ToString(), out var result) ? result : throw PaperPrecision.Unsupported();
    }
    private static Dictionary<string, decimal> PendingMargins(IEnumerable<PlayPlanDocument> plans, HashSet<string> entered) =>
        PendingAllocations(plans, entered).ToDictionary(x => x.Key, x => x.Value.Margin, StringComparer.Ordinal);
    private static Dictionary<string, (decimal Quantity, decimal Margin)> PendingAllocations(
        IEnumerable<PlayPlanDocument> plans, HashSet<string> entered) =>
        plans.SelectMany(p => PaperEvaluator.Allocations(p)).Where(x => !entered.Contains(x.Key))
            .GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => (g.Max(x => x.Value.Quantity), g.Max(x => x.Value.Margin)), StringComparer.Ordinal);
    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
