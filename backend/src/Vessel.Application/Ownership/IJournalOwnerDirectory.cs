namespace Vessel.Application.Ownership;

/// <summary>How background work treats an owner. Unknown means no such user: its messages are dead-lettered.</summary>
public enum JournalOwnerStatus
{
    Unknown,
    Active,
    Suspended,
}

/// <summary>
/// Which journal owners exist and whether their background work may run, implemented by the identity module over its
/// users. Retained-data maintenance (event retention) enumerates owners from journal data instead, so suspended owners
/// keep being pruned.
/// </summary>
/// <remarks>
/// Implementations are safe to call from singleton background services and never capture a scoped DbContext.
/// Owner pages use ascending UUID order with an exclusive cursor; callers restart the scan after reaching the end.
/// </remarks>
public interface IJournalOwnerDirectory
{
    Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Active owners in ascending ID order after <paramref name="after"/> (exclusive), at most <paramref name="limit"/> (1–200).</summary>
    Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken);
}
