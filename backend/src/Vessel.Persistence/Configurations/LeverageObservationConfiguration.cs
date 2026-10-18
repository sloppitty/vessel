using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

// Venue-reported position leverage seen by refreshes, retained after the latest snapshot replaces its position.
internal sealed class LeverageObservationConfiguration : IEntityTypeConfiguration<LeverageObservation>
{
    public void Configure(EntityTypeBuilder<LeverageObservation> builder)
    {
        builder.ToTable("account_leverage_observations", table =>
            table.HasCheckConstraint("CK_account_leverage_observations_leverage", "\"Leverage\" BETWEEN 1 AND 1000"));
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.ContractId, x.ObservedAtUtc });
        builder.Property(x => x.ContractId).HasMaxLength(128);
        builder.Property(x => x.VenueContractId).HasMaxLength(128);
    }
}
