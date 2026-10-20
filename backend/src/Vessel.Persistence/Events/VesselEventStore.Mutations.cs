using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Events;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Events;

public sealed partial class VesselEventStore
{
    /// <summary>Drafts for this unit's captured mutations in causal order: link changes, then the fills they
    /// claim, then the status changes those facts caused.</summary>
    private async Task<List<VesselEventDraft>> CaptureMutationsAsync(CancellationToken ct)
    {
        var drafts = new List<VesselEventDraft>();
        var detected = DateTimeOffset.UtcNow;
        // Cached for this capture only; tracked entities already resolve to one instance per key.
        var lookups = new CaptureLookups(db);
        var linkIds = mutations.Links.Select(l => l.LinkId).Distinct().ToList();
        var finalStates = linkIds.Count == 0 ? [] : await db.OrderLinks.Where(l => linkIds.Contains(l.Id))
            .Select(l => new { l.Id, l.State }).ToDictionaryAsync(l => l.Id, l => l.State, ct);
        // A link's order is immutable and always retained; payloads keep its external ID.
        var linkOrderIds = mutations.LinkOrders.Select(x => x.ImportedOrderId).Distinct().ToArray();
        var linkOrders = linkOrderIds.Length == 0 ? [] : await db.Orders.Where(o => linkOrderIds.Contains(o.Id))
            .AsNoTracking().ToDictionaryAsync(o => o.Id, ct);
        foreach (var link in mutations.Links)
        {
            var order = linkOrders.GetValueOrDefault(link.ImportedOrderId)
                ?? throw new InvalidOperationException("Captured link order not found.");
            var account = await lookups.AccountAsync(link.AccountId, ct);
            var play = await lookups.PlayAsync(link.PlayId, ct)
                ?? throw new InvalidOperationException("Captured link Play not found.");
            var subject = new VesselEventSubject(link.AccountId, account.VenueId, play.ContractId, link.PlayId, link.EntryId, link.LevelId);
            var source = new VesselEventSource(link.Source == OrderLinkSource.Owner ? "owner" : trigger, [order.ExternalOrderId]);
            var data = VesselEventJson.Element(new
            {
                linkId = link.LinkId, previous = link.Previous, current = link.Current,
                role = link.Role.ToString().ToLowerInvariant(), source = link.Source.ToString().ToLowerInvariant()
            });
            OrderLinkState? finalState = finalStates.TryGetValue(link.LinkId, out var state) ? state : null;
            var relevance = (finalState?.ToString().ToLowerInvariant() ?? "unlinked") == link.Current ? "current" : "superseded";
            if (link.Current == "suggested")
                drafts.Add(new("link.needs-confirmation", runId, $"link:{link.LinkId}:suggested", "warning",
                    link.OccurredAt, detected, subject, source, data, Timeliness(link.OccurredAt, detected), relevance));
            else
                drafts.Add(new("execution.link-changed", runId, $"link-change:{link.ChangeId}", "info",
                    link.OccurredAt, detected, subject, source, data, Timeliness(link.OccurredAt, detected), relevance));
        }
        drafts.AddRange(await CaptureFillsAsync(lookups, linkOrders, detected, ct));
        foreach (var change in mutations.StatusChanges)
        {
            var play = await lookups.PlayAsync(change.PlayId, ct);
            if (play is null) continue;
            // An Unresolved Play that its fills settle closes with the same closed-result event as an Open one (1.3a).
            var type = change.To == PlayStatus.Open && change.From is PlayStatus.Planned or PlayStatus.Paused
                ? "play.opened" : change.From is PlayStatus.Open or PlayStatus.Unresolved && change.To == PlayStatus.Closed
                    ? "play.closed" : "play.status-changed";
            var key = type == "play.opened" ? $"play:{play.Id}:opened" : type == "play.closed"
                ? $"play:{play.Id}:closed" : $"status-change:{change.Id}";
            var account = await lookups.AccountAsync(play.AccountId, ct);
            var occurred = await LifecycleSourceTimeAsync(play, change, ct);
            var data = type == "play.closed" ? await ClosedDataAsync(play, ct) : VesselEventJson.Element(new
            {
                previous = change.From.ToString().ToLowerInvariant(), current = change.To.ToString().ToLowerInvariant(),
                source = change.Source.ToString().ToLowerInvariant(),
                reason = change.UnresolvedReason is { } unresolved ? PlayUnresolved.ReasonName(unresolved) : change.Reason?.ToString().ToLowerInvariant()
            });
            drafts.Add(new(type, runId, key, change.From == PlayStatus.Paused && change.To == PlayStatus.Open
                ? "warning" : "info", occurred, detected,
                new(play.AccountId, account.VenueId, play.ContractId, play.Id), new(trigger), data,
                Timeliness(occurred, detected), play.Status == change.To ? "current" : "superseded"));
        }
        return drafts;
    }

