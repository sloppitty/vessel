using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class AccountManagementTests
{
    [Fact]
    public void Domain_settings_preserve_source_and_sync_metadata()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Before");
        account.Configure(Guid.NewGuid(), "0x1111111111111111111111111111111111111111", null);
        var observed = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        account.RecordSync(observed, "Bounded history");
        Assert.True(account.IsEnabled);
        account.UpdateSettings("  Renamed  ", null, false);
        Assert.Equal("Renamed", account.Name); Assert.Null(account.PortfolioId); Assert.False(account.IsEnabled);
        Assert.Equal("hyperliquid", account.VenueId); Assert.Equal("0x1111111111111111111111111111111111111111", account.Address);
        Assert.Equal(observed, account.LastSyncedAtUtc); Assert.Equal("synced", account.SyncStatus); Assert.Equal("Bounded history", account.HistoryNotice);
        account.UpdateSettings("Renamed", Guid.NewGuid(), true); Assert.True(account.IsEnabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Domain_rejects_empty_names(string? name)
    {
        var owner = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), owner, "manual", "Account");
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Portfolio");
        Assert.ThrowsAny<ArgumentException>(() => account.UpdateSettings(name!, null, false));
        Assert.ThrowsAny<ArgumentException>(() => portfolio.Rename(name!));
        Assert.True(account.IsEnabled); Assert.Equal("Account", account.Name); Assert.Equal("Portfolio", portfolio.Name);
    }

    [Fact]
    public void Domain_rejects_long_names_and_empty_portfolio_ID_without_changes()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Account");
        Assert.Throws<DomainValidationException>(() => account.UpdateSettings(new string('x', 201), null, false));
        Assert.Throws<DomainValidationException>(() => account.UpdateSettings("Name", Guid.Empty, false));
        Assert.True(account.IsEnabled); Assert.Null(account.PortfolioId);
        var portfolio = new Portfolio(Guid.NewGuid(), account.OwnerId, "Portfolio");
        Assert.Throws<DomainValidationException>(() => portfolio.Rename(new string('x', 201)));
        portfolio.Rename(" Trimmed "); Assert.Equal("Trimmed", portfolio.Name);
    }
}
