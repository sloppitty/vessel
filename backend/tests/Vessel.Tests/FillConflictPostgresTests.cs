using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 14c (F-12, probe V10): a repeated fill ID with changed values used to be ignored silently. The first stored
/// copy is still kept and never overwritten, and the changed field names are now recorded where Account detail reads them.
/// </summary>
public sealed class FillConflictPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private static VenueFill Fill(decimal price = 100, decimal fee = 0.05m, int minute = 1, string? venueContract = null, int? leverage = null,
        string positionEffect = "open") =>
        new("f1", "BTC", "buy", "Open Long", price, 1, fee, "USDC", 0, Start.AddMinutes(minute), "order-1", "0xhash", "", positionEffect,
            VenueContractId: venueContract, Leverage: leverage);

    private static PerpetualVenueReadResult Read(int minute, params VenueFill[] fills) =>
        new(new(Start.AddMinutes(minute), "primary-perpetual-dex", 100, 100, 1, []), [], fills, "Bounded history.");

    private static async Task SaveAsync(VesselDbContext db, Guid accountId, PerpetualVenueReadResult read)
    {
        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        await work.ExecuteAsync(() => store.WithAccountLockAsync(accountId, async locked =>
        {
            await store.SaveRefreshAsync(locked, read, default);
            return true;
        }, default), default);
    }

    [PostgresFact]
    public async Task A_changed_report_keeps_the_first_copy_and_records_only_the_changed_field_names()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        account.Configure(null, "0x" + new string('a', 40), null);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        await SaveAsync(db, account.Id, Read(2, Fill()));
        // Identical, and a venue contract or leverage newly stated where none was, and Vessel's own reading changing: no conflict.
        await SaveAsync(db, account.Id, Read(3, Fill(venueContract: "0", leverage: 5, positionEffect: "unknown")));
        Assert.Empty(await db.FillImportConflicts.ToListAsync());

        await SaveAsync(db, account.Id, Read(4, Fill(price: 101, fee: 0.06m)));
        await SaveAsync(db, account.Id, Read(5, Fill(price: 101, minute: 2)));
        var conflict = await db.FillImportConflicts.AsNoTracking().SingleAsync();
        Assert.Equal(("price,fee,time", 2, Start.AddMinutes(4), Start.AddMinutes(5)),
            (conflict.Fields, conflict.Reports, conflict.FirstSeenAtUtc, conflict.LastSeenAtUtc));
        // The first copy is never overwritten.
        var kept = await db.Fills.AsNoTracking().SingleAsync();
        Assert.Equal((100m, 0.05m, Start.AddMinutes(1)), (kept.Price, kept.Fee, kept.OccurredAtUtc));

        var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(db));
        var read = await service.FillConflictsAsync(account.Id, default);
        Assert.Equal(1, read.Total);
        var listed = Assert.Single(read.Recent);
        Assert.Equal(("f1", 2), (listed.SourceFillId, listed.Reports));
        Assert.Equal(["price", "fee", "time"], listed.Fields);
        Assert.Contains("keeps the first copy", read.Notice);

        // Deleting the account removes them with its other retained facts.
        await TestUnitOfWork.For(db).ExecuteAsync(async () =>
        {
            await new WorkspaceStore(db, TestUnitOfWork.For(db)).DeleteAccountAsync(account, default);
            return true;
        }, default);
        Assert.Empty(await db.FillImportConflicts.ToListAsync());
    }

    [PostgresFact]
    public async Task A_changed_copy_in_one_batch_and_a_changed_direction_are_conflicts_and_a_disabled_account_lists_none()
    {
        // Sol round 1 on 14c: a changed second copy within the first batch was dropped silently, the venue's direction wording
        // was not compared, and a disabled account's conflicts stayed readable.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        account.Configure(null, "0x" + new string('b', 40), null);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        await SaveAsync(db, account.Id, Read(2, Fill(), Fill(price: 101), Fill()));
        var kept = await db.Fills.AsNoTracking().SingleAsync();
        Assert.Equal(100m, kept.Price);
        var first = await db.FillImportConflicts.AsNoTracking().SingleAsync();
        Assert.Equal(("price", 1), (first.Fields, first.Reports));

        await SaveAsync(db, account.Id, Read(3, Fill() with { Direction = "Close Short" }));
        var second = await db.FillImportConflicts.AsNoTracking().SingleAsync();
        Assert.Equal(("direction,price", 2), (second.Fields, second.Reports));

        var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(db));
        Assert.Equal(1, (await service.FillConflictsAsync(account.Id, default)).Total);
        account.UpdateSettings(account.Name, null, false);
        await db.SaveChangesAsync();
        var hidden = await service.FillConflictsAsync(account.Id, default);
        Assert.Equal((0, 0), (hidden.Total, hidden.Recent.Count));
    }

    [PostgresFact]
    public async Task Paper_accounts_have_no_import_conflicts_to_read()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var paper = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", "paper", 4000);
        paper.Configure(portfolio.Id, null, null);
        db.AddRange(portfolio, paper);
        await db.SaveChangesAsync();
        var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(db));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.FillConflictsAsync(paper.Id, default, "paper"))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.FillConflictsAsync(Guid.NewGuid(), default))).StatusCode);
    }

    [PostgresFact]
    public async Task A_paper_request_is_refused_before_any_read()
    {
        // Like the raw-facts export (Sol round 1 on #39): actual venue facts only.
        await using var database = await CoreDatabase.CreateAsync();
        await using var factory = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/import-conflicts?tradingMode=paper")).StatusCode);
    }
}
