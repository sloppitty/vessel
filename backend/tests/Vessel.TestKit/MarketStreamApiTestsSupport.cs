using Vessel.Domain.Accounts;
using Vessel.Domain.Common;

namespace Vessel.Tests;

public partial class MarketStreamApiTestsSupport
{

    internal static (Guid Owner, MemoryWorkspaceStore Store, Account Account) Setup(string venue = "hyperliquid", bool foreign = false, bool disabled = false)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), foreign ? Guid.NewGuid() : owner, venue, "Private account", TradingMode.Real);
        if (disabled) account.UpdateSettings(account.Name, null, false);
        store.Accounts.Add(account);
        return (owner, store, account);
    }
}
