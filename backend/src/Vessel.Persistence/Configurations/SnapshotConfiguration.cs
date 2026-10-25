using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

// The latest observation of an account, with its stablecoin balances and positions.
internal sealed class AccountSnapshotConfiguration : IEntityTypeConfiguration<AccountSnapshot>
{
    public void Configure(EntityTypeBuilder<AccountSnapshot> builder)
    {
        builder.ToTable("account_snapshots");
        builder.HasKey(x => new { x.OwnerId, x.AccountId });
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.ValueScope).HasMaxLength(128);
        builder.Property(x => x.AccountMode).HasMaxLength(64);
        builder.Property(x => x.StablecoinScope).HasMaxLength(128);
        builder.HasMany(x => x.Stablecoins).WithOne().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Positions).WithOne().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AccountStablecoinConfiguration : IEntityTypeConfiguration<AccountStablecoin>
{
    public void Configure(EntityTypeBuilder<AccountStablecoin> builder)
    {
        builder.ToTable("account_stablecoins");
        builder.HasKey(x => new { x.OwnerId, x.AccountId, x.TokenId });
        builder.Property(x => x.TokenId).HasMaxLength(64);
        builder.Property(x => x.Symbol).HasMaxLength(32);
    }
}

internal sealed class AccountPositionConfiguration : IEntityTypeConfiguration<AccountPosition>
{
    public void Configure(EntityTypeBuilder<AccountPosition> builder)
    {
        builder.ToTable("account_positions", table => table.HasCheckConstraint("CK_account_positions_margin_mode",
            "\"MarginMode\" IS NULL OR \"MarginMode\" IN ('cross', 'isolated')"));
        builder.HasKey(x => new { x.OwnerId, x.AccountId, x.ContractId });
        builder.Property(x => x.ContractId).HasMaxLength(128);
        builder.Property(x => x.VenueContractId).HasMaxLength(128);
        builder.Property(x => x.MarginMode).HasMaxLength(16);
    }
}
