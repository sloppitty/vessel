using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class InstrumentCatalogueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalogue_only_reads_metadata_without_address_refresh_or_write_side_effects(bool failure)
    {
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner) { RejectOwnerWideReads = true, RejectActivityReads = true };
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL without an address");
        account.RecordSync(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), "Retained incomplete history.");
        account.RecordSyncFailure();
        store.Accounts.Add(account);
        store.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = account.Id, AccountValueUsd = 123.45m });
        store.Fills.Add(new ImportedFill { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "ETH" });
        var before = JsonSerializer.Serialize(new { store.Accounts, store.Snapshots, store.Fills });
        var reader = new FixtureReader { Fail = failure };
        reader.Result = reader.Result with { Instruments = [new("MiXeD", 5, 40, "USDC"), new("BTC", 0, 3, "USDC")] };
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.None);
        using var caller = new CancellationTokenSource();

        if (failure)
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() => service.InstrumentsAsync(account.Id, caller.Token));
            Assert.Equal(502, error.StatusCode);
            Assert.Equal("The venue instrument read failed. Try again later.", error.Message);
        }
        else
        {
            var result = await service.InstrumentsAsync(account.Id, caller.Token);
            Assert.Equal("hyperliquid", result.VenueId);
            Assert.Equal("perpetuals", result.MarketScope);
            Assert.Equal("venue-perpetuals", result.Scope);
            Assert.Same(reader.Result.Instruments, result.Instruments);
            Assert.Equal("Venue perpetual contract metadata only. No orders, balances or execution refresh.", result.Notice);
        }
        Assert.Equal(1, reader.InstrumentReads);
        Assert.Equal(caller.Token, reader.InstrumentCancellationToken);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, store.Saves);
        Assert.Equal(before, JsonSerializer.Serialize(new { store.Accounts, store.Snapshots, store.Fills }));
    }

    [Fact]
    public async Task Manual_catalogue_is_empty_without_a_venue_read()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Manual");
        store.Accounts.Add(account);
        var reader = new FixtureReader { Fail = true };
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.None);
        var result = await service.InstrumentsAsync(account.Id, default);
        Assert.Equal("manual", result.VenueId);
        Assert.Equal("manual", result.Scope);
        Assert.Equal("perpetuals", result.MarketScope);
        Assert.Empty(result.Instruments);
        Assert.Contains("Manual catalogue", result.Notice);
        Assert.Equal(0, reader.InstrumentReads);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("foreign", 404)]
    [InlineData("disabled", 409)]
    [InlineData("disabled-manual", 409)]
    public async Task Missing_foreign_or_disabled_accounts_do_not_read_metadata(string scenario, int status)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), scenario == "foreign" ? Guid.NewGuid() : owner,
            scenario == "disabled-manual" ? "manual" : "hyperliquid", "Private account");
        if (scenario.StartsWith("disabled", StringComparison.Ordinal))
            account.UpdateSettings(account.Name, null, false);
        if (scenario != "missing") store.Accounts.Add(account);
        var reader = new FixtureReader { Fail = true };
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.None);
        var error = await Assert.ThrowsAsync<ProblemException>(() => service.InstrumentsAsync(account.Id, default));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("Private", error.Message);
        Assert.Equal(0, reader.InstrumentReads);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("venue")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Provider_failures_are_generic_502_without_changing_sync_state(string failure)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL"); store.Accounts.Add(account);
        var reader = new FixtureReader { InstrumentFailure = failure switch
        {
            "venue" => new VenueReadException("private-secret"),
            "network" => new HttpRequestException("private-secret"),
            _ => new OperationCanceledException("private-secret")
        }};
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.None);
        var error = await Assert.ThrowsAsync<ProblemException>(() => service.InstrumentsAsync(account.Id, default));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("The venue instrument read failed. Try again later.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal("not-synced", account.SyncStatus);
        Assert.Null(account.LastSyncedAtUtc);
        Assert.Null(account.LastSyncError);
        Assert.Null(account.HistoryNotice);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Missing_reader_returns_generic_502_without_metadata_read()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "unsupported-venue", "Legacy"); store.Accounts.Add(account);
        var reader = new FixtureReader();
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.None);
        var error = await Assert.ThrowsAsync<ProblemException>(() => service.InstrumentsAsync(account.Id, default));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("The venue instrument read failed. Try again later.", error.Message);
        Assert.Equal(0, reader.InstrumentReads);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_provider_failure_and_makes_no_read_or_write()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL"); store.Accounts.Add(account);
        var reader = new FixtureReader();
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.None);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstrumentsAsync(account.Id, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(0, reader.InstrumentReads);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, store.Saves);
        Assert.Equal("not-synced", account.SyncStatus);
    }
}
