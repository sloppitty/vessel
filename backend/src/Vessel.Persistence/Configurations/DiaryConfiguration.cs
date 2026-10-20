using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Plays;

namespace Vessel.Persistence.Configurations;

// Review history, owner-entered executions with their corrections, and idempotent request records.
internal sealed class PlayReviewRevisionConfiguration : IEntityTypeConfiguration<PlayReviewRevision>
{
    public void Configure(EntityTypeBuilder<PlayReviewRevision> builder)
    {
        builder.ToTable("play_review_revisions");
        builder.HasKey(x => x.Id);
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.OwnerId, x.PlayId, x.Number }).IsUnique();
        builder.Property(x => x.StructuredReview).HasColumnType("jsonb");
        builder.Property(x => x.Review).HasMaxLength(Play.MaxReviewLength);
        builder.Property(x => x.Action).HasMaxLength(16).HasDefaultValue(ReviewActions.Edited);
    }
}

// The append-only in-trade log (phase 1, 1.2). Entries go with their Draft when it is deleted, as review history does.
internal sealed class PlayNoteConfiguration : IEntityTypeConfiguration<PlayNote>
{
    public void Configure(EntityTypeBuilder<PlayNote> builder)
    {
        builder.ToTable("play_notes", table => table.HasCheckConstraint("CK_play_notes_state",
            "\"State\" IS NULL OR \"State\" IN ('calm','rushed','fomo','tired','after-loss')"));
        builder.HasKey(x => x.Id);
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.OwnerId, x.PlayId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.OwnerId, x.RequestId }).IsUnique();
        builder.Property(x => x.Text).HasMaxLength(PlayNote.MaxTextLength);
        builder.Property(x => x.State).HasMaxLength(16);
    }
}

internal sealed class ManualExecutionConfiguration : IEntityTypeConfiguration<ManualExecution>
{
    public void Configure(EntityTypeBuilder<ManualExecution> builder)
    {
        builder.ToTable("manual_executions");
        builder.HasKey(x => x.Id);
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.OwnerId, x.PlayId, x.OccurredAtUtc });
        builder.Property(x => x.Role).HasMaxLength(16); builder.Property(x => x.EntryId).HasMaxLength(100);
        builder.Property(x => x.Side).HasMaxLength(8); builder.Property(x => x.Source).HasMaxLength(100);
        builder.Property(x => x.Quantity).HasMaxLength(96); builder.Property(x => x.Price).HasMaxLength(96);
        builder.Property(x => x.Fee).HasMaxLength(96); builder.Property(x => x.FeeCurrency).HasMaxLength(32);
    }
}

internal sealed class ManualExecutionHistoryConfiguration : IEntityTypeConfiguration<ManualExecutionHistory>
{
    public void Configure(EntityTypeBuilder<ManualExecutionHistory> builder)
    {
        builder.ToTable("manual_execution_history");
        builder.HasKey(x => x.Id);
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.OwnerId, x.PlayId, x.OccurredAtUtc });
        builder.Property(x => x.Before).HasColumnType("jsonb");
        builder.Property(x => x.After).HasColumnType("jsonb");
        builder.Property(x => x.Reason).HasMaxLength(2000);
    }
}

internal sealed class DiaryRequestConfiguration : IEntityTypeConfiguration<DiaryRequest>
{
    public void Configure(EntityTypeBuilder<DiaryRequest> builder)
    {
        builder.ToTable("diary_requests");
        builder.HasKey(x => new { x.OwnerId, x.Id });
        builder.Property(x => x.PayloadHash).HasMaxLength(64);
        builder.Property(x => x.Response).HasColumnType("jsonb");
    }
}
