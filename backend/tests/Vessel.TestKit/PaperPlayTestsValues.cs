using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public partial class PaperPlayTestsValues
{
    internal static Account PaperAccount(Guid owner, Guid portfolio)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Hypotheses", "paper", 4000);
        account.Configure(portfolio, null, null);
        return account;
    }
}
