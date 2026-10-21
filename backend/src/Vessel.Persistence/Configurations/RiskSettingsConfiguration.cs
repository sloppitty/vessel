using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Risk;

namespace Vessel.Persistence.Configurations;

internal sealed class RiskSettingsConfiguration : IEntityTypeConfiguration<RiskSettings>
{
    public void Configure(EntityTypeBuilder<RiskSettings> builder)
    {
        builder.ToTable("risk_settings", table => table.HasCheckConstraint("CK_risk_settings_open_risk_limit",
            "\"OpenRiskLimitPercent\" IS NULL OR (\"OpenRiskLimitPercent\" > 0 AND \"OpenRiskLimitPercent\" <= 100)"));
        builder.HasKey(x => x.OwnerId);
        builder.Property(x => x.OpenRiskLimitPercent).HasPrecision(7, 4);
    }
}
