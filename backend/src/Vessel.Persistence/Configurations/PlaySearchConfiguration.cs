using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Plays;

namespace Vessel.Persistence.Configurations;

/// <summary>Phase 2, item 13: one projection row per Play, deleted with it. Derived data only; no fact refers to it.</summary>
internal sealed class PlaySearchEntryConfiguration : IEntityTypeConfiguration<PlaySearchEntry>
{
    public void Configure(EntityTypeBuilder<PlaySearchEntry> builder)
    {
        builder.ToTable("play_search", table =>
        {
            table.HasCheckConstraint("CK_play_search_state",
                "\"State\" IN ('active', 'scored', 'executions', 'fees', 'unresolved', 'cancelled')");
            // Figures exist only for a scored Play, and R never without its result.
            table.HasCheckConstraint("CK_play_search_figures",
                "(\"State\" = 'scored') = (\"ResultUsd\" IS NOT NULL) AND (\"EstimatedR\" IS NULL OR \"ResultUsd\" IS NOT NULL) AND " +
                "(\"RReason\" IS NULL OR \"State\" = 'scored' AND \"EstimatedR\" IS NULL)");
            table.HasCheckConstraint("CK_play_search_reason", "\"RReason\" IS NULL OR \"RReason\" IN ('retrospective', 'plan-after-fill', 'no-stop')");
        });
        builder.HasKey(x => new { x.OwnerId, x.PlayId });
        builder.HasOne<Play>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Property(x => x.State).HasMaxLength(16);
        builder.Property(x => x.RReason).HasMaxLength(16);
    }
}

/// <summary>Searchable texts of a projected Play, deleted with its projection. Matching is a substring of the folded text.</summary>
internal sealed class PlaySearchTextConfiguration : IEntityTypeConfiguration<PlaySearchText>
{
    public void Configure(EntityTypeBuilder<PlaySearchText> builder)
    {
        builder.ToTable("play_search_texts", table => table.HasCheckConstraint("CK_play_search_texts_text",
            "\"Text\" <> '' AND length(\"Folded\") = length(\"Text\") AND \"Position\" >= 0"));
        builder.HasKey(x => new { x.OwnerId, x.PlayId, x.Position });
        builder.HasOne<PlaySearchEntry>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PlayId }).OnDelete(DeleteBehavior.Cascade);
        builder.Property(x => x.Field).HasMaxLength(16);
    }
}
