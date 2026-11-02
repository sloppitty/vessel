using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Reporting;

namespace Vessel.Persistence.Configurations;

internal sealed class ReportingSettingsConfiguration : IEntityTypeConfiguration<ReportingSettings>
{
    public void Configure(EntityTypeBuilder<ReportingSettings> builder)
    {
        builder.ToTable("reporting_settings", table => table.HasCheckConstraint("CK_reporting_settings_week_start",
            "\"WeekStart\" IN ('monday', 'sunday')"));
        builder.HasKey(x => x.OwnerId);
        builder.Property(x => x.TimeZone).HasMaxLength(ReportingSettings.MaxTimeZoneLength);
        builder.Property(x => x.WeekStart).HasMaxLength(16);
    }
}
