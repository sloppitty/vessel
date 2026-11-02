using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Vessel.Application.Evidence;
using Vessel.Domain.Accounts;
using Vessel.Domain.Evidence;
using Vessel.Domain.Markets;
using Vessel.Infrastructure;
using Vessel.Infrastructure.Evidence;

namespace Vessel.Tests;

public sealed class EvidenceIntegrationTests : EvidenceTestsSupport, IDisposable
{

    [Fact]
    public async Task Local_store_round_trips_without_overwriting_or_leaving_temporary_files()
    {
        var local = new LocalEvidenceObjectStore(root);
        await local.PutAsync("owner/play/item.png", new MemoryStream(EvidenceFixtures.Png), default);
        await using (var read = await local.OpenReadAsync("owner/play/item.png", default))
        {
            using var copy = new MemoryStream();
            await read!.CopyToAsync(copy);
            Assert.Equal(EvidenceFixtures.Png, copy.ToArray());
        }
        await Assert.ThrowsAsync<IOException>(() => local.PutAsync("owner/play/item.png", new MemoryStream(EvidenceFixtures.Jpeg), default));
        Assert.Equal(["item.png"], Directory.GetFiles(Path.Combine(root, "owner", "play")).Select(Path.GetFileName));
        await local.DeleteAsync("owner/play/item.png", default);
        await local.DeleteAsync("owner/play/item.png", default);
        Assert.Null(await local.OpenReadAsync("owner/play/item.png", default));
        Assert.Null(await local.OpenReadAsync("owner/other/item.png", default));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("owner/../../outside.png")]
    [InlineData("/etc/passwd.png")]
    [InlineData("Owner/item.png")]
    [InlineData("owner/item")]
    [InlineData("owner\\item.png")]
    [InlineData("owner/item.png\n")]
    public async Task Local_store_rejects_keys_it_did_not_generate(string key)
    {
        var local = new LocalEvidenceObjectStore(root);
        await Assert.ThrowsAsync<ArgumentException>(() => local.PutAsync(key, new MemoryStream(EvidenceFixtures.Png), default));
        await Assert.ThrowsAsync<ArgumentException>(() => local.OpenReadAsync(key, default));
        Assert.False(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any());
    }
}

public sealed class EvidenceApiIntegrationTests : EvidenceApiTestsSupport, IDisposable
{

    [Fact]
    public async Task Unknown_storage_provider_fails_at_startup()
    {
        // A plain host: WebApplicationFactory's deferred host can dispose its services before the startup error surfaces.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Vessel:Evidence:Storage:Provider"] = "Unknown" });
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("Provider must be Local or S3", error.Message);
    }

    [Fact]
    public void Default_settings_use_the_legacy_local_folder()
    {
        var defaults = new EvidenceSettings();
        Assert.Equal(("Local", "data/evidence", 10L * 1024 * 1024, 50),
            (defaults.Storage.Provider, defaults.Storage.Local.RootPath, defaults.MaxUploadBytes, defaults.MaxPerPlay));
    }
}

public sealed class EvidencePostgresTests
{
    [PostgresFact]
    public async Task Migration_stores_owner_scoped_evidence_restricted_to_its_play()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        var evidence = new PlayEvidence(Guid.NewGuid(), play, $"{owner:N}/{play.Id:N}/a.png", "image/png", 12,
            new string('a', 64), EvidenceSource.Capture, "Note", DateTimeOffset.Parse("2026-10-02T10:00:00Z"));
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            db.Plays.Add(play);
            db.Evidence.Add(evidence);
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var tracked = await db.Evidence.SingleAsync();
            tracked.UpdateMarkup(EvidenceMarkup.Serialize(new ImageMarkup(10, 10, [new("a", "box", "#ffffff", 2, From: new(1, 1), To: new(5, 5))])), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal("box", (await db.Database.SqlQueryRaw<string>("SELECT \"Markup\"->'shapes'->0->>'kind' AS \"Value\" FROM play_evidence").SingleAsync()));
        }
        await using (var db = database.Context(owner))
        {
            var stored = await new Vessel.Persistence.EvidenceStore(db, TestUnitOfWork.For(db)).ListAsync(play.Id, default);
            Assert.Equal(("Note", EvidenceSource.Capture, 12L), (Assert.Single(stored).Note, stored[0].Source, stored[0].SizeBytes));
            db.Plays.Remove(await db.Plays.SingleAsync());
            var restrict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, Assert.IsType<PostgresException>(restrict.InnerException).SqlState);
        }
        await using (var db = database.Context(other))
        {
            Assert.Empty(await db.Evidence.ToListAsync());
            Assert.Null(await new Vessel.Persistence.EvidenceStore(db, TestUnitOfWork.For(db)).FindAsync(evidence.Id, default));
            var stolen = await db.Evidence.IgnoreQueryFilters().SingleAsync();
            stolen.UpdateNote("Changed", DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
        }
        await using (var db = database.Context(owner))
        {
            var sneaky = new PlayEvidence(Guid.NewGuid(), play, $"{owner:N}/{play.Id:N}/b.png", "image/png", 1,
                new string('A', 64), EvidenceSource.Upload, "", DateTimeOffset.UtcNow);
            db.Evidence.Add(sneaky);
            var check = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(check.InnerException).SqlState);
        }
    }
}

public sealed class EvidenceLimitPostgresTests
{
    [PostgresFact]
    public async Task Concurrent_adds_never_exceed_the_per_play_limit()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            db.Plays.Add(play);
            await db.SaveChangesAsync();
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var db = database.Context(owner);
            var tracked = await db.Plays.SingleAsync();
            var evidence = new PlayEvidence(Guid.NewGuid(), tracked, $"{owner:N}/{play.Id:N}/{i}.png", "image/png", 12,
                new string('a', 64), EvidenceSource.Upload, "", DateTimeOffset.UtcNow);
            var work = TestUnitOfWork.For(db);
            return await work.ExecuteAsync(() => new Vessel.Persistence.EvidenceStore(db, work).AddAsync(evidence, 3, default), default);
        }));
        Assert.Equal(3, results.Count(added => added));
        await using (var db = database.Context(owner))
            Assert.Equal(3, await db.Evidence.CountAsync());
    }
}
