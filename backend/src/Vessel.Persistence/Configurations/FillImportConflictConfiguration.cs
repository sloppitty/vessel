using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

// A retained fill reported again with different venue facts (Phase 2, item 14c): field names only, never payloads.
internal sealed class FillImportConflictConfiguration : IEntityTypeConfiguration<FillImportConflict>
{
    public void Configure(EntityTypeBuilder<FillImportConflict> builder)
    {
        builder.ToTable("fill_import_conflicts", table =>
        {
            table.HasCheckConstraint("CK_fill_import_conflicts_reports", "\"Reports\" >= 1");
            table.HasCheckConstraint("CK_fill_import_conflicts_seen", "\"FirstSeenAtUtc\" <= \"LastSeenAtUtc\"");
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportedFill>().WithMany().HasForeignKey(x => x.FillId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.OwnerId, x.FillId }).IsUnique();
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.LastSeenAtUtc });
        builder.Property(x => x.ContractId).HasMaxLength(128);
        builder.Property(x => x.SourceFillId).HasMaxLength(128);
        builder.Property(x => x.Fields).HasMaxLength(256);
    }
}
