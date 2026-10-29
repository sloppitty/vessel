using Vessel.Application.MarketData;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public partial class MarketContextApiTestsSupport
{

    internal static (Guid Owner, MemoryWorkspaceStore Store, Account Account) Setup(string venue = "hyperliquid", bool foreign = false, bool disabled = false)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), foreign ? Guid.NewGuid() : owner, venue, "Private account");
        if (disabled) account.UpdateSettings(account.Name, null, false);
        store.Accounts.Add(account);
        return (owner, store, account);
    }

    internal sealed class Contexts : IMarketContextReader
    {
        public string VenueId { get; init; } = "hyperliquid";
        public int Reads; public Exception? Failure;
        public IReadOnlyList<VenueMarketContext> Result { get; set; } = [];
        public Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Failure is null ? Task.FromResult(Result) : Task.FromException<IReadOnlyList<VenueMarketContext>>(Failure);
        }
    }
}
