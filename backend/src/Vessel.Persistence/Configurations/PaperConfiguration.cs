using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;

namespace Vessel.Persistence.Configurations;

internal sealed class PaperSettingsConfiguration : IEntityTypeConfiguration<PaperSettings>
{
    public void Configure(EntityTypeBuilder<PaperSettings> b)
    {
        b.ToTable("paper_settings", t => t.HasCheckConstraint("CK_paper_settings_interval", "\"CheckIntervalMinutes\" BETWEEN 1 AND 1440"));
        b.HasKey(x => x.OwnerId);
    }
}

internal sealed class PaperLedgerEntryConfiguration : IEntityTypeConfiguration<PaperLedgerEntry>
{
    public void Configure(EntityTypeBuilder<PaperLedgerEntry> b)
    {
        b.ToTable("paper_ledger_entries", t =>
        {
            t.HasCheckConstraint("CK_paper_ledger_mode", "\"TradingMode\" = 'paper'");
            t.HasCheckConstraint("CK_paper_ledger_kind",
                """("Kind"='seed' AND "PlayId" IS NULL AND "ExecutionId" IS NULL AND "AmountUsd">=0) OR ("Kind"='realized-pnl' AND "PlayId" IS NOT NULL AND "ExecutionId" IS NOT NULL)""");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.TradingMode).HasMaxLength(8);
        b.Property(x => x.Kind).HasMaxLength(24);
        b.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaperExecution>().WithMany().HasForeignKey(x => new { x.OwnerId, x.ExecutionId, x.AccountId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.AccountId, x.PlayId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OwnerId, x.AccountId }).IsUnique().HasFilter("\"Kind\"='seed'");
        b.HasIndex(x => new { x.OwnerId, x.ExecutionId }).IsUnique().HasFilter("\"ExecutionId\" IS NOT NULL");
        foreach (var p in b.Metadata.GetProperties()) p.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}

internal sealed class PaperExecutionConfiguration : IEntityTypeConfiguration<PaperExecution>
{
    public void Configure(EntityTypeBuilder<PaperExecution> b)
    {
        b.ToTable("paper_executions", t =>
        {
            t.HasCheckConstraint("CK_paper_execution_mode", "\"TradingMode\"='paper'");
            t.HasCheckConstraint("CK_paper_execution_model", "\"ModelVersion\"=1");
            t.HasCheckConstraint("CK_paper_execution_values", "\"Price\">0 AND \"Quantity\">0 AND \"PlanVersion\">=1");
            t.HasCheckConstraint("CK_paper_execution_precision", "\"Price\"<=1000000000000 AND \"Quantity\"<=1000000000000 AND \"MarginUsd\" BETWEEN -1000000000000000 AND 1000000000000000 AND \"PnlUsd\" BETWEEN -1000000000000000 AND 1000000000000000 AND scale(trim_scale(\"Price\"))<=28 AND \"Price\"*power(10::numeric,scale(trim_scale(\"Price\")))<=79228162514264337593543950335 AND trunc(\"Quantity\",12)=\"Quantity\" AND trunc(\"MarginUsd\",12)=\"MarginUsd\" AND trunc(\"PnlUsd\",12)=\"PnlUsd\"");
            t.HasCheckConstraint("CK_paper_execution_role", """("Role"='entry' AND "LevelId" IS NULL AND "PnlUsd"=0 AND "MarginUsd">0) OR ("Role" IN ('stop','target','exit') AND "MarginUsd"<0)""");
            t.HasCheckConstraint("CK_paper_execution_time", "\"CandleCloseAtUtc\"=\"CandleOpenAtUtc\"+interval '1 minute' AND \"OccurredAtUtc\"=\"CandleCloseAtUtc\"");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.OwnerId, x.Id, x.AccountId, x.PlayId });
        b.Property(x => x.ModelVersion).HasDefaultValue(1);
        b.Property(x => x.TradingMode).HasMaxLength(8);
        b.Property(x => x.EntryId).HasMaxLength(64);
        b.Property(x => x.LevelId).HasMaxLength(64);
        b.Property(x => x.Role).HasMaxLength(12);
        b.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        // Entry executes once, and each planned exit allocation executes once, across retries and revisions.
        b.HasIndex(x => new { x.OwnerId, x.PlayId, x.EntryId }).IsUnique().HasFilter("\"Role\"='entry'");
        b.HasIndex(x => new { x.OwnerId, x.PlayId, x.EntryId, x.LevelId, x.Role }).IsUnique().HasFilter("\"Role\"<>'entry'");
        b.HasIndex(x => new { x.OwnerId, x.AccountId, x.OccurredAtUtc });
        foreach (var p in b.Metadata.GetProperties()) p.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}

internal sealed class PaperSimulationConfiguration : IEntityTypeConfiguration<PaperSimulation>
{
    public void Configure(EntityTypeBuilder<PaperSimulation> b)
    {
        b.ToTable("paper_simulations", t =>
        {
            t.HasCheckConstraint("CK_paper_simulation_mode", "\"TradingMode\"='paper'");
            t.HasCheckConstraint("CK_paper_simulation_state", "\"State\" IN ('waiting','ready','ambiguous','gap','disabled','suspended')");
        });
        b.HasKey(x => x.PlayId);
        b.Property(x => x.TradingMode).HasMaxLength(8);
        b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.IssuesJson).HasColumnType("jsonb");
        b.Property(x => x.DeclinedEntriesJson).HasColumnType("jsonb");
        b.Property(x => x.SuspensionsJson).HasColumnType("jsonb");
        b.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Play>().WithOne().HasForeignKey<PaperSimulation>(x => new { x.OwnerId, x.PlayId, x.TradingMode })
            .HasPrincipalKey<Play>(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OwnerId, x.NextCheckAtUtc, x.PlayId });
    }
}

internal sealed class PaperPlanActivationConfiguration : IEntityTypeConfiguration<PaperPlanActivation>
{
    public void Configure(EntityTypeBuilder<PaperPlanActivation> b)
    {
        b.ToTable("paper_plan_activations", t => t.HasCheckConstraint("CK_paper_activation_mode", "\"TradingMode\"='paper'"));
        b.HasKey(x => x.Id);
        b.Property(x => x.TradingMode).HasMaxLength(8);
        b.Property(x => x.PlanJson).HasColumnType("jsonb");
        b.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OwnerId, x.PlayId, x.EffectiveAtUtc, x.PlanVersion }).IsUnique();
        foreach (var p in b.Metadata.GetProperties()) p.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}

internal sealed class PaperReservationConfiguration : IEntityTypeConfiguration<PaperReservation>
{
    public void Configure(EntityTypeBuilder<PaperReservation> b)
    {
        b.ToTable("paper_reservations", t =>
        {
            t.HasCheckConstraint("CK_paper_reservation_mode", "\"TradingMode\"='paper'");
            t.HasCheckConstraint("CK_paper_reservation_amount", "\"AmountUsd\">=0");
        });
        b.HasKey(x => x.PlayId);
        b.Property(x => x.TradingMode).HasMaxLength(8);
        b.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Play>().WithOne().HasForeignKey<PaperReservation>(x => new { x.OwnerId, x.PlayId, x.TradingMode })
            .HasPrincipalKey<Play>(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
    }
}
