using Vessel.Domain.Common;
using Vessel.Domain.Workspace;

namespace Vessel.Domain.Plays;

public enum OrderLinkRole { Entry, Stop, Target, Exit }
/// <summary>Linked counts toward the Play; Suggested waits for the owner; Dismissed is never proposed again.</summary>
/// <summary>
/// Where an order stands with a Play. <c>Dismissed</c> declines one level; <c>Rejected</c> means the owner said the order
/// is not the Play's at all (phase 1, 1.6d), so automatic matching never offers it to that Play again, at any level.
/// </summary>
public enum OrderLinkState { Linked, Suggested, Dismissed, Rejected }
public enum OrderLinkSource { Automatic, Owner }

/// <summary>
/// Ties a venue order to one level of a Play: an entry, one of its stops or targets, or an unplanned exit.
/// Fills reach a Play only through linked orders.
/// </summary>
public sealed class PlayOrderLink : IOwned
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid PlayId { get; private set; }
    public Guid AccountId { get; private set; }
    /// <summary>The internal ID of the linked <see cref="ImportedOrder"/>; immutable.</summary>
    public Guid ImportedOrderId { get; private set; }
    public OrderLinkRole Role { get; private set; }
    /// <summary>Plan entry ID; null for an unplanned exit.</summary>
    public string? EntryId { get; private set; }
    /// <summary>Stop or target ID within the entry for stop and target links; null for entries and exits.</summary>
    public string? LevelId { get; private set; }
    public OrderLinkState State { get; private set; }
    public OrderLinkSource Source { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    private PlayOrderLink() { }

    public PlayOrderLink(Play play, ImportedOrder order, OrderLinkRole role, string? entryId, string? levelId,
        OrderLinkState state, OrderLinkSource source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(order);
        if (order.OwnerId != play.OwnerId || order.AccountId != play.AccountId
            || !string.Equals(order.ContractId, play.ContractId, StringComparison.Ordinal))
            throw new DomainRuleException("The order belongs to another account or instrument than the play.");
        if ((role == OrderLinkRole.Exit) != (entryId is null) || (role is OrderLinkRole.Stop or OrderLinkRole.Target) != (levelId is not null))
            throw new ArgumentException("The level does not fit the link role.");
        Id = Guid.NewGuid();
        OwnerId = play.OwnerId;
        PlayId = play.Id;
        AccountId = play.AccountId;
        ImportedOrderId = order.Id;
        Role = role;
        EntryId = entryId;
        LevelId = levelId;
        State = state;
        Source = source;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>Identifies the level within the Play, e.g. <c>target|entry-1|t-2</c>.</summary>
    public string LevelKey => Key(Role, EntryId, LevelId);

    public static string Key(OrderLinkRole role, string? entryId, string? levelId) => role switch
    {
        OrderLinkRole.Exit => "exit",
        OrderLinkRole.Entry => $"entry|{entryId}",
        _ => $"{role.ToString().ToLowerInvariant()}|{entryId}|{levelId}"
    };

    /// <summary>A suggestion that became the only candidate, e.g. after the owner declined the others.</summary>
    public void LinkAutomatically(DateTimeOffset now) { State = OrderLinkState.Linked; Source = OrderLinkSource.Automatic; UpdatedAtUtc = now; }
    public void Confirm(DateTimeOffset now) { State = OrderLinkState.Linked; Source = OrderLinkSource.Owner; UpdatedAtUtc = now; }
    public void Dismiss(DateTimeOffset now) { State = OrderLinkState.Dismissed; Source = OrderLinkSource.Owner; UpdatedAtUtc = now; }
}
