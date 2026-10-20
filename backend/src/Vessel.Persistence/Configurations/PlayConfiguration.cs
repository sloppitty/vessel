using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;

namespace Vessel.Persistence.Configurations;

internal sealed class PlayConfiguration : IEntityTypeConfiguration<Play>
{
    public void Configure(EntityTypeBuilder<Play> builder)
    {
        builder.ToTable("plays");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.InstrumentSource).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.CancelReason).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Title).HasMaxLength(Play.MaxTitleLength);
        builder.Property(x => x.Plan).HasColumnType("jsonb");
        builder.Property(x => x.Drawings).HasColumnType("jsonb");
        builder.Property(x => x.Review).HasMaxLength(Play.MaxReviewLength);
        builder.Property(x => x.Origin).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ReviewCompletion).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ReviewAdherence).HasMaxLength(32);
        builder.Property(x => x.ReviewMistakeTag).HasMaxLength(100);
        builder.Property(x => x.ReviewLesson).HasMaxLength(4000);
        builder.Property(x => x.ReviewNextAction).HasMaxLength(4000);
        builder.Ignore(x => x.Answers);
        builder.Ignore(x => x.HasReview);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => new { x.OwnerId, x.UpdatedAtUtc });
        // List views filter by status and order by last update or end time, newest first.
        builder.HasIndex(x => new { x.OwnerId, x.Status, x.UpdatedAtUtc, x.Id }).IsDescending(false, false, true, false);
        builder.HasIndex(x => new { x.OwnerId, x.Status, x.EndedAtUtc, x.UpdatedAtUtc }).IsDescending(false, false, true, true);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.Instrument);
        builder.Property(x => x.VenueId).HasMaxLength(64);
        builder.Property(x => x.ContractId).HasMaxLength(128);
        // Deliberately non-unique. Distinct active ideas can share a venue position.
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.VenueId, x.ContractId });
        builder.HasAlternateKey(x => new { x.OwnerId, x.Id });
    }
}

internal sealed class PlayPlanRevisionConfiguration : IEntityTypeConfiguration<PlayPlanRevision>
{
    public void Configure(EntityTypeBuilder<PlayPlanRevision> builder)
    {
        builder.ToTable("play_plan_revisions", table => table.HasCheckConstraint("CK_play_plan_revisions_number", "\"Number\" >= 1"));
        builder.HasKey(x => x.Id);
        // History is deleted only with its Draft, explicitly; it never cascades.
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.PlayId, x.Number }).IsUnique();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Plan).HasColumnType("jsonb");
        builder.Property(x => x.Reason).HasMaxLength(PlayPlanRevision.MaxReasonLength);
        builder.Property(x => x.Preflight).HasColumnType("jsonb");
    }
}

internal sealed class PlayStatusChangeConfiguration : IEntityTypeConfiguration<PlayStatusChange>
{
    public void Configure(EntityTypeBuilder<PlayStatusChange> builder)
    {
        builder.ToTable("play_status_changes");
        builder.HasKey(x => x.Id);
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.PlayId, x.OccurredAtUtc });
        builder.Property(x => x.From).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.To).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Note).HasMaxLength(Play.MaxCancelNoteLength);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(16).HasDefaultValue(StatusChangeSource.Owner)
            .HasSentinel(StatusChangeSource.Owner);
    }
}
