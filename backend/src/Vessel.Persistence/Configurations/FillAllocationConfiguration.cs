using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

// Phase 2, item 11: the owner's split of one imported fill between Plays, its current lines and every revision.
internal sealed class FillAllocationConfiguration : IEntityTypeConfiguration<FillAllocation>
{
    public void Configure(EntityTypeBuilder<FillAllocation> builder)
    {
        builder.ToTable("fill_allocations", table =>
        {
            table.HasCheckConstraint("CK_fill_allocations_revision", "\"Revision\" >= 1");
            table.HasCheckConstraint("CK_fill_allocations_reason", "\"Reason\" IN ('SharedClose', 'Flip', 'Other')");
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        // Fills are deleted only with their account, which no Play may hold, so no line survives to lose its fill.
        builder.HasOne<ImportedFill>().WithMany().HasForeignKey(x => x.FillId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.AllocationId).OnDelete(DeleteBehavior.Cascade);
        // One allocation per fill; a replacement or removal is its next revision.
        builder.HasIndex(x => new { x.OwnerId, x.FillId }).IsUnique();
        builder.HasIndex(x => new { x.OwnerId, x.AccountId });
        builder.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Note).HasMaxLength(FillAllocation.MaxNoteLength);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Property);
    }
}

internal sealed class FillAllocationLineConfiguration : IEntityTypeConfiguration<FillAllocationLine>
{
    public void Configure(EntityTypeBuilder<FillAllocationLine> builder)
    {
        builder.ToTable("fill_allocation_lines", table =>
        {
            table.HasCheckConstraint("CK_fill_allocation_lines_quantity", "\"Quantity\" > 0");
            table.HasCheckConstraint("CK_fill_allocation_lines_role", "\"Role\" IN ('Exit', 'Entry')");
            table.HasCheckConstraint("CK_fill_allocation_lines_entry", "(\"Role\" = 'Entry') = (\"EntryId\" IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        // A Play with a line is never a Draft, and only Drafts are deleted.
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.AllocationId, x.PlayId }).IsUnique();
        builder.HasIndex(x => new { x.OwnerId, x.PlayId });
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(8);
        builder.Property(x => x.EntryId).HasMaxLength(64);
    }
}

internal sealed class FillAllocationRevisionConfiguration : IEntityTypeConfiguration<FillAllocationRevision>
{
    public void Configure(EntityTypeBuilder<FillAllocationRevision> builder)
    {
        builder.ToTable("fill_allocation_revisions");
        builder.HasKey(x => x.Id);
        builder.HasOne<FillAllocation>().WithMany().HasForeignKey(x => x.AllocationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.AllocationId, x.Revision }).IsUnique();
        builder.HasIndex(x => new { x.OwnerId, x.AllocationId });
        builder.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Note).HasMaxLength(FillAllocation.MaxNoteLength);
        builder.Property(x => x.Lines).HasColumnType("jsonb");
    }
}
