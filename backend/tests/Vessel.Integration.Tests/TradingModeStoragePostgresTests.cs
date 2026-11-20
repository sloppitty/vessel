using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Issue #113: the typed mode is stored as the same column strings, and existing rows read back unchanged.</summary>
public sealed class TradingModeStoragePostgresTests
{
    [PostgresFact]
    public async Task The_typed_mode_is_stored_as_real_or_paper_and_rows_written_as_strings_read_back()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var paperPortfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", TradingMode.Paper);
        var paper = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", TradingMode.Paper);
        paper.Configure(paperPortfolio.Id, null, null);
        var real = new Account(Guid.NewGuid(), owner, "manual", "Real", TradingMode.Real);
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(paperPortfolio); db.Accounts.AddRange(paper, real);
            await db.SaveChangesAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        await using (var db = database.Context(owner))
        {
            Assert.Equal(["paper:Paper", "real:Real"], await db.Database.SqlQueryRaw<string>(
                "SELECT \"TradingMode\" || ':' || \"Name\" AS \"Value\" FROM accounts ORDER BY 1").ToListAsync());
            Assert.Equal("paper", await db.Database.SqlQueryRaw<string>(
                "SELECT \"TradingMode\" AS \"Value\" FROM portfolios").SingleAsync());
            // A row written with the database default, as every account before paper trading was, is real.
            var legacy = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO accounts (\"Id\",\"OwnerId\",\"VenueId\",\"Name\",\"SyncStatus\") VALUES ({legacy},{owner},'manual','Legacy','manual')");
            Assert.Equal(TradingMode.Real, (await db.Accounts.SingleAsync(a => a.Id == legacy)).TradingMode);
            Assert.Equal([paper.Id], await db.Accounts.Where(a => a.TradingMode == TradingMode.Paper).Select(a => a.Id).ToListAsync());
            var mode = TradingMode.Real;
            Assert.Equal(2, await db.Accounts.CountAsync(a => a.TradingMode == mode));
        }
    }
}