    private async Task<DateTimeOffset> LifecycleSourceTimeAsync(Play play, PlayStatusChange change, CancellationToken ct)
    {
        // Mutations still decide whether an event exists. Linked facts only supply its source timestamp.
        // Manual facts and owner lifecycle actions retain their action time, even when facts are backdated.
        var fallback = change.OccurredAtUtc;
        if (play.InstrumentSource != InstrumentSource.Venue || change.Source != StatusChangeSource.Venue ||
            change.To is not (PlayStatus.Open or PlayStatus.Closed)) return fallback;
        var rows = await db.LinkedOrders(db.OrderLinks.Where(l => l.PlayId == play.Id && l.State == OrderLinkState.Linked)).ToListAsync(ct);
        var links = rows.Select(r => r.Link).ToList();
        var orders = rows.Select(r => r.Order).DistinctBy(o => o.Id).ToList();
        var orderOf = orders.ToDictionary(o => o.Id);
        var fills = (await db.FillsOf(db.Orders.Where(o => orderOf.Keys.Contains(o.Id)))
            .Where(f => f.AccountId == play.AccountId && f.ContractId == play.ContractId).ToListAsync(ct)).ToLookup(OrderKey.Of);
        var facts = links.SelectMany(l => fills[OrderKey.Of(orderOf[l.ImportedOrderId])]
            .Select(f => new LinkedFill(l.Role, f))).ToList();
        if (facts.Any(f => f.Fill.Quantity <= 0)) return fallback;
        var side = ExecutionIntegrity.EntrySide(PlayDocuments.Read(play.Plan));
        var counted = facts.Where(f => !ExecutionIntegrity.Contradicts(f.Role, side, f.Fill)).ToList();
        var entries = counted.Where(f => f.Role == OrderLinkRole.Entry).ToList();
        if (entries.Count == 0) return fallback;
        var incompleteOrders = orders.Where(o => ExecutionIntegrity.MissingExecutions(o, fills[OrderKey.Of(o)])).ToList();
        var incomplete = incompleteOrders.Select(o => o.ExternalOrderId).ToHashSet(StringComparer.Ordinal);
        var incompleteIds = incompleteOrders.Select(o => o.Id).ToHashSet();
        if (change.To == PlayStatus.Open)
        {
            // A partial resting order is valid evidence. Known missing executions cannot establish the first fill.
            if (links.Any(l => l.Role == OrderLinkRole.Entry && incompleteIds.Contains(l.ImportedOrderId))) return fallback;
            var opened = entries.Min(f => f.Fill.OccurredAtUtc);
            // An implausible future source timestamp is not evidence that a mutation happened in the future.
            return opened <= fallback ? opened : fallback;
        }
        var entryOrders = links.Where(l => l.Role == OrderLinkRole.Entry).Select(l => l.ImportedOrderId).ToHashSet();
        if (orders.Any(o => entryOrders.Contains(o.Id) && o.Status == "open")) return fallback;
        // The same latest valid exit the outcome calculator scores, clamped to the detection time.
        return PlayExitTime.LatestValidExit(new(play, [], facts, incomplete), fallback) ?? fallback;
    }

