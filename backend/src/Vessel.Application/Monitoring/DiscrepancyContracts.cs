using Vessel.Application.Plays.Execution;
using Vessel.Application.Common;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Monitoring;

public sealed record DismissMonitorDiscrepancyRequest(string? Note = null);

/// <summary>
/// Retained facts the checks need, including suggested links: Open (and Planned/Paused) Plays, terminal Plays still
/// under exit/orphan watch, with tracked records (even unverifiable), linked to a reported resting order or that ended
/// recently, their linked orders and fills, and the fills on
/// each Open Play's instrument after its earliest entry. A bounded load must report any omitted facts.
/// </summary>
/// <param name="Complete">False only when facts every check depends on were omitted.</param>
/// <param name="AssignmentLinks">Linked or suggested links, on any Play of the account including retrospective ones,
/// for the orders of the loaded fills. Separate from the links used to evaluate monitored planned Plays; null means unknown.</param>
/// <param name="AssignmentLinksComplete">True only when every assignment link of those orders was loaded.
/// A capped or unknown load cannot establish or clear an unlinked-fill discrepancy.</param>
/// <param name="TruncatedFillContracts">Instruments whose post-entry fills exceeded the safety bound. Only their
/// unlinked-fill checks are unknown.</param>
public sealed record MonitorDiscrepancyFacts(IReadOnlyList<Play> Plays, IReadOnlyList<PlayOrderLink> Links,
    IReadOnlyList<ImportedOrder> Orders, IReadOnlyList<ImportedFill> Fills, bool Complete = true,
    IReadOnlyList<PlayOrderLink>? AssignmentLinks = null, bool AssignmentLinksComplete = false,
    IReadOnlySet<string>? TruncatedFillContracts = null)
{
    /// <summary>
    /// Owner-confirmed allocations naming the loaded Plays or on the loaded fills (Phase 2, item 11, Sol round 1): the protection
    /// checks read a Play's part of them, and an allocated fill is not unlinked. The position comparison does not read them.
    /// </summary>
    public IReadOnlyList<AllocatedFill> Allocations { get; init; } = [];
}

public interface IMonitorDiscrepancyStore
{
    Task<Account?> AccountAsync(Guid accountId, CancellationToken ct);
    Task<Play?> PlayAsync(Guid playId, CancellationToken ct);
    /// <param name="recentSince">Terminal Plays that ended at or after this time are loaded, so their late fills can
    /// make an earlier position snapshot unknown.</param>
    /// <param name="restingOrders">Orders the current read reports resting, even from a partial book, by contract and
    /// external ID (a venue order ID is unique only within its contract). Terminal Plays whose linked order has one of
    /// these keys are evaluated: that is positive evidence of an orphaned entry or exit. Keys of other accounts are ignored.</param>
    Task<MonitorDiscrepancyFacts> LoadAsync(Guid accountId, DateTimeOffset recentSince, IReadOnlyCollection<OrderKey> restingOrders, CancellationToken ct);
    Task<IReadOnlyList<PlayDiscrepancy>> RecordsAsync(Guid accountId, CancellationToken ct);
    void Add(PlayDiscrepancy record);
    /// <summary>Serializes dismissal with the monitor account fence. Requires an active unit of work.</summary>
    Task LockAsync(Guid accountId, CancellationToken ct);
}
