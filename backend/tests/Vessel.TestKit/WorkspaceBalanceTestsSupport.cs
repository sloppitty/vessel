using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class WorkspaceBalanceTestsSupport
{
    internal readonly Guid owner = Guid.NewGuid();
    internal readonly MemoryWorkspaceStore store;
    internal readonly WorkspaceService service;

    public WorkspaceBalanceTestsSupport()
    {
        store = new(owner);
        var nonWallet = new VenueDescriptor("public-perps", "Public perps", "read-only", VenueSources.EvmAddress,
            VenueCapabilities.None with { Sync = true });
        service = new(store, new CoreOwner(owner), new VenueRegistry([VenueDescriptor.Manual, Vessel.Infrastructure.Venues.Hyperliquid.HyperliquidPerpetualReader.Descriptor, nonWallet],
                [], [], [], [], [], [], []), TestUnitOfWork.None);
    }

    internal Account Add(Guid? portfolio, string venue, decimal? equity, decimal? withdrawable = null,
        string? mode = null, decimal? walletTotal = null, decimal? walletAvailable = null, bool enabled = true, decimal? availableMargin = null)
    {
        var account = new Account(Guid.NewGuid(), owner, venue, "Fixture account");
        account.Configure(portfolio, null, venue == "manual" ? equity : null);
        if (!enabled) account.UpdateSettings(account.Name, portfolio, false);
        store.Accounts.Add(account);
        if (venue != "manual")
            store.Snapshots.Add(new AccountSnapshot
            {
                OwnerId = owner, AccountId = account.Id, ObservedAtUtc = DateTimeOffset.UtcNow, ValueScope = "fixture",
                AccountValueUsd = equity, WithdrawableUsd = withdrawable, AvailableMarginUsd = availableMargin, AccountMode = mode,
                StablecoinsObservedAtUtc = walletTotal.HasValue ? DateTimeOffset.UtcNow : null,
                Stablecoins = walletTotal is { } total
                    ? [new() { Total = total, Held = total - (walletAvailable ?? total), Available = walletAvailable ?? total }]
                    : [],
            });
        return account;
    }
}
