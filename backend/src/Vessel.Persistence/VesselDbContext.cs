using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Credentials;
using Vessel.Domain.Evidence;
using Vessel.Domain.Plays;
using Vessel.Domain.Paper;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed class VesselDbContext(DbContextOptions<VesselDbContext> options, IJournalOwnerContext owner)
    : DbContext(options)
{
    public Guid CurrentOwnerId => owner.OwnerId;
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountCredential> AccountCredentials => Set<AccountCredential>();
    public DbSet<Play> Plays => Set<Play>();
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<PaperSettings> PaperSettings => Set<PaperSettings>();
    public DbSet<PaperLedgerEntry> PaperLedgerEntries => Set<PaperLedgerEntry>();
    public DbSet<PaperExecution> PaperExecutions => Set<PaperExecution>();
    public DbSet<PaperSimulation> PaperSimulations => Set<PaperSimulation>();
    public DbSet<PaperPlanActivation> PaperPlanActivations => Set<PaperPlanActivation>();
    public DbSet<PaperReservation> PaperReservations => Set<PaperReservation>();
    public DbSet<AccountSnapshot> Snapshots => Set<AccountSnapshot>();
    public DbSet<AccountPosition> Positions => Set<AccountPosition>();
    public DbSet<AccountStablecoin> Stablecoins => Set<AccountStablecoin>();
    public DbSet<ImportedFill> Fills => Set<ImportedFill>();
    public DbSet<LeverageObservation> LeverageObservations => Set<LeverageObservation>();
    public DbSet<RiskObservation> RiskObservations => Set<RiskObservation>();
    public DbSet<FillImportConflict> FillImportConflicts => Set<FillImportConflict>();
    public DbSet<FundingPayment> FundingPayments => Set<FundingPayment>();
    public DbSet<AccountFundingState> FundingStates => Set<AccountFundingState>();
    public DbSet<PlayEvidence> Evidence => Set<PlayEvidence>();
    public DbSet<PlayPlanRevision> PlanRevisions => Set<PlayPlanRevision>();
    public DbSet<PlayStatusChange> StatusChanges => Set<PlayStatusChange>();
    public DbSet<ImportedOrder> Orders => Set<ImportedOrder>();
    public DbSet<PlayOrderLink> OrderLinks => Set<PlayOrderLink>();
    public DbSet<ManualExecution> ManualExecutions => Set<ManualExecution>();
    public DbSet<ManualExecutionHistory> ManualExecutionHistory => Set<ManualExecutionHistory>();
    public DbSet<PlayReviewRevision> ReviewRevisions => Set<PlayReviewRevision>();
    public DbSet<PlayNote> PlayNotes => Set<PlayNote>();
    public DbSet<DiaryRequest> DiaryRequests => Set<DiaryRequest>();
    public DbSet<SizingSettings> SizingSettings => Set<SizingSettings>();
    public DbSet<Vessel.Domain.Risk.RiskSettings> RiskSettings => Set<Vessel.Domain.Risk.RiskSettings>();

    // Unconstrained PostgreSQL numeric preserves every .NET decimal digit and scale. This also covers decimal?.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<decimal>().HaveColumnType("numeric");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VesselDbContext).Assembly);
        MonitorModel.Configure(modelBuilder);
        MonitorDiscrepancyModel.Configure(modelBuilder);
        ApplyOwnerFilters(modelBuilder);
    }

    // Every entity type is owned and filtered to the current owner. A type that is not IOwned fails model
    // building, so a new table cannot silently miss the read filter or the save guard.
    private void ApplyOwnerFilters(ModelBuilder modelBuilder)
    {
        // The context constant becomes a per-query parameter, exactly as with HasQueryFilter(x => x.OwnerId == CurrentOwnerId).
        var currentOwnerId = Expression.Property(Expression.Constant(this), nameof(CurrentOwnerId));
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(IOwned).IsAssignableFrom(entityType.ClrType))
                throw new InvalidOperationException(
                    $"Entity type '{entityType.DisplayName()}' must implement {nameof(IOwned)} to be filtered and guarded by owner.");
            var entity = Expression.Parameter(entityType.ClrType, "e");
            var ownerId = Expression.Property(Expression.Convert(entity, typeof(IOwned)), nameof(IOwned.OwnerId));
            entityType.SetQueryFilter(Expression.Lambda(Expression.Equal(ownerId, currentOwnerId), entity));
        }
    }

    private void ValidateOwnership()
    {
        // Reject foreign tracked records even if loaded with an explicit filter bypass.
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Modified &&
                entry.Entity is Vessel.Domain.Plays.ManualExecutionHistory or PlayReviewRevision or DiaryRequest)
                throw new InvalidOperationException("Diary audit and request records are append-only.");
            var owned = entry.Entity as IOwned ?? throw new InvalidOperationException(
                $"Entity type '{entry.Metadata.DisplayName()}' has no owner and cannot be saved.");
            if (owned.OwnerId != CurrentOwnerId ||
                entry.Property(nameof(IOwned.OwnerId)).OriginalValue is Guid original && original != CurrentOwnerId)
                throw new UnauthorizedAccessException("Cannot write another owner's records.");
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
