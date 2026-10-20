using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;

namespace Vessel.Application.Paper;

public sealed record PaperSettingsDto(bool Enabled, int CheckIntervalMinutes);
public sealed record PaperExecutionDto(Guid Id, string EntryId, string? LevelId, string Role, string Price, string Quantity,
    string PnlUsd, DateTimeOffset OccurredAtUtc, DateTimeOffset CandleOpenAtUtc, DateTimeOffset CandleCloseAtUtc);
public sealed record PaperPlayState(Guid PlayId, string Status, DateTimeOffset? CheckedAtUtc, DateTimeOffset? NextCheckAtUtc,
    string EnteredQuantity, string ExitedQuantity, string OpenQuantity, string RealizedPnlUsd, IReadOnlyList<string> Issues,
    IReadOnlyList<PaperExecutionDto> Executions);
public sealed record PaperExecutionProof(DateTimeOffset? FirstEntryAtUtc, int? FirstEntryPlanVersion, decimal EnteredQuantity,
    decimal ExitedQuantity, decimal OpenQuantity, decimal RealizedPnlUsd, bool HasIssues, IReadOnlySet<string> ExecutedEntryIds);

/// <summary>Lifecycle hooks join the caller's existing account transaction; they never create imported facts.</summary>
public interface IPaperTradingService
{
    Task ArmAsync(Play play, CancellationToken ct);
    Task ActivateRevisionAsync(Play play, CancellationToken ct);
    Task SuspendAsync(Play play, CancellationToken ct);
    Task SuspendAccountAsync(Guid accountId, CancellationToken ct);
    Task<PaperExecutionProof> GetExecutionProofAsync(Guid playId, CancellationToken ct);
}

public sealed record PaperDuePlay(Guid OwnerId, Guid AccountId, Guid PlayId, string ContractId, long Version,
    DateTimeOffset FromUtc, DateTimeOffset? CheckpointUtc, DateTimeOffset? NextCheckAtUtc = null);
/// <summary>The complete active account set, not a page of independently spendable Play windows.</summary>
public sealed record PaperAccountCandidate(Guid OwnerId, Guid AccountId, IReadOnlyList<PaperDuePlay> Plays);
public sealed record PaperCapital(decimal CashUsd, decimal OpenMarginUsd, decimal ReservedUsd, bool Unresolved);

public interface IPaperStore
{
    Guid OwnerId { get; }
    void RequireActive();
    Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct);
    Task<T> WithSettingsLockAsync<T>(Func<Task<T>> action, CancellationToken ct);
    Task<PaperSettings?> SettingsAsync(CancellationToken ct);
    Task<Account?> AccountAsync(Guid id, CancellationToken ct);
    Task<Play?> PlayAsync(Guid id, CancellationToken ct);
    Task<PaperSimulation?> SimulationAsync(Guid playId, CancellationToken ct);
    Task<List<PaperSimulation>> AccountSimulationsAsync(Guid accountId, CancellationToken ct);
    Task<List<PaperSimulation>> ActiveSimulationsAsync(CancellationToken ct);
    Task<List<PaperExecution>> ExecutionsAsync(Guid playId, CancellationToken ct);
    Task<List<PaperPlanActivation>> ActivationsAsync(Guid playId, CancellationToken ct);
    Task<PaperReservation?> ReservationAsync(Guid playId, CancellationToken ct);
    Task<PaperCapital> CapitalAsync(Guid accountId, Guid exceptReservationPlayId, bool includeOwnIssues, CancellationToken ct);
    Task<PaperCapital> TimelineCapitalAsync(Guid accountId, IReadOnlyCollection<Guid> recoveringPlayIds, CancellationToken ct);
    Task<bool> HasExecutionsAfterAsync(Guid accountId, DateTimeOffset frontier, CancellationToken ct);
    Task<List<PaperDuePlay>> AccountCandidatesAsync(Guid accountId, Guid? after, int limit, CancellationToken ct);
    Task DeferAccountAsync(Guid accountId, string issuesJson, DateTimeOffset now, CancellationToken ct);
    Task<bool> HasActiveAsync(CancellationToken ct);
    Task<List<PaperDuePlay>> DueAsync(DateTimeOffset now, int limit, CancellationToken ct);
    void Add(PaperSettings value);
    void Add(PaperSimulation value);
    void Add(PaperPlanActivation value);
    void Add(PaperReservation value);
    void Add(PaperExecution value);
    void Add(PaperLedgerEntry value);
    void Add(PlayStatusChange value);
}
