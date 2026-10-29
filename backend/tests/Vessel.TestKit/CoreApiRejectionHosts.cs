
namespace Vessel.Tests;

/// <summary>
/// Hosts shared by CoreApiTests' rejection matrices. <see cref="Bare"/> has no database or store, so a route
/// that reached persistence before authentication would answer 503, not 401. <see cref="Rejecting"/> has an
/// in-memory store with one portfolio; the cases that use it are refused before any write and assert so.
/// </summary>
public sealed class CoreApiRejectionHosts : IAsyncDisposable
{
    internal CoreApiFactory Bare { get; } = new(Guid.NewGuid());
    internal MemoryWorkspaceStore Store { get; }
    internal Vessel.Domain.Workspace.Portfolio Portfolio { get; }
    internal CoreApiFactory Rejecting { get; }

    public CoreApiRejectionHosts()
    {
        var owner = Guid.NewGuid();
        Store = new MemoryWorkspaceStore(owner);
        Portfolio = new Vessel.Domain.Workspace.Portfolio(Guid.NewGuid(), owner, "Trading");
        Store.Portfolios.Add(Portfolio);
        Rejecting = new CoreApiFactory(owner, Store);
    }

    public async ValueTask DisposeAsync()
    {
        await Bare.DisposeAsync();
        await Rejecting.DisposeAsync();
    }
}
