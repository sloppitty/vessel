using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Risk;

namespace Vessel.Persistence.Configurations;

internal sealed class RiskSettingsConfiguration : IEntityTypeConfiguration<RiskSettings>
{
    public void Configure(EntityTypeBuilder<RiskSettings> builder)
    {
        builder.ToTable("risk_settings", table =>
        {
            table.HasCheckConstraint("CK_risk_settings_open_risk_limit",
                "\"OpenRiskLimitPercent\" IS NULL OR (\"OpenRiskLimitPercent\" > 0 AND \"OpenRiskLimitPercent\" <= 100)");
            // Phase 2, item 10b: soft loss limits in R.
            table.HasCheckConstraint("CK_risk_settings_daily_loss_limit",
                "\"DailyLossLimitR\" IS NULL OR (\"DailyLossLimitR\" > 0 AND \"DailyLossLimitR\" <= 100)");
            table.HasCheckConstraint("CK_risk_settings_weekly_loss_limit",
                "\"WeeklyLossLimitR\" IS NULL OR (\"WeeklyLossLimitR\" > 0 AND \"WeeklyLossLimitR\" <= 100)");
        });
        builder.HasKey(x => x.OwnerId);
        builder.Property(x => x.OpenRiskLimitPercent).HasPrecision(7, 4);
        builder.Property(x => x.DailyLossLimitR).HasPrecision(7, 4);
        builder.Property(x => x.WeeklyLossLimitR).HasPrecision(7, 4);
    }
}
