using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;

namespace Vessel.Persistence;

public static class MonitorModel
{
    public static void Configure(ModelBuilder model)
    {
        var account = model.Entity<MonitorAccount>();
        account.ToTable("monitor_accounts", table =>
        {
            table.HasCheckConstraint("CK_monitor_generation", "\"Generation\" >= 0");
            table.HasCheckConstraint("CK_monitor_state_revision", "\"StateRevision\" >= 0");
            table.HasCheckConstraint("CK_monitor_manual_state", "\"ManualState\" IN ('idle','pending','running')");
        });
        account.HasKey(x => x.AccountId);
        account.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        account.Property(x => x.State).HasMaxLength(32);
        account.Property(x => x.ManualState).HasMaxLength(16);
        account.Property(x => x.Trigger).HasMaxLength(32);
        account.Property(x => x.LeaseHolder).HasMaxLength(128);
        account.Property(x => x.Error).HasMaxLength(500);
        account.Property(x => x.CredentialRevision).HasMaxLength(64);
        account.HasIndex(x => new { x.OwnerId, x.NextRunAtUtc });
        account.HasIndex(x => new { x.NextRunAtUtc, x.OwnerId, x.AccountId }).HasDatabaseName("IX_monitor_accounts_due");

        var exit = model.Entity<MonitorExitWatch>();
        exit.ToTable("monitor_exit_watches", table => table.HasCheckConstraint("CK_monitor_exit_watch_outcome",
            "\"Outcome\" IS NULL OR \"Outcome\" IN ('terminal','absent','unverifiable')"));
        exit.HasKey(x => x.LinkId);
        exit.Property(x => x.Outcome).HasMaxLength(16);
        exit.HasOne<PlayOrderLink>().WithMany().HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Cascade);
        exit.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        exit.HasIndex(x => new { x.OwnerId, x.AccountId, x.EndedAtUtc });

        var coverage = model.Entity<MonitorCoverage>();
        coverage.ToTable("monitor_coverage", table =>
        {
            table.HasCheckConstraint("CK_monitor_coverage_state", "\"State\" IN ('known','shared','unknown','unsupported')");
            table.HasCheckConstraint("CK_monitor_coverage_covered", "(\"State\" = 'known') = (\"Covered\" IS NOT NULL)");
        });
        coverage.HasKey(x => x.PlayId);
        coverage.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        coverage.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        coverage.Property(x => x.ContractId).HasMaxLength(200);
        coverage.Property(x => x.State).HasMaxLength(16);
        coverage.Property(x => x.Reason).HasMaxLength(48);
        coverage.Property(x => x.Stops).HasColumnType("jsonb");
        coverage.HasIndex(x => new { x.OwnerId, x.AccountId });
    }
}
