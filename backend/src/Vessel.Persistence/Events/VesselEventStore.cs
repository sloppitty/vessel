using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Application.Common;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Events;

/// <summary>
/// Appends events in the unit of work. Captured mutations and explicit drafts are sequenced together at the end
/// of the transaction, facts before the events that describe their consequences, under the owner cursor lock.
/// </summary>
public sealed partial class VesselEventStore(VesselDbContext db, VesselEventMutationCapture mutations,
    IVesselEventOutbox outbox, IOptions<VesselEventSettings> settings, ILogger<VesselEventStore> logger)
    : IVesselEventSink, IUnitOfWorkParticipant, IMonitorCommandSink
{
    private readonly List<VesselEventDraft> pending = [];
    private readonly List<MonitorAccountCommand> commands = [];
    private Guid runId = Guid.CreateVersion7();
    private string trigger = "owner";

    public Task SetRunAsync(Guid runId, string trigger, CancellationToken cancellationToken) => VesselEventSql.ParticipateAsync(db, () =>
    {
        if (runId == Guid.Empty || trigger is not ("owner" or "monitor"))
            throw new ArgumentException("Invalid event run.");
        this.runId = runId; this.trigger = trigger;
        return Task.CompletedTask;
    }, cancellationToken);

    public Task AppendAsync(VesselEventDraft draft, CancellationToken cancellationToken) => VesselEventSql.ParticipateAsync(db, () =>
    {
        pending.Add(Validated(draft));
        return Task.CompletedTask;
    }, cancellationToken);

    private static VesselEventDraft Validated(VesselEventDraft draft)
    {
        if (!VesselEventValues.Types.Contains(draft.Type) || draft.Version != 1 || draft.RunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(draft.DedupKey) || draft.DedupKey.Length > 1024 ||
            draft.Severity is not ("info" or "warning" or "critical") ||
            draft.Timeliness is not ("timely" or "late") || draft.Relevance is not ("current" or "superseded") ||
            draft.Source.Trigger is not ("owner" or "monitor") || draft.Data.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(draft.Data.GetRawText()) > 32_768 ||
            !ValidIds(draft.Source.OrderIds) || !ValidIds(draft.Source.FillIds))
            throw new ArgumentException("Invalid event.");
        // Clone so disposing the producer's JsonDocument cannot invalidate the buffered event.
        return draft with
        {
            Data = draft.Data.Clone(),
            Source = draft.Source with { OrderIds = draft.Source.OrderIds?.ToArray(), FillIds = draft.Source.FillIds?.ToArray() }
        };
    }

    private static bool ValidIds(string[]? ids) => ids is null ||
        ids.Length <= 200 && ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256);

    public Task EnqueueAsync(MonitorAccountCommand command, CancellationToken cancellationToken) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        if (command.OwnerId != db.CurrentOwnerId || command.RequestId == Guid.Empty ||
            command.Trigger is not ("manual" or "scheduled") ||
            !await db.Accounts.AnyAsync(a => a.Id == command.AccountId && a.IsEnabled, cancellationToken))
            throw new ProblemException(409, "The account is not eligible for monitoring.");
        commands.Add(command);
    }, cancellationToken);

    public Task EnsureMonitoringBaselineAsync(Guid accountId, CancellationToken cancellationToken) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId && a.IsEnabled, cancellationToken))
            throw new ProblemException(404, "Account not found.");
        // First insert wins and commits with every existing source identity. A rollback leaves neither.
        await VesselEventSql.ExecuteAsync(db, """
            WITH baseline AS (
                INSERT INTO event_baselines(owner_id,account_id,started_at)
                VALUES(@owner,@account,@now) ON CONFLICT DO NOTHING RETURNING owner_id
            )
            INSERT INTO event_fills(owner_id,account_id,contract_id,source_fill_id)
                SELECT f."OwnerId",f."AccountId",f."ContractId",f."SourceFillId"
                FROM imported_fills f, baseline b WHERE f."OwnerId"=@owner AND f."AccountId"=@account
            ON CONFLICT DO NOTHING
            """, cancellationToken, ("owner", db.CurrentOwnerId), ("account", accountId), ("now", DateTimeOffset.UtcNow));
    }, cancellationToken);

    public Task ClaimHistoricalFillsAsync(IReadOnlyCollection<ImportedFill> fills, CancellationToken cancellationToken) =>
        VesselEventSql.ParticipateAsync(db, async () =>
        {
            if (fills.Any(f => f.OwnerId != db.CurrentOwnerId)) throw new UnauthorizedAccessException();
            await ClaimFillsAsync(fills, cancellationToken);
        }, cancellationToken);

    public Task BeforeCommitAsync(CancellationToken cancellationToken) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        // Facts (links, fills, status changes) first; explicit drafts such as discrepancies describe them.
        var drafts = (await CaptureMutationsAsync(cancellationToken)).Select(Validated).Concat(pending).ToList();
        if (drafts.Count != 0)
        {
            // No nextval. This row lock remains held until the OUTER transaction completes.
            await VesselEventSql.ExecuteAsync(db, "INSERT INTO event_cursors(owner_id) VALUES(@owner) ON CONFLICT DO NOTHING",
                cancellationToken, ("owner", db.CurrentOwnerId));
            await VesselEventSql.ScalarAsync(db, "SELECT sequence FROM event_cursors WHERE owner_id=@owner FOR UPDATE",
                cancellationToken, ("owner", db.CurrentOwnerId));
            foreach (var draft in drafts)
                await PersistAsync(draft, cancellationToken);
        }
        foreach (var command in commands) await outbox.StageAsync(command, cancellationToken);
        // There is no SaveChangesAndFlushMessagesAsync here. Envelope writes enlist in db's actual transaction.
    }, cancellationToken);

    public async Task AfterCommitAsync(CancellationToken cancellationToken)
    {
        try { await outbox.FlushAsync(CancellationToken.None); }
        catch (Exception error)
        {
            // The commit succeeded. Wolverine's durable envelopes and subscriber catch-up own recovery.
            logger.LogWarning("Event dispatch is deferred to durable recovery after a successful commit ({ErrorType}).",
                error.GetType().Name);
        }
        finally { Reset(); }
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        Reset();
        return Task.CompletedTask;
    }

    private void Reset()
    {
        pending.Clear(); commands.Clear(); mutations.Clear(); outbox.Reset();
        runId = Guid.CreateVersion7(); trigger = "owner";
    }

    private async Task PersistAsync(VesselEventDraft draft, CancellationToken ct)
    {
        await ValidateSubjectAsync(draft.Subject, ct);
        var id = Guid.CreateVersion7();
        // The event and its dedup identity are created together; only account deletion removes both.
        if (await VesselEventSql.ScalarAsync(db, """
            INSERT INTO event_dedup(owner_id,dedup_key,event_id,account_id) VALUES(@owner,@key,@id,@account)
            ON CONFLICT DO NOTHING RETURNING event_id
            """, ct, ("owner", db.CurrentOwnerId), ("key", draft.DedupKey), ("id", id), ("account", draft.Subject.AccountId)) is null)
            return;
        var sequence = (long)(await VesselEventSql.ScalarAsync(db,
            "UPDATE event_cursors SET sequence=sequence+1 WHERE owner_id=@owner RETURNING sequence",
            ct, ("owner", db.CurrentOwnerId)))!;
        var message = new VesselEvent(id, draft.Type, draft.Version, db.CurrentOwnerId,
            sequence.ToString(CultureInfo.InvariantCulture), draft.RunId, draft.DedupKey, draft.Severity,
            draft.OccurredAt.ToUniversalTime(), draft.DetectedAt.ToUniversalTime(), draft.Timeliness,
            draft.Relevance, draft.ExpiresAt?.ToUniversalTime(), draft.Subject, draft.Source, draft.Data);
        await VesselEventSql.ExecuteAsync(db, """
            INSERT INTO events(owner_id,sequence,event_id,type,account_id,detected_at,expires_at,payload)
            VALUES(@owner,@sequence,@id,@type,@account,@detected,@expires,CAST(@payload AS jsonb))
            """, ct, ("owner", db.CurrentOwnerId), ("sequence", sequence), ("id", id), ("type", draft.Type),
            ("account", draft.Subject.AccountId), ("detected", message.DetectedAt), ("expires", message.ExpiresAt),
            ("payload", JsonSerializer.Serialize(message, VesselEventJson.Options)));
        await outbox.StageAsync(message, ct);
    }

    private async Task ValidateSubjectAsync(VesselEventSubject subject, CancellationToken ct)
    {
        if (subject.AccountId is { } accountId && !await db.Accounts.AnyAsync(a => a.Id == accountId, ct))
            throw new ProblemException(404, "Event account not found.");
        if (subject.PlayId is { } playId && !await db.Plays.AnyAsync(p => p.Id == playId &&
            (!subject.AccountId.HasValue || p.AccountId == subject.AccountId), ct))
            throw new ProblemException(404, "Event Play not found.");
    }
}
