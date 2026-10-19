using Vessel.Application.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Application.Workspace;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Monitoring;

/// <summary>Observes discrepancies only; never changes executions, links or Play status.</summary>
public sealed partial class MonitorDiscrepancyService(IMonitorDiscrepancyStore store, IUnitOfWork work,
    IJournalOwnerContext owner, IVesselEventSink events, MonitorOptions options, TimeProvider clock) : IMonitorEvaluator
{
    public static readonly TimeSpan ConfirmationDelay = DiscrepancyLifecycle.ConfirmationDelay;

    public async Task<MonitorEvaluationResult> EvaluateAsync(MonitorEvaluation observation, CancellationToken ct)
    {
        if (observation.Account.OwnerId != owner.OwnerId) throw new ProblemException(404, "Account not found.");
        var now = clock.GetUtcNow();
        // A Play that ended after the snapshot can still be in it; older endings cannot affect a fresh snapshot.
        var snapshotAt = observation.Read.Sync.Snapshot.ObservedAtUtc;
        var recent = now - MonitorOptions.Lease;
        // Positive evidence admits a terminal Play's subject even from a partial book: an old entry reported resting.
        // The same venue ID on another contract is another order, so the key is (contract, external ID).
        var resting = observation.Read.Orders?.Orders.Where(x => x.Status == Venues.VenueOrderStatus.Open)
            .Select(x => new OrderKey(observation.Account.Id, x.ContractId, x.OrderId)).Distinct().ToArray() ?? [];
        var facts = await store.LoadAsync(observation.Account.Id, snapshotAt > recent ? snapshotAt : recent, resting, ct);
        var records = (await store.RecordsAsync(observation.Account.Id, ct)).ToDictionary(x => (x.Kind, x.SubjectKey));
        var scan = Observe(observation, facts, now);
        DateTimeOffset? next = scan.Next;
        var seen = new HashSet<(string, string)>();
        foreach (var finding in scan.Findings)
        {
            var key = (finding.Kind, finding.Subject);
            seen.Add(key);
            var row = records.GetValueOrDefault(key);
            var json = JsonSerializer.Serialize(finding.Details);
            // Event data is capped at 32 KiB. Never silently truncate evidence used for confirmation. Display lists are
            // bounded where they can grow (fills, Plays); anything still larger is the conservative fallback: unknown.
            if (finding.Present is null || Encoding.UTF8.GetByteCount(json) > 16_000)
            {
                // Structural unknowns are undecidable by design: they neither confirm nor resolve, but are not missing evidence.
                if (finding.Present is not null) scan.Incomplete = true;
                if (row is not null) await Unobservable(row);
                continue;
            }
            if (row is null)
            {
                if (!finding.Present.Value) continue;
                row = new PlayDiscrepancy
                {
                    OwnerId = owner.OwnerId, AccountId = observation.Account.Id, PlayId = finding.PlayId,
                    ContractId = finding.Contract, NativeContractId = finding.Native, Kind = finding.Kind,
                    SubjectKey = finding.Subject, Severity = finding.Severity, OpenedAtUtc = finding.At
                };
                store.Add(row);
            }
            // A repeated delivery of the same source snapshot is not independent confirmation.
            else if (finding.At <= row.LastObservedAtUtc) { scan.Incomplete = true; continue; }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                finding.Identity is null ? json : JsonSerializer.Serialize(finding.Identity))));
            // Keep the stored text when it is equivalent, so an unchanged baseline is not rewritten every run.
            if (JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(row.Details), JsonSerializer.Deserialize<JsonElement>(json)))
                json = row.Details;
            var plays = row.InvolvedPlayIds.SequenceEqual(finding.PlayIds) ? row.InvolvedPlayIds : finding.PlayIds;
            var change = DiscrepancyLifecycle.Apply(row, finding.Present.Value
                ? DiscrepancyInput.Present(now, finding.At, fingerprint, json, plays)
                : DiscrepancyInput.Absent(now, finding.At, fingerprint, json, plays));
            if (change.ConfirmationDueAtUtc is { } due) next = Earlier(next, due > now ? due : now + ConfirmationDelay);
            await Publish(row, change, observation.RunId, observation.Account.VenueId, observation.Trigger, now, ct);
        }
        foreach (var row in records.Values.Where(x => !seen.Contains((x.Kind, x.SubjectKey))))
        {
            // A missing subject (e.g. retired catalogue identity) is not proof that a condition cleared.
            await Unobservable(row);
            if (row.HasWork) scan.Incomplete = true;
        }
        return new(next, scan.Incomplete, scan.Incomplete ? "Some discrepancy checks lack fresh, complete or consistent evidence." : null,
            scan.Coverage);

        async Task Unobservable(PlayDiscrepancy row)
        {
            DiscrepancyLifecycle.Apply(row, DiscrepancyInput.Unknown(now));
            await Publish(row, DiscrepancyLifecycle.Apply(row, DiscrepancyInput.Expiry(now, options.UnobservableLifetime)),
                observation.RunId, observation.Account.VenueId, observation.Trigger, now, ct);
        }
    }

    /// <summary>
    /// Housekeeping when a run made no decisions (a failed, discarded or refresh run), under the account fence, so such
    /// runs can neither hold the account forever nor let a later success confirm on a stale timer. Nothing is decided.
    /// </summary>
    public async Task SettleAsync(Vessel.Domain.Accounts.Account account, Guid runId, string trigger, bool interrupted, CancellationToken ct)
    {
        if (account.OwnerId != owner.OwnerId) throw new ProblemException(404, "Account not found.");
        var now = clock.GetUtcNow();
        foreach (var row in await store.RecordsAsync(account.Id, ct))
        {
            if (interrupted) DiscrepancyLifecycle.Apply(row, DiscrepancyInput.Unknown(now));
            await Publish(row, DiscrepancyLifecycle.Apply(row, DiscrepancyInput.Expiry(now, options.UnobservableLifetime)),
                runId, account.VenueId, trigger, now, ct);
        }
    }

    public async Task<IReadOnlyList<MonitorDiscrepancyDto>> ReadAsync(Guid accountId, Guid? playId, CancellationToken ct) =>
        (await ReadListAsync(accountId, playId, ct)).Records;

    public async Task<MonitorDiscrepancyList> ReadListAsync(Guid accountId, Guid? playId, CancellationToken ct)
    {
        await RequireSubject(accountId, playId, ct);
        var visible = (await store.RecordsAsync(accountId, ct)).Where(x => x.Visible &&
            (playId is null || x.PlayId == playId || x.InvolvedPlayIds.Contains(playId.Value))).ToArray();
        // Outstanding incidents first, so historical records can never push them past the cap; each group keeps the
        // previous order (newest confirmation first, then ID).
        var listed = visible.OrderByDescending(x => x.Outstanding).ThenByDescending(x => x.ConfirmedAtUtc).ThenBy(x => x.Id)
            .Take(MonitorDiscrepancyList.MaxRecords).Select(Dto).ToArray();
        return new(listed, visible.Length > listed.Length);
    }

    /// <summary>Owner/account and optional involved-Play membership are validated under the account fence.</summary>
    public async Task DismissPlayAsync(Guid playId, Guid discrepancyId, string? note, CancellationToken ct)
    {
        var play = await store.PlayAsync(playId, ct);
        if (play?.OwnerId != owner.OwnerId) throw new ProblemException(404, "Play not found.");
        await DismissAsync(play.AccountId, playId, discrepancyId, note, ct);
    }

    public Task DismissAsync(Guid accountId, Guid? playId, Guid discrepancyId, string? note, CancellationToken ct) =>
        work.ExecuteAsync(async () =>
        {
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (note?.Length > PlayDiscrepancy.MaxNoteLength || note?.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) == true)
                throw new ProblemException(400, $"Notes must be plain text of at most {PlayDiscrepancy.MaxNoteLength} characters.");
            await store.LockAsync(accountId, ct);
            var account = await RequireSubject(accountId, playId, ct);
            var row = (await store.RecordsAsync(accountId, ct)).SingleOrDefault(x => x.Id == discrepancyId && x.Visible &&
                (playId is null || x.PlayId == playId || x.InvolvedPlayIds.Contains(playId.Value)))
                ?? throw new ProblemException(404, "Discrepancy not found.");
            var now = clock.GetUtcNow();
            // Anything still publicly announced (opened or unverifiable) can be dismissed; the dismissal closes it.
            var change = DiscrepancyLifecycle.Apply(row, DiscrepancyInput.Dismissal(now, note));
            if (change.Rejected) throw new ProblemException(409, "Only open or unverifiable discrepancies can be dismissed.");
            await Publish(row, change, Guid.NewGuid(), account.VenueId, "owner", now, ct);
            return true;
        }, ct);

    private async Task<Vessel.Domain.Accounts.Account> RequireSubject(Guid accountId, Guid? playId, CancellationToken ct)
    {
        var account = await store.AccountAsync(accountId, ct);
        if (account?.OwnerId != owner.OwnerId) throw new ProblemException(404, "Account not found.");
        if (playId is { } id)
        {
            var play = await store.PlayAsync(id, ct);
            if (play?.AccountId != accountId || play.OwnerId != owner.OwnerId) throw new ProblemException(404, "Play not found.");
        }
        return account;
    }

    /// <summary>
    /// One event per published transition, deduplicated by its revision. <c>discrepancy.unverifiable</c> means
    /// verification ended without a decision; resolved and dismissed both publish <c>discrepancy.resolved</c>.
    /// </summary>
    private Task Publish(PlayDiscrepancy row, DiscrepancyTransition change, Guid run, string venue, string trigger, DateTimeOffset now, CancellationToken ct)
    {
        if (change.Publish is not { } transition) return Task.CompletedTask;
        return events.AppendAsync(new(transition switch
            {
                DiscrepancyLifecycle.Opened => "discrepancy.opened",
                DiscrepancyLifecycle.Unverifiable => "discrepancy.unverifiable",
                _ => "discrepancy.resolved"
            }, run,
            $"discrepancy:{row.Id}:{row.Revision}:{transition}", transition switch
            {
                DiscrepancyLifecycle.Opened => row.Severity,
                DiscrepancyLifecycle.Unverifiable => "warning",
                _ => "info"
            }, now, now,
            new(AccountId: row.AccountId, VenueId: venue, ContractId: row.ContractId, PlayId: row.PlayId, DiscrepancyId: row.Id),
            new(trigger == "owner" ? "owner" : "monitor"), transition == DiscrepancyLifecycle.Unverifiable
                ? JsonSerializer.SerializeToElement(new { kind = row.Kind, state = row.State, outcome = "unverifiable",
                    lastObservedAtUtc = row.LastObservedAtUtc, unverifiableAtUtc = row.UnverifiableAtUtc,
                    fingerprint = row.Fingerprint, details = JsonSerializer.Deserialize<JsonElement>(row.Details) })
                : JsonSerializer.SerializeToElement(new { kind = row.Kind, state = row.State,
                    resolution = transition == DiscrepancyLifecycle.Opened ? null : transition,
                    fingerprint = row.Fingerprint, details = JsonSerializer.Deserialize<JsonElement>(row.Details), note = row.Note })), ct);
    }

    private static MonitorDiscrepancyDto Dto(PlayDiscrepancy row) => new(row.Id, row.AccountId, row.PlayId, row.ContractId,
        row.Kind, row.Severity, row.State, row.OpenedAtUtc, row.ConfirmedAtUtc, row.ResolvedAtUtc, row.DismissedAtUtc,
        row.Note, JsonSerializer.Deserialize<JsonElement>(row.Details), row.UnverifiableAtUtc is null ? null : "unverifiable", row.UnverifiableAtUtc,
        row.LastObservedAtUtc == default ? null : row.LastObservedAtUtc);
    private static DateTimeOffset Earlier(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value < b ? value : b;
}
