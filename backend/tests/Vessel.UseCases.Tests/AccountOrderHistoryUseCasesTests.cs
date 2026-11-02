using Vessel.Domain.Accounts;

namespace Vessel.Tests;

/// <summary>
/// Order-history retention on the owner's account Refresh, through the production monitor path: the public read
/// commits first, then the optional order read is retained without linking orders or changing Plays.
/// </summary>
public sealed class AccountOrderHistoryUseCasesTests
{

    [Fact]
    public void Order_notice_replacement_keeps_latest_public_coverage_and_failure_state_without_growth()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "History");
        account.RecordSync(AccountHistoryOrders.At, "Previous public coverage.");
        account.RecordOrderHistoryNotice("Previous order coverage.");
        account.RecordSync(AccountHistoryOrders.At.AddMinutes(1), "Latest public coverage.");
        Assert.Contains("Previous order coverage.", account.HistoryNotice);
        account.RecordSyncFailure();
        var state = (account.SyncStatus, account.LastSyncError, account.LastSyncedAtUtc, account.SettingsRevision);
        account.RecordOrderHistoryNotice("Latest order coverage.");
        var expected = account.HistoryNotice;
        for (var refresh = 0; refresh < 5; refresh++)
        {
            account.RecordOrderHistoryNotice("Latest order coverage.");
            Assert.Equal(expected, account.HistoryNotice);
            Assert.Equal(state, (account.SyncStatus, account.LastSyncError, account.LastSyncedAtUtc, account.SettingsRevision));
        }
        Assert.Contains("Latest public coverage.", account.HistoryNotice);
        Assert.DoesNotContain("Previous", account.HistoryNotice);
        account.RecordOrderHistoryNotice(null);
        Assert.Equal("Latest public coverage.", account.HistoryNotice);
        Assert.Equal(state, (account.SyncStatus, account.LastSyncError, account.LastSyncedAtUtc, account.SettingsRevision));
    }
}