    /// <summary>
    /// One draft per affected order with newly claimed fills, ordered by (account, order). Set-based: one baseline
    /// read, one linked-order read, one fill read and one identity claim for the unit, so an unlinked order costs no
    /// round trip. Claims cover exactly what per-order capture claimed (baselined accounts, linked orders, fills on
    /// the Play's contract), including historical, pre-baseline and contradictory fills whose events are suppressed.
    /// </summary>
    private async Task<List<VesselEventDraft>> CaptureFillsAsync(CaptureLookups lookups, IReadOnlyDictionary<Guid, ImportedOrder> linkOrders,
        DateTimeOffset detected, CancellationToken ct)
    {
        var drafts = new List<VesselEventDraft>();
        // Orders are identified by account, contract and external ID; a changed link contributes its order's identity.
        var affected = mutations.FillOrders.Select(x => new OrderKey(x.AccountId, x.ContractId, x.ExternalOrderId))
            .Concat(mutations.LinkOrders.Select(x => linkOrders.GetValueOrDefault(x.ImportedOrderId)).OfType<ImportedOrder>().Select(OrderKey.Of))
            .Distinct().OrderBy(x => x.AccountId).ThenBy(x => x.ExternalOrderId, StringComparer.Ordinal)
            .ThenBy(x => x.ContractId, StringComparer.Ordinal).ToList();
        if (affected.Count == 0) return drafts;
        var baselines = new Dictionary<Guid, DateTimeOffset>();
        await using (var command = VesselEventSql.TransactionCommand(db, """
            SELECT account_id, started_at FROM event_baselines WHERE owner_id=@owner AND account_id = ANY(@accounts)
            """, ("owner", db.CurrentOwnerId), ("accounts", affected.Select(x => x.AccountId).Distinct().ToArray())))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                baselines[reader.GetGuid(0)] = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
        affected.RemoveAll(x => !baselines.ContainsKey(x.AccountId));
        if (affected.Count == 0) return drafts;

        var accountIds = affected.Select(x => x.AccountId).Distinct().ToArray();
        var externalIds = affected.Select(x => x.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        var wanted = affected.ToHashSet();
        // The candidate read may over-fetch across accounts and contracts; the OrderKey rule keeps the wanted orders.
        var retained = (await db.Orders.Where(o => accountIds.Contains(o.AccountId) && externalIds.Contains(o.ExternalOrderId))
                .ToListAsync(ct)).Where(o => wanted.Contains(OrderKey.Of(o))).ToDictionary(OrderKey.Of);
        var retainedIds = retained.Values.Select(o => o.Id).ToArray();
        // A linked link is unique per (owner, order).
        var links = retainedIds.Length == 0 ? [] : (await db.OrderLinks.Where(l => retainedIds.Contains(l.ImportedOrderId) &&
                l.State == OrderLinkState.Linked).ToListAsync(ct))
            .GroupBy(l => l.ImportedOrderId).ToDictionary(g => g.Key, g => g.Single());
        var linked = affected.Where(x => retained.TryGetValue(x, out var order) && links.ContainsKey(order.Id)).ToList();
        if (linked.Count == 0) return drafts;

        await lookups.LoadPlaysAsync(linked.Select(x => links[retained[x].Id].PlayId), ct);
        var linkedAccounts = linked.Select(x => x.AccountId).Distinct().ToArray();
        var linkedOrders = linked.Select(x => x.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        // Within each order the database order is the per-order read's (occurred, source fill id) order.
        var candidates = await db.Fills.Where(f => linkedAccounts.Contains(f.AccountId) && linkedOrders.Contains(f.ExternalOrderId))
            .OrderBy(f => f.AccountId).ThenBy(f => f.ExternalOrderId).ThenBy(f => f.OccurredAtUtc).ThenBy(f => f.SourceFillId)
            .ToListAsync(ct);
        var byOrder = candidates.GroupBy(OrderKey.Of).ToDictionary(g => g.Key, g => g.ToList());
        var orders = new List<(PlayOrderLink Link, ImportedOrder Order, Play Play, List<ImportedFill> Fills)>();
        foreach (var key in linked)
        {
            var order = retained[key];
            var link = links[order.Id];
            var play = await lookups.PlayAsync(link.PlayId, ct)
                ?? throw new InvalidOperationException("Linked Play not found.");
            var fills = byOrder.TryGetValue(key, out var rows) ? rows.Where(f => f.ContractId == play.ContractId).ToList() : [];
            if (fills.Count != 0) orders.Add((link, order, play, fills));
        }
        if (orders.Count == 0) return drafts;

        var claimed = await ClaimFillsAsync(orders.SelectMany(o => o.Fills), ct);
        var emitting = new List<(PlayOrderLink Link, ImportedOrder Order, Play Play, List<ImportedFill> Fills, List<ImportedFill> New)>();
        foreach (var (link, order, play, fills) in orders)
        {
            // Retrospective creation links history, not a new watched execution.
            var historical = play.Origin == PlayOrigin.Retrospective;
            var baseline = baselines[link.AccountId];
            var newlyClaimed = fills.Where(f => claimed.Contains(f) && !historical && f.OccurredAtUtc >= baseline).ToList();
            if (newlyClaimed.Count == 0) continue;
            // Integrity rules match the existing lifecycle, not the venue's raw side vocabulary.
            var side = ExecutionIntegrity.EntrySide(PlayDocuments.Read(play.Plan));
            newlyClaimed.RemoveAll(f => ExecutionIntegrity.Contradicts(link.Role, side, f));
            if (newlyClaimed.Count != 0) emitting.Add((link, order, play, fills, newlyClaimed));
        }
        if (emitting.Count == 0) return drafts;

        foreach (var (link, order, play, fills, newlyClaimed) in emitting)
        {
            var accountId = link.AccountId;
            var account = await lookups.AccountAsync(accountId, ct);
            var side = ExecutionIntegrity.EntrySide(PlayDocuments.Read(play.Plan));
            var counted = fills.Where(f => !ExecutionIntegrity.Contradicts(link.Role, side, f)).ToList();
            var cumulative = VesselEventValues.Sum(counted.Select(f => f.Quantity));
            var occurred = newlyClaimed.Max(f => f.OccurredAtUtc);
            var data = VesselEventJson.Element(new
            {
                role = link.Role.ToString().ToLowerInvariant(), side = newlyClaimed[0].Side,
                filledQuantity = VesselEventValues.Sum(newlyClaimed.Select(f => f.Quantity)), cumulativeQuantity = cumulative,
                remainingQuantity = VesselEventValues.Exact(order.RemainingSize),
                averagePrice = VesselEventValues.AveragePrice(counted.Select(f => (f.Price, f.Quantity))),
                sourceFillCount = newlyClaimed.Count, sourceFillIdsComplete = newlyClaimed.Count <= 200,
                // A venue's "filled" status alone does not prove all executions are retained.
                complete = order.Status == "filled" && !ExecutionIntegrity.MissingExecutions(order, counted)
            });
            // The external ID alone collides across contracts, so the dedup key uses the internal order ID.
            drafts.Add(new("execution.filled", runId, $"order:{accountId}:{order.Id}:filled:{cumulative}",
                link.Role == OrderLinkRole.Stop ? "warning" : "info", occurred, detected,
                new(accountId, account.VenueId, play.ContractId, play.Id, link.EntryId, link.LevelId),
                new(trigger, [order.ExternalOrderId], newlyClaimed.Take(200).Select(f => f.SourceFillId).ToArray()), data,
                Timeliness(occurred, detected), play.Status is PlayStatus.Closed or PlayStatus.Cancelled ? "superseded" : "current"));
        }
        return drafts;
    }

    /// <summary>
    /// Claims every fill identity in one statement, in sequence order, and returns the fills it newly claimed.
    /// RETURNING is unordered, so rows map back by identity; a repeated identity counts only at its first position,
    /// as the sequential claims did.
    /// </summary>
    private async Task<HashSet<ImportedFill>> ClaimFillsAsync(IEnumerable<ImportedFill> sequence, CancellationToken ct)
    {
        var first = new Dictionary<(Guid Account, string Contract, string Fill), ImportedFill>();
        var keys = new List<(Guid Account, string Contract, string Fill)>();
        foreach (var fill in sequence)
            if (first.TryAdd((fill.AccountId, fill.ContractId, fill.SourceFillId), fill))
                keys.Add((fill.AccountId, fill.ContractId, fill.SourceFillId));
        var claimed = new HashSet<ImportedFill>(ReferenceEqualityComparer.Instance);
        if (keys.Count == 0) return claimed;
        await using var command = VesselEventSql.TransactionCommand(db, """
            INSERT INTO event_fills(owner_id,account_id,contract_id,source_fill_id)
            SELECT @owner, t.account_id, t.contract_id, t.source_fill_id
            FROM unnest(@accounts, @contracts, @fills) WITH ORDINALITY AS t(account_id, contract_id, source_fill_id, position)
            ORDER BY t.position
            ON CONFLICT DO NOTHING RETURNING account_id, contract_id, source_fill_id
            """, ("owner", db.CurrentOwnerId), ("accounts", keys.Select(k => k.Account).ToArray()),
            ("contracts", keys.Select(k => k.Contract).ToArray()), ("fills", keys.Select(k => k.Fill).ToArray()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            claimed.Add(first[(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))]);
        return claimed;
    }

    private async Task<JsonElement> ClosedDataAsync(Play play, CancellationToken ct)
    {
        var facts = (await db.LinkedFills(db.OrderLinks.Where(l => l.PlayId == play.Id && l.State == OrderLinkState.Linked))
            .Where(x => x.Fill.AccountId == play.AccountId).Select(x => new { x.Link.Role, x.Fill }).ToListAsync(ct))
            .Select(x => (x.Role, x.Fill)).ToList();
        if (play.InstrumentSource == InstrumentSource.Manual)
        {
            var rows = await db.ManualExecutions.Where(f => f.PlayId == play.Id).ToListAsync(ct);
            var ledger = ManualLedger.Calculate(play, rows);
            facts = ledger.Projection.Select(x => (x.Role, x.Fill)).ToList();
        }
        var counted = facts.Where(f => !ExecutionIntegrity.Contradicts(f.Role,
            ExecutionIntegrity.EntrySide(PlayDocuments.Read(play.Plan)), f.Fill)).ToList();
        var basis = counted.Select(f => f.Fill.PnlBasis).Distinct(StringComparer.Ordinal).ToList();
        return VesselEventJson.Element(new
        {
            enteredQuantity = VesselEventValues.Sum(counted.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill.Quantity)),
            exitedQuantity = VesselEventValues.Sum(counted.Where(f => f.Role != OrderLinkRole.Entry).Select(f => f.Fill.Quantity)),
            closedPnlUsd = VesselEventValues.Sum(counted.Select(f => f.Fill.ClosedPnlUsd)),
            closedPnlBasis = basis.Count == 1 ? basis[0] : basis.Count == 0 ? ExecutionFacts.PnlGross : "mixed",
            fees = counted.GroupBy(f => f.Fill.FeeToken).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { token = g.Key, amount = VesselEventValues.Sum(g.Select(f => f.Fill.Fee)) }).ToArray()
        });
    }

    /// <summary>Account and Play reads shared by one capture; a missing Play stays missing.</summary>
    private sealed class CaptureLookups(VesselDbContext db)
    {
        private readonly Dictionary<Guid, Account> accounts = [];
        private readonly Dictionary<Guid, Play?> plays = [];

        public async Task<Account> AccountAsync(Guid id, CancellationToken ct)
        {
            if (!accounts.TryGetValue(id, out var account))
                accounts[id] = account = await db.Accounts.SingleAsync(a => a.Id == id, ct);
            return account;
        }

        public async Task<Play?> PlayAsync(Guid id, CancellationToken ct)
        {
            if (!plays.TryGetValue(id, out var play))
                plays[id] = play = await db.Plays.SingleOrDefaultAsync(p => p.Id == id, ct);
            return play;
        }

        public async Task LoadPlaysAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var missing = ids.Where(id => !plays.ContainsKey(id)).Distinct().ToArray();
            if (missing.Length == 0) return;
            var found = await db.Plays.Where(p => missing.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            foreach (var id in missing) plays[id] = found.GetValueOrDefault(id);
        }
    }

    private string Timeliness(DateTimeOffset occurred, DateTimeOffset detected) =>
        settings.Value.Timeliness(occurred, detected);
}
