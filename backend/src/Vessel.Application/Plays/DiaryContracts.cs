using System.Text.Json.Serialization;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays;

public sealed record ManualExecutionRequest(
    [property: JsonRequired] Guid RequestId, [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] string Role, string? EntryId,
    [property: JsonRequired] DateTimeOffset OccurredAtUtc, [property: JsonRequired] string Side,
    [property: JsonRequired] string Quantity, [property: JsonRequired] string Price,
    string? Fee, string? FeeCurrency, [property: JsonRequired] string Source, string? Reason = null);

public sealed record ManualExecutionDto(Guid Id, string Role, string? EntryId, DateTimeOffset OccurredAtUtc,
    string Side, string Quantity, string Price, string? Fee, string? FeeCurrency, string Source,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ManualHistoryDto(Guid Id, Guid ExecutionId, string? Reason, DateTimeOffset OccurredAtUtc,
    ManualExecutionDto? Before, ManualExecutionDto After)
{
    /// <summary>Shared by the rows one correction batch changed together; null for single-row writes.</summary>
    public Guid? BatchId { get; init; }
}

/// <summary>One row of a correction batch: an existing execution's full corrected facts, as a single correction sends them.</summary>
public sealed record ManualCorrectionRow(
    [property: JsonRequired] Guid ExecutionId, [property: JsonRequired] string Role, string? EntryId,
    [property: JsonRequired] DateTimeOffset OccurredAtUtc, [property: JsonRequired] string Side,
    [property: JsonRequired] string Quantity, [property: JsonRequired] string Price,
    string? Fee, string? FeeCurrency, [property: JsonRequired] string Source);

/// <summary>
/// Several corrections with one reason, applied all or nothing (phase 1, 1.3b, decision 5). Only the final ledger is
/// checked as a whole, so a closed 1/1 ledger can become 2/2 in one step.
/// </summary>
public sealed record ManualCorrectionBatchRequest(
    [property: JsonRequired] Guid RequestId, [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] string Reason, [property: JsonRequired] IReadOnlyList<ManualCorrectionRow> Corrections);
public sealed record ManualTotalsDto(string EnteredQuantity, string ExitedQuantity, string OpenQuantity,
    string GrossResultUsd, string? NetResultUsd, bool FeesComplete);
public sealed record ManualExecutionsDto(PlayDto Play, IReadOnlyList<ManualExecutionDto> Records,
    IReadOnlyList<ManualHistoryDto> History, ManualTotalsDto Totals, string Notice)
{
    /// <summary>Each planned entry's ledger fills beside its plan, as the venue execution read gives them.</summary>
    public IReadOnlyList<EntryProgressDto> Entries { get; init; } = [];
    /// <summary>Times, averages and estimated R by Review's rules, over the ledger Review reads.</summary>
    public PlayReceiptDto? Receipt { get; init; }
    /// <summary>Known fees by currency as entered, exactly; executions with unknown costs are counted, never added as zero.</summary>
    public IReadOnlyList<FeeTotalDto> Fees { get; init; } = [];
    public int UnknownFees { get; init; }
}

public sealed record RetrospectivePreviewDto(bool Eligible, IReadOnlyList<string> Reasons, string Fingerprint,
    ActivityOperationDto Operation, string Notice);
public sealed record CreateRetrospectiveRequest(
    [property: JsonRequired] Guid RequestId, [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] string OperationId, [property: JsonRequired] string ExpectedFingerprint,
    [property: JsonRequired] string Title, string? Review = null);

// Small diary-owned records; imported facts are only read here.
public interface IDiaryStore
{
    Task<T> WithRequestLockAsync<T>(Guid requestId, Func<Task<T>> action, CancellationToken ct);
    Task<DiaryRequest?> RequestAsync(Guid requestId, CancellationToken ct);
    Task<List<ManualExecution>> ManualAsync(Guid playId, CancellationToken ct);
    Task<List<ManualExecutionHistory>> ManualHistoryAsync(Guid playId, CancellationToken ct);
    Task<ActivityFacts> ActivityAsync(Guid accountId, CancellationToken ct);
    Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct);
    Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct);
    /// <summary>Read-only orders of the account with one of the external <paramref name="orders"/> keys.</summary>
    async Task<List<ImportedOrder>> OrdersAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
        (await OrdersAsync(accountId, ct)).Where(o => orders.Contains(OrderKey.Of(o))).ToList();
    /// <summary>Read-only links of the account in any state on one of the internal <paramref name="orderIds"/>.</summary>
    async Task<List<PlayOrderLink>> LinksAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => orderIds.Contains(l.ImportedOrderId)).ToList();
    /// <summary>Of <paramref name="fillIds"/>, the fills with a current allocation between Plays (Phase 2, item 11); none by default.</summary>
    Task<IReadOnlySet<Guid>> AllocatedFillIdsAsync(IReadOnlyCollection<Guid> fillIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    void Add(ManualExecution execution);
    void Add(ManualExecutionHistory history);
    void Add(DiaryRequest request);
    void Add(PlayOrderLink link);
}
