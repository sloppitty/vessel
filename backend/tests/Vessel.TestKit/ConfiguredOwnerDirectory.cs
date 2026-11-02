using Vessel.Application.Ownership;

namespace Vessel.Tests;

/// <summary>A test directory with one owner that is always active.</summary>
internal sealed class ConfiguredOwnerDirectory(Guid ownerId) : IJournalOwnerDirectory
{
    private readonly Guid owner = ownerId != Guid.Empty ? ownerId : throw new ArgumentException("Owner ID must be nonempty.", nameof(ownerId));

    public Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(ownerId == owner ? JournalOwnerStatus.Active : JournalOwnerStatus.Unknown);

    public Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        IReadOnlyList<Guid> owners = after is { } last && last.CompareTo(owner) >= 0 ? [] : [owner];
        return Task.FromResult(owners);
    }
}
