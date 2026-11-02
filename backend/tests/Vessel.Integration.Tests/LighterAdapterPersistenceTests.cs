using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Lighter's history notices against the persisted column, read from the EF model without a connection.</summary>
public sealed class LighterAdapterPersistenceTests
{
    [Fact]
    public void History_notice_fits_its_column_even_with_every_sentence_at_the_largest_count()
    {
        // Conservative: no single refresh adds every sentence, and the omitted counts share at most 500 executions.
        var notice = string.Join(" ", LighterReader.HistoryNotice, LighterReader.NonzeroRateNotice,
            LighterReader.CredentialRefusedNotice, LighterReader.HistoryRetryNotice, LighterReader.HistoryUnavailableNotice,
            LighterReader.PageLimitNotice, LighterReader.OmissionNotice(500, 500, 500, 500, 500, 500));
        var options = new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql("Host=model-only").Options;
        using var db = new VesselDbContext(options, new CoreOwner(Guid.NewGuid()));
        var limit = db.Model.FindEntityType(typeof(Vessel.Domain.Accounts.Account))!.FindProperty(nameof(Vessel.Domain.Accounts.Account.HistoryNotice))!.GetMaxLength();
        Assert.NotNull(limit);
        Assert.Equal(1000, limit);
        Assert.True(notice.Length <= limit, $"{notice.Length} characters exceed the {limit}-character HistoryNotice column.");
        foreach (var coverage in AccountHistoryNoticeBudget.CombinedCases())
        {
            var account = new Account(Guid.NewGuid(), Guid.NewGuid(), coverage.Venue, "Coverage");
            account.RecordSync(DateTimeOffset.UnixEpoch, "Prior public coverage.");
            account.RecordOrderHistoryNotice(coverage.Orders);
            account.RecordSync(DateTimeOffset.UnixEpoch.AddSeconds(1), coverage.Public);
            var expected = coverage.Public + "\nOrder history: " + coverage.Orders;
            Assert.Equal(expected, account.HistoryNotice);
            Assert.True(expected.Length <= limit,
                $"{coverage.Venue}: {expected.Length} combined characters exceed the {limit}-character HistoryNotice column.");
            account.RecordOrderHistoryNotice(coverage.Orders);
            Assert.Equal(expected, account.HistoryNotice);
        }
    }
}
