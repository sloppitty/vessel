using Vessel.Application.Ownership;

namespace Vessel.Tests;

/// <summary>Test-only default for <see cref="IUnitOfWork"/> (issue #114): without snapshots, a snapshot read is an ordinary unit.</summary>
public interface IUnitOfWorkDefaults : IUnitOfWork
{
    Task<T> IUnitOfWork.ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken) => ExecuteAsync(read, cancellationToken);
}
