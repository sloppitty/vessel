using Vessel.Application.Workspace;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Application.Ownership;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays;

public sealed class DiaryService(IPlayStore plays, IDiaryStore diary, IPlayExecutionStore executions,
    IVenueRegistry venues, TimeProvider time, IUnitOfWork unitOfWork)
{
    public const int MaxManualRecords = 200, MaxManualHistory = 1000;
    public const int MaxReasons = 100;
    public const string ManualNotice = "Owner-entered manual facts and chronological average-cost USD estimates only. " +
        "Linear USD-quoted contracts only, one base unit per recorded quantity; inverse contracts, other quotes and implicit multipliers are unsupported. " +
        "Not venue imports, Activity operations or reported account balances. Unknown costs are not zero. " +
        "Execution times must use whole microseconds; finer precision is rejected, never rounded. " +
        "Derived partial average-cost allocations use rounded decimal precision; residual cost carries to the final close. " +
        "Net results subtract known execution fees in listed USD stablecoins; other fee currencies leave net unknown. " +
        NominalStablecoins.Caveat + " Funding and slippage are not added. " +
        "At most 200 records and 1000 history revisions per Play; further writes are rejected, never silently omitted.";
    public const string RetrospectiveNotice = "Owner-confirmed completed imported operation, not a contemporaneous thesis or plan. " +
        "Whole-order associations only. No inferred leverage or initial-risk R; excluded from the sizing ladder. " +
        "Return is net result divided by executed entry notional, not return on margin. Retained history may be incomplete.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ManualExecutionsDto> ManualAsync(Guid playId, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => plays.WithPlayLockAsync(playId, async () =>
        {
            var play = await RequireManual(playId, ct);
            return await ManualResult(play, await diary.ManualAsync(playId, ct), ct);
        }, ct), ct);

    public Task<ManualExecutionsDto> RecordAsync(Guid playId, Guid? executionId, ManualExecutionRequest request, CancellationToken ct) =>
        Idempotent(request.RequestId, new { playId, executionId, request }, async () =>
            await plays.WithPlayLockAsync(playId, async () =>
            {
                var play = await RequireManual(playId, ct);
                if (play.Version != request.ExpectedVersion) throw new ProblemException(409, "This Play changed. Reload before recording facts.");
                if (executionId is null ? play.Status is not (PlayStatus.Planned or PlayStatus.Open) :
                    play.Status is not (PlayStatus.Open or PlayStatus.Closed))
                    throw new ProblemException(409, "Record facts on Planned/Open Plays; corrections require Open/Closed Plays.");
                var rows = await diary.ManualAsync(playId, ct);
                if (executionId is null && rows.Count >= MaxManualRecords ||
                    (await diary.ManualHistoryAsync(playId, ct)).Count >= MaxManualHistory)
                    throw new ProblemException(409, "This manual ledger reached its explicit record/history limit.");
                var previous = executionId is { } id ? rows.SingleOrDefault(r => r.Id == id)
                    ?? throw new ProblemException(404, "Manual execution not found.") : null;
                Validate(request, play, executionId is not null);
                var now = time.GetUtcNow();
                var row = previous is null ? new ManualExecution
                {
                    Id = Guid.NewGuid(), OwnerId = play.OwnerId, PlayId = play.Id,
                    Role = request.Role, EntryId = request.EntryId, OccurredAtUtc = request.OccurredAtUtc,
                    Side = request.Side, Quantity = request.Quantity, Price = request.Price, Fee = request.Fee,
                    FeeCurrency = request.FeeCurrency, Source = request.Source, CreatedAtUtc = now, UpdatedAtUtc = now
                } : Corrected(play, previous, request, now);
                return await ApplyAsync(play, rows, [(previous, row)], request.Reason, null, now, ct);
            }, ct), ct);

    /// <summary>
    /// Corrects several existing executions with one reason, all or nothing (phase 1, 1.3b, decision 5). Each row is
    /// validated as a single correction is; only the final ledger is checked as a whole, so a closed 1/1 ledger can
    /// become 2/2 in one step. It never adds, deletes or reopens rows, and each row's history shares one batch ID.
    /// </summary>
    public Task<ManualExecutionsDto> CorrectBatchAsync(Guid playId, ManualCorrectionBatchRequest request, CancellationToken ct) =>
        Idempotent(request.RequestId, new { playId, batch = request }, async () =>
            await plays.WithPlayLockAsync(playId, async () =>
            {
                var play = await RequireManual(playId, ct);
                if (play.Version != request.ExpectedVersion) throw new ProblemException(409, "This Play changed. Reload before recording facts.");
                if (play.Status is not (PlayStatus.Open or PlayStatus.Closed))
                    throw new ProblemException(409, "Corrections require Open/Closed Plays.");
                if (request.Corrections is not { Count: > 0 } corrections || corrections.Count > MaxManualRecords)
                    throw new ProblemException(400, $"A correction batch changes 1 to {MaxManualRecords} executions.");
                if (corrections.Any(c => c is null) || corrections.Select(c => c.ExecutionId).Distinct().Count() != corrections.Count)
                    throw new ProblemException(400, "A correction batch changes each execution once.");
                var rows = await diary.ManualAsync(playId, ct);
                if ((await diary.ManualHistoryAsync(playId, ct)).Count + corrections.Count > MaxManualHistory)
                    throw new ProblemException(409, "This manual ledger reached its explicit record/history limit.");
                var now = time.GetUtcNow();
                var changes = corrections.Select(c =>
                {
                    var previous = rows.SingleOrDefault(r => r.Id == c.ExecutionId) ?? throw new ProblemException(404, "Manual execution not found.");
                    var single = new ManualExecutionRequest(request.RequestId, request.ExpectedVersion, c.Role, c.EntryId, c.OccurredAtUtc,
                        c.Side, c.Quantity, c.Price, c.Fee, c.FeeCurrency, c.Source, request.Reason);
                    Validate(single, play, correction: true);
                    return ((ManualExecution?)previous, Corrected(play, previous, single, now));
                }).ToList();
                return await ApplyAsync(play, rows, changes, request.Reason, Guid.NewGuid(), now, ct);
            }, ct), ct);

    /// <summary>A correction of an existing row: role, entry and side stay, and a Closed ledger keeps its source label.</summary>
    private static ManualExecution Corrected(Play play, ManualExecution previous, ManualExecutionRequest request, DateTimeOffset now)
    {
        if (previous.Role != request.Role || previous.EntryId != request.EntryId || previous.Side != request.Side)
            throw new ProblemException(409, "A correction keeps execution role, entry and side.");
        if (play.Status == PlayStatus.Closed && previous.Source != request.Source)
            throw new ProblemException(409, "Closed corrections keep the source label; correct amounts, prices, costs or times.");
        return new ManualExecution
        {
            Id = previous.Id, OwnerId = play.OwnerId, PlayId = play.Id,
            Role = request.Role, EntryId = request.EntryId, OccurredAtUtc = request.OccurredAtUtc,
            Side = request.Side, Quantity = request.Quantity, Price = request.Price, Fee = request.Fee,
            FeeCurrency = request.FeeCurrency, Source = request.Source, CreatedAtUtc = previous.CreatedAtUtc, UpdatedAtUtc = now
        };
    }

    /// <summary>
    /// Checks the resulting ledger as a whole, then writes the rows, one history entry each (sharing
    /// <paramref name="batchId"/> when given), and the status the ledger reaches. Nothing is written when a check fails.
    /// </summary>
    private async Task<ManualExecutionsDto> ApplyAsync(Play play, List<ManualExecution> rows,
        IReadOnlyList<(ManualExecution? Previous, ManualExecution Row)> changes, string? reason, Guid? batchId, DateTimeOffset now, CancellationToken ct)
    {
        var replaced = changes.Select(c => c.Row.Id).ToHashSet();
        var resulting = rows.Where(r => !replaced.Contains(r.Id)).Concat(changes.Select(c => c.Row)).ToList();
        var ledger = ManualLedger.Calculate(play, resulting);
        if (play.Status == PlayStatus.Closed && ledger.ClosedAt is null)
            throw new ProblemException(409, "A correction cannot reopen a Closed Play.");
        if (ledger.ClosedAt is { } projectedClose)
        {
            var facts = new ClosedPlayFacts(play, await plays.RevisionsAsync(play.Id, ct),
                ledger.Projection, ManualSource: true, ManualFeesComplete: ledger.FeesComplete);
            PlayOutcomeCalculator.Outcome(facts, out var problem, projectedClose);
            if (problem is not null) throw new ProblemException(400, problem);
        }
        foreach (var (previous, row) in changes)
        {
            var before = previous is null ? null : Dto(previous);
            if (previous is null) diary.Add(row);
            else
            {
                previous.OccurredAtUtc = row.OccurredAtUtc; previous.Quantity = row.Quantity; previous.Price = row.Price;
                previous.Fee = row.Fee; previous.FeeCurrency = row.FeeCurrency; previous.Source = row.Source; previous.UpdatedAtUtc = now;
            }
            diary.Add(new ManualExecutionHistory
            {
                Id = Guid.NewGuid(), OwnerId = play.OwnerId, PlayId = play.Id, ExecutionId = row.Id,
                Reason = previous is null ? null : reason!.Trim(), OccurredAtUtc = now, BatchId = batchId,
                Before = before is null ? null : JsonSerializer.Serialize(before, Json), After = JsonSerializer.Serialize(Dto(row), Json)
            });
        }
        if (play.Status == PlayStatus.Planned && ledger.Entered > 0) plays.Add(play.RecordManualOpen(now));
        if (play.Status == PlayStatus.Open && ledger.ClosedAt is { } closedAt) plays.Add(play.RecordManualClose(closedAt, now));
        else play.RecordManualCorrection(ledger.ClosedAt, now);
        await plays.SaveAsync(ct);
        return await ManualResult(play, resulting, ct);
    }

    private async Task<Play> RequireManual(Guid id, CancellationToken ct)
    {
        var play = await plays.FindAsync(id, ct) ?? throw new ProblemException(404, "Play not found.");
        var account = await plays.AccountAsync(play.AccountId, ct) ?? throw new ProblemException(404, "Account not found.");
        if (!account.IsEnabled) throw new ProblemException(409, "Enable the account before using manual execution facts.");
        if (venues.Descriptor(account.VenueId)?.Capabilities.ManualExecutions != true || play.Origin != PlayOrigin.Planned)
            throw new ProblemException(409, "This account does not support owner-entered manual execution facts.");
        return play;
    }

    private void Validate(ManualExecutionRequest request, Play play, bool correction)
    {
        var plan = PlayDocuments.Read(play.Plan);
        if (request.Role is not ("entry" or "exit") || request.Side is not (ExecutionFacts.Buy or ExecutionFacts.Sell) ||
            request.Role == "entry" && !plan.Entries.Any(e => e.Id == request.EntryId) ||
            request.Role == "exit" && request.EntryId is not null)
            throw new ProblemException(400, "Choose entry with a saved entry ID, or exit without an entry ID, and normalized buy/sell.");
        if (request.OccurredAtUtc.Offset != TimeSpan.Zero || request.OccurredAtUtc > time.GetUtcNow() ||
            request.OccurredAtUtc == default)
            throw new ProblemException(400, "Execution time must be a nonfuture UTC timestamp.");
        if (request.OccurredAtUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw new ProblemException(400, "Execution time must use whole microseconds; finer precision is not supported.");
        ManualLedger.Number(request.Quantity, true); ManualLedger.Number(request.Price, true);
        if ((request.Fee is null) != (request.FeeCurrency is null) ||
            request.FeeCurrency is { } currency && (currency.Length is < 1 or > 32 || currency != currency.Trim() ||
                currency.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new ProblemException(400, "Supply both fee and currency, or leave both null for unknown costs.");
        if (request.Fee is not null) ManualLedger.Number(request.Fee, false);
        if (request.Source is null || request.Source.Length is < 1 or > 100 || request.Source != request.Source.Trim() ||
            request.Source.Any(char.IsControl))
            throw new ProblemException(400, "Source must be a short plain label, up to 100 characters.");
        if (correction && (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000 || request.Reason.Any(char.IsControl)))
            throw new ProblemException(400, "A correction requires a plain reason, up to 2000 characters.");
    }

    private async Task<ManualExecutionsDto> ManualResult(Play play, List<ManualExecution> rows, CancellationToken ct)
    {
        var ledger = ManualLedger.Calculate(play, rows);
        var history = await diary.ManualHistoryAsync(play.Id, ct);
        if (rows.Count > MaxManualRecords || history.Count > MaxManualHistory)
            throw new ProblemException(409, "Stored ledger exceeds its explicit response limits.");
        var revisions = await plays.RevisionsAsync(play.Id, ct);
        var entries = PlayDocuments.Read(play.Plan).Entries.Select(entry =>
        {
            var own = rows.Where(r => r.Role == "entry" && r.EntryId == entry.Id)
                .Select(r => (r.OccurredAtUtc, Quantity: ManualLedger.Number(r.Quantity, true), Price: ManualLedger.Number(r.Price, true))).ToList();
            var quantity = own.Aggregate(default(ExactAmount), (sum, r) => sum + ExactAmount.From(r.Quantity));
            var planned = PlayReceipts.Planned(play, revisions, entry.Id, own.Count == 0 ? null : own.Min(r => r.OccurredAtUtc));
            return new EntryProgressDto(entry.Id, quantity.ToString(), quantity.Units.Sign > 0
                ? ExactAmount.Average(own.Aggregate(BigInteger.Zero, (sum, r) => sum + ExactAmount.Product(r.Price, r.Quantity)), quantity) : null, 0)
            {
                PlannedPrice = planned.Price, PlannedQuantity = planned.Quantity, PlanRevision = planned.Revision,
            };
        }).ToList();
        return new(PlayService.ToDto(play), rows.OrderBy(r => r.OccurredAtUtc).ThenBy(r => r.Id).Select(Dto).ToList(),
            history.Select(h => new ManualHistoryDto(h.Id, h.ExecutionId, h.Reason, h.OccurredAtUtc,
                h.Before is null ? null : JsonSerializer.Deserialize<ManualExecutionDto>(h.Before, Json),
                JsonSerializer.Deserialize<ManualExecutionDto>(h.After, Json)!) { BatchId = h.BatchId }).ToList(),
            new(ManualLedger.Text(ledger.Entered), ManualLedger.Text(ledger.Exited), ManualLedger.Text(ledger.Open),
                ManualLedger.Text(ledger.Gross), ledger.Net is { } net ? ManualLedger.Text(net) : null, ledger.FeesComplete), ManualNotice)
        {
            Entries = entries,
            Fees = rows.Where(r => r.Fee is not null && r.FeeCurrency is not null).GroupBy(r => r.FeeCurrency!, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new FeeTotalDto(g.Key,
                    g.Aggregate(default(ExactAmount), (sum, r) => sum + ExactAmount.From(ManualLedger.Number(r.Fee!, false))).ToString())).ToList(),
            UnknownFees = rows.Count(r => r.Fee is null),
            Receipt = PlayReceipts.Build(new ClosedPlayFacts(play, revisions, ledger.Projection, [], true, ledger.FeesComplete), time.GetUtcNow()),
        };
    }
    private static ManualExecutionDto Dto(ManualExecution row) => new(row.Id, row.Role, row.EntryId, row.OccurredAtUtc,
        row.Side, row.Quantity, row.Price, row.Fee, row.FeeCurrency, row.Source, row.CreatedAtUtc, row.UpdatedAtUtc);

    public Task<RetrospectivePreviewDto> PreviewAsync(Guid accountId, string operationId, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => executions.WithAccountLockAsync(accountId, () => PreviewLocked(accountId, operationId, ct), ct), ct);

    private async Task<RetrospectivePreviewDto> PreviewLocked(Guid accountId, string operationId, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(operationId) || operationId.Length > 200)
            throw new ProblemException(400, "Choose a valid account and retained operation.");
        var account = await plays.AccountAsync(accountId, ct) ?? throw new ProblemException(404, "Account not found.");
        if (!account.IsEnabled) throw new ProblemException(409, "Enable the account before reviewing imported operations.");
        var facts = await diary.ActivityAsync(accountId, ct);
        var operation = OperationAggregator.Aggregate(facts.Fills, facts.FillPlayIds, facts.Snapshots, facts.FundingPayments).SingleOrDefault(o => o.Id == operationId)
            ?? throw new ProblemException(404, "Retained operation not found. Reload Activity.");
        var bounded = operation with { Fills = operation.Fills.Take(ActivityService.MaxFillsPerOperation).ToList(),
            OmittedFillCount = operation.OmittedFillCount + Math.Max(0, operation.Fills.Count - ActivityService.MaxFillsPerOperation) };
        if (operation.FillCount > ActivityService.MaxFillsPerOperation || operation.Fills.Count > ActivityService.MaxFillsPerOperation ||
            operation.OmittedFillCount > 0)
            return new(false, [$"Constituent rows are omitted or exceed the {ActivityService.MaxFillsPerOperation}-fill preview limit; whole-order attribution is unsafe."],
                Hash(new { account.Id, account.OwnerId, account.IsEnabled, account.SettingsRevision, operation = bounded }),
                bounded, RetrospectiveNotice);

        var ids = operation.Fills.Select(f => f.Fill.Id).ToHashSet();
        var operationOrders = operation.Fills.ToLookup(f => Key(f.Fill));
        // Operations are per account and contract, so every order key is on the operation's contract: the same venue ID
        // on another contract is another order, and neither its fills nor its order row nor its links are read here.
        var orderKeys = operationOrders.Select(g => g.Key).ToHashSet();
        // Include every retained fill of the involved orders, also those outside the selected operation.
        var relevant = facts.Fills.Where(f => f.ContractId == operation.ContractId && orderKeys.Contains(OrderKey.Of(f))).ToList();
        var fillsByOrder = relevant.ToLookup(OrderKey.Of);
        var own = relevant.Where(f => ids.Contains(f.Id)).ToDictionary(f => f.Id);
        // Only the operation's orders and their links are read; retained fills above stay whole-order evidence.
        var orders = (await diary.OrdersAsync(accountId, orderKeys, ct)).Where(o => orderKeys.Contains(OrderKey.Of(o)))
            .ToDictionary(OrderKey.Of);
        var orderIds = orders.Values.Select(o => o.Id).ToHashSet();
        var links = (await diary.LinksAsync(accountId, orderIds, ct)).Where(l => orderIds.Contains(l.ImportedOrderId)).ToList();
        var retrospectivePlan = new PlayPlanDocument(operation.Direction ?? "long", "margin", "", "0", null, [],
            new PlanNotes("", "", "", ""));
        List<string> reasons = [];
        if (operation.Status != "closed" || operation.Direction is not ("long" or "short"))
            reasons.Add("Only a completed single-direction operation can become a retrospective Play.");
        if (operation.PlayAssociation != "unassigned" || links.Any(l => l.State == OrderLinkState.Linked))
            reasons.Add("This operation contains existing or mixed Play associations.");
        reasons.AddRange(operation.Coverage.Where(r => !r.StartsWith("Net result is unknown:", StringComparison.Ordinal)));
        if (operation.FillCount != own.Count || operation.Fills.Any(f =>
            !own.TryGetValue(f.Fill.Id, out var retained) || f.Quantity != ManualLedger.Text(retained.Quantity)))
            reasons.Add("Constituent rows are omitted or split/flip allocations; whole-order attribution is unsafe.");
        if (own.Values.Any(f => f.PositionEffect is not (ExecutionFacts.Open or ExecutionFacts.Close)))
            reasons.Add("Some fills have unknown or flip position effects.");
        if (own.Values.Any(f => f.Quantity <= 0 || f.Price <= 0 || f.OccurredAtUtc > time.GetUtcNow()))
            reasons.Add("Some retained fills have nonpositive amounts/prices or future timestamps.");
        foreach (var key in orderKeys.OrderBy(k => k.ExternalOrderId, StringComparer.Ordinal).ThenBy(k => k.ContractId, StringComparer.Ordinal))
        {
            var orderId = key.ExternalOrderId;
            var fills = fillsByOrder[key].ToList();
            var order = orders.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(orderId) || order is null)
                reasons.Add($"Order {orderId} has no retained order record; execution completeness cannot be checked.");
            else if (ExecutionIntegrity.MissingExecutions(order, fills))
                reasons.Add($"Order {orderId} is known to have missing executions.");
            else if (order.Status is "open" or "triggered")
                reasons.Add($"Order {orderId} can still execute.");
            if (fills.Any(f => !ids.Contains(f.Id)) || fills.Select(f => f.PositionEffect).Distinct().Count() != 1)
                reasons.Add($"Order {orderId} crosses operation boundaries or requires different roles.");
            if (order is not null && (order.ContractId != operation.ContractId ||
                fills.Any(f => f.AccountId != account.Id || f.OwnerId != account.OwnerId || f.ContractId != operation.ContractId ||
                    order.VenueContractId is not null && f.VenueContractId is not null && order.VenueContractId != f.VenueContractId)))
                reasons.Add($"Order {orderId} identity or role contradicts its executions.");
            var role = operationOrders[key].First().Role == "entry" ? OrderLinkRole.Entry : OrderLinkRole.Exit;
            if (order is not null && ExecutionIntegrity.RoleConflict(role, retrospectivePlan, order, fills) is { } conflict)
                reasons.Add($"Order {orderId}: {conflict}");
        }
        // An explicit, stable projection of what eligibility and creation read, never whole entities.
        var fingerprint = Hash(new { account.Id, account.OwnerId, account.IsEnabled, account.SettingsRevision, operation,
            fills = relevant.OrderBy(f => f.Id).Select(FillPrint), orders = orders.Values.OrderBy(o => o.Id).Select(OrderPrint),
            links = links.OrderBy(l => l.Id).Select(LinkPrint) });
        return new(reasons.Count == 0, BoundedReasons(reasons), fingerprint, bounded, RetrospectiveNotice);
    }

    public Task<PlayDto> CreateRetrospectiveAsync(CreateRetrospectiveRequest request, CancellationToken ct) =>
        Idempotent(request.RequestId, request, () => executions.WithAccountLockAsync(request.AccountId, async () =>
        {
            var preview = await PreviewLocked(request.AccountId, request.OperationId, ct);
            if (preview.Fingerprint != request.ExpectedFingerprint)
                throw new ProblemException(409, "The retained operation changed since preview. Reload and review it again.");
            if (!preview.Eligible) throw new ProblemException(409, string.Join(" ", preview.Reasons));
            var account = (await plays.AccountAsync(request.AccountId, ct))!;
            if (request.Title is null || request.Title.Trim().Length > Play.MaxTitleLength ||
                request.Review?.Length > Play.MaxReviewLength)
                throw new ProblemException(400, "Retrospective title or review exceeds its text limit.");
            var now = time.GetUtcNow();
            var emptyPlan = new PlayPlanDocument(preview.Operation.Direction!, "margin", "", "0", null, [],
                new PlanNotes("", "", "", ""));
            var play = Play.Retrospective(Guid.NewGuid(), account, new(account.VenueId, preview.Operation.ContractId),
                PlayDocuments.Serialize(emptyPlan), preview.Operation.ClosedAtUtc!.Value, now, request.Title);
            var review = play.Annotate(request.Title ?? "", "{}", request.Review ?? "", now);
            plays.Add(play);
            if (review is not null) plays.Add(review);
            // Eligibility proved every order is retained; links reference their internal IDs.
            var operationOrders = preview.Operation.Fills.GroupBy(f => Key(f.Fill)).ToList();
            var retained = (await diary.OrdersAsync(account.Id, operationOrders.Select(g => g.Key).ToList(), ct)).ToDictionary(OrderKey.Of);
            foreach (var order in operationOrders)
                diary.Add(new PlayOrderLink(play, retained.GetValueOrDefault(order.Key) ?? throw new ProblemException(409,
                        "The retained operation changed since preview. Reload and review it again."),
                    order.First().Role == "entry" ? OrderLinkRole.Entry : OrderLinkRole.Exit,
                    order.First().Role == "entry" ? "retrospective" : null, null, OrderLinkState.Linked, OrderLinkSource.Owner, now));
            await plays.SaveAsync(ct);
            return PlayService.ToDto(play);
        }, ct), ct);

    private Task<T> Idempotent<T>(Guid requestId, object payload, Func<Task<T>> action, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => diary.WithRequestLockAsync(requestId, async () =>
        {
            if (requestId == Guid.Empty) throw new ProblemException(400, "A nonempty requestId is required.");
            var hash = Hash(payload);
            if (await diary.RequestAsync(requestId, ct) is { } existing)
            {
                if (existing.PayloadHash != hash) throw new ProblemException(409, "This requestId was already used with a different payload.");
                var cached = JsonSerializer.Deserialize<T>(existing.Response, Json)!;
                var accountId = cached switch { ManualExecutionsDto m => m.Play.Summary.AccountId, PlayDto p => p.Summary.AccountId,
                    _ => throw new InvalidOperationException() };
                return await executions.WithAccountLockAsync(accountId, async () =>
                {
                    var account = await plays.AccountAsync(accountId, ct) ?? throw new ProblemException(404, "Account not found.");
                    if (!account.IsEnabled) throw new ProblemException(409, "Enable the account before accessing diary execution facts.");
                    return cached;
                }, ct);
            }
            var result = await action();
            var ownerId = result switch { ManualExecutionsDto m => (await plays.FindAsync(m.Play.Summary.Id, ct))!.OwnerId,
                PlayDto p => (await plays.FindAsync(p.Summary.Id, ct))!.OwnerId, _ => throw new InvalidOperationException() };
            diary.Add(new DiaryRequest { Id = requestId, OwnerId = ownerId, PayloadHash = hash, Response = JsonSerializer.Serialize(result, Json) });
            await plays.SaveAsync(ct);
            return result;
        }, ct), ct);

    /// <summary>The order of an operation fill, by the shared <see cref="OrderKey"/> rule.</summary>
    private static OrderKey Key(FillDto fill) => new(fill.AccountId, fill.ContractId, fill.OrderId);

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FillPrint(ImportedFill f) => new
    {
        f.Id, f.OwnerId, f.AccountId, f.ContractId, f.VenueContractId, f.SourceFillId, f.ExternalOrderId, f.Side, f.PositionEffect,
        Price = ExactDecimal.Format(f.Price), Quantity = ExactDecimal.Format(f.Quantity), Fee = ExactDecimal.Format(f.Fee), f.FeeToken,
        f.FeeBasis, ClosedPnlUsd = ExactDecimal.Format(f.ClosedPnlUsd), f.PnlBasis, OccurredAtUtc = Time(f.OccurredAtUtc)
    };

    private static object OrderPrint(ImportedOrder o) => new
    {
        o.Id, o.OwnerId, o.AccountId, o.ContractId, o.VenueContractId, o.ExternalOrderId, o.Side, o.OrderType,
        LimitPrice = ExactDecimal.Format(o.LimitPrice), TriggerPrice = ExactDecimal.Format(o.TriggerPrice), o.ReduceOnly, o.IsPositionTpsl,
        OriginalSize = ExactDecimal.Format(o.OriginalSize), RemainingSize = ExactDecimal.Format(o.RemainingSize),
        PlacedAtUtc = Time(o.PlacedAtUtc), o.Status, StatusAtUtc = Time(o.StatusAtUtc)
    };

    private static object LinkPrint(PlayOrderLink l) => new
    {
        l.Id, l.PlayId, l.ImportedOrderId, Role = l.Role.ToString(), l.EntryId, l.LevelId, State = l.State.ToString()
    };

    private static string Hash(object payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json))));

    public static IReadOnlyList<string> BoundedReasons(IEnumerable<string> reasons)
    {
        var all = reasons.Distinct().ToList();
        return all.Count <= MaxReasons ? all : all.Take(MaxReasons - 1)
            .Append($"{all.Count - MaxReasons + 1} additional reasons omitted from this response; this record is still excluded/ineligible.").ToList();
    }
}
