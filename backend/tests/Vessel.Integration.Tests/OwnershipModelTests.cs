using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

// Owner isolation fails closed: every entity type is owned, filtered by owner and guarded on save.
// None of these tests opens a database connection.
public sealed class OwnershipModelTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Foreign = Guid.NewGuid();

    [Fact]
    public void Every_entity_type_is_owned_and_has_the_owner_filter()
    {
        using var db = Context(Owner);
        var entityTypes = db.Model.GetEntityTypes().ToList();
        Assert.NotEmpty(entityTypes);
        Assert.All(entityTypes, entityType =>
        {
            Assert.True(typeof(IOwned).IsAssignableFrom(entityType.ClrType), $"{entityType.DisplayName()} does not implement IOwned.");
            var filter = Assert.Single(entityType.GetDeclaredQueryFilters());
            Assert.Contains(nameof(VesselDbContext.CurrentOwnerId), filter.Expression?.ToString());
        });
    }

    [Fact]
    public void Every_query_is_scoped_to_the_owner_of_its_own_context()
    {
        using var first = Context(Owner);
        using var second = Context(Foreign);
        // One cached model serves both contexts, so the filter must read each context's owner per query.
        Assert.Same(first.Model, second.Model);
        var set = typeof(DbContext).GetMethods().Single(m => m.Name == nameof(DbContext.Set) && m.GetParameters().Length == 0);
        foreach (var entityType in first.Model.GetEntityTypes())
        {
            string Sql(DbContext db) => ((IQueryable)set.MakeGenericMethod(entityType.ClrType).Invoke(db, null)!).ToQueryString();
            var firstSql = Sql(first);
            var secondSql = Sql(second);
            Assert.Contains("\"OwnerId\" = @ef_filter__CurrentOwnerId", firstSql);
            Assert.Contains(Owner.ToString(), firstSql);
            Assert.DoesNotContain(Foreign.ToString(), firstSql);
            Assert.Contains(Foreign.ToString(), secondSql);
            Assert.DoesNotContain(Owner.ToString(), secondSql);
        }
    }

    [Fact]
    public async Task Save_guard_rejects_records_of_another_owner()
    {
        using var db = Context(Owner);
        db.Portfolios.Add(new Portfolio(Guid.NewGuid(), Foreign, "Foreign"));
        Assert.Throws<UnauthorizedAccessException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Save_guard_rejects_moving_a_foreign_record_to_the_current_owner()
    {
        using var db = Context(Owner);
        var fill = new ImportedFill { Id = Guid.NewGuid(), OwnerId = Foreign, AccountId = Guid.NewGuid() };
        db.Fills.Attach(fill);
        fill.OwnerId = Owner;
        Assert.Throws<UnauthorizedAccessException>(() => db.SaveChanges());
    }

    [Fact]
    public void Save_guard_keeps_diary_audit_and_request_records_append_only()
    {
        using var db = Context(Owner);
        var request = new DiaryRequest { Id = Guid.NewGuid(), OwnerId = Owner, PayloadHash = "hash", Response = "{}" };
        db.DiaryRequests.Attach(request);
        request.Response = "{\"changed\":true}";
        var error = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("append-only", error.Message);
    }

    [Fact]
    public void Model_building_rejects_an_entity_type_without_an_owner()
    {
        using var db = Context(Owner, options => options.ReplaceService<IModelCustomizer, UnownedBeforeOwnerFilters>());
        var error = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(UnownedRecord), error.Message);
        Assert.Contains(nameof(IOwned), error.Message);
    }

    [Fact]
    public void Save_guard_rejects_an_entity_type_without_an_owner()
    {
        // Added after OnModelCreating, so the model builds and only the save guard is left to refuse it.
        using var db = Context(Owner, options => options.ReplaceService<IModelCustomizer, UnownedAfterOwnerFilters>());
        Assert.Empty(db.Model.FindEntityType(typeof(UnownedRecord))!.GetDeclaredQueryFilters());
        db.Add(new UnownedRecord { Id = Guid.NewGuid() });
        var error = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("has no owner", error.Message);
    }

    private static VesselDbContext Context(Guid owner, Action<DbContextOptionsBuilder<VesselDbContext>>? configure = null)
    {
        var options = new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql("Host=model-only");
        configure?.Invoke(options);
        return new VesselDbContext(options.Options, new CoreOwner(owner));
    }

    private sealed class UnownedRecord
    {
        public Guid Id { get; set; }
    }

    // VesselDbContext is sealed, so the extra entity type reaches its real OnModelCreating through the model customizer.
    private sealed class UnownedBeforeOwnerFilters(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            modelBuilder.Entity<UnownedRecord>().ToTable("unowned_records");
            base.Customize(modelBuilder, context);
        }
    }

    private sealed class UnownedAfterOwnerFilters(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<UnownedRecord>().ToTable("unowned_records");
        }
    }
}
