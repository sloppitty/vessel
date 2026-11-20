using Vessel.Domain.Accounts;
using Vessel.Domain.Common;

namespace Vessel.Tests;

public partial class PaperPlayTestsValues
{
    internal static Account PaperAccount(Guid owner, Guid portfolio)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Hypotheses", TradingMode.Paper, 4000);
        account.Configure(portfolio, null, null);
        return account;
    }
}
