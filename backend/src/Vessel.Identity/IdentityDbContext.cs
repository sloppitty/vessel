using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Vessel.Identity;

public sealed class IdentityUser
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "member";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public int Revision { get; set; } = 1;
}

public sealed class UserWallet
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Address { get; set; } = "";
    public string? Label { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class AuthChallenge
{
    public Guid Id { get; set; }
    public string Purpose { get; set; } = "sign-in";
    public string Address { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Message { get; set; } = "";
    public byte[]? BindingHash { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string? Action { get; set; }
    public string? Target { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class PendingRegistration
{
    public Guid Id { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public string Address { get; set; } = "";
    public string Origin { get; set; } = "";
    public byte[] BindingHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class IdentitySession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? WalletId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public bool Remember { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public string? StepUpAction { get; set; }
    public string? StepUpTarget { get; set; }
    public Guid? StepUpWalletId { get; set; }
    public DateTimeOffset? StepUpExpiresAt { get; set; }
    public string? UserAgentSummary { get; set; }
    /// <summary>How the session was issued, for labels only: <c>wallet</c> (signature) or <c>pairing</c> (device pairing).</summary>
    public string Source { get; set; } = "wallet";
}

/// <summary>
/// A signed-in desktop connecting a new device without a wallet (device-pairing-plan.md). Only SHA-256 hashes of the
/// secrets are kept. <see cref="ExpiresAt"/> is the deadline of the current <see cref="Status"/>; an unfinished row
/// past it is reported as <c>expired</c> even while it still holds its earlier status.
/// </summary>
public sealed class DevicePairing
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CreatedSessionId { get; set; }
    /// <summary>The wallet that signed the <c>device.pair</c> step-up; null only once the wallet was removed and the pairing cancelled.</summary>
    public Guid? SignerWalletId { get; set; }
    public byte[] SecretHash { get; set; } = [];
    public byte[] TypedCodeHash { get; set; } = [];
    public string Origin { get; set; } = "";
    public string Status { get; set; } = "pending";
    public byte[]? BindingHash { get; set; }
    public byte[]? CodeHash { get; set; }
    public int CodeAttempts { get; set; }
    public string? DeviceSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class Invitation
{
    public Guid Id { get; set; }
    public byte[] CodeHash { get; set; } = [];
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxUses { get; set; }
    public int Uses { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, IdentitySchema schema) : DbContext(options)
{
    public IdentitySchema Schema => schema;
    public DbSet<IdentityUser> Users => Set<IdentityUser>();
    public DbSet<UserWallet> Wallets => Set<UserWallet>();
    public DbSet<AuthChallenge> Challenges => Set<AuthChallenge>();
    public DbSet<PendingRegistration> Registrations => Set<PendingRegistration>();
    public DbSet<IdentitySession> Sessions => Set<IdentitySession>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<DevicePairing> Pairings => Set<DevicePairing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(schema.Name);
        var users = modelBuilder.Entity<IdentityUser>();
        users.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_role", "role IN ('member', 'operator')");
            table.HasCheckConstraint("ck_users_status", "status IN ('active', 'suspended')");
        });
        users.Property(x => x.DisplayName).HasMaxLength(60);
        var wallets = modelBuilder.Entity<UserWallet>();
        wallets.ToTable("user_wallets");
        wallets.Property(x => x.Address).HasColumnType("character(42)");
        wallets.Property(x => x.Label).HasMaxLength(40);
        wallets.HasIndex(x => x.Address).IsUnique();
        wallets.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        var challenges = modelBuilder.Entity<AuthChallenge>();
        challenges.ToTable("auth_challenges", table =>
            table.HasCheckConstraint("ck_auth_challenges_purpose", "purpose IN ('sign-in', 'link', 'step-up')"));
        challenges.Property(x => x.Address).HasColumnType("character(42)");
        challenges.HasIndex(x => x.ExpiresAt);
        var registrations = modelBuilder.Entity<PendingRegistration>();
        registrations.ToTable("pending_registrations");
        registrations.Property(x => x.Address).HasColumnType("character(42)");
        registrations.HasIndex(x => x.TokenHash).IsUnique();
        registrations.HasIndex(x => x.ExpiresAt);
        var sessions = modelBuilder.Entity<IdentitySession>();
        sessions.ToTable("sessions", table =>
        {
            table.HasCheckConstraint("ck_sessions_live_wallet", "revoked_at IS NOT NULL OR wallet_id IS NOT NULL");
            table.HasCheckConstraint("ck_sessions_source", "source IN ('wallet', 'pairing')");
        });
        sessions.Property(x => x.UserAgentSummary).HasMaxLength(80);
        sessions.Property(x => x.Source).HasDefaultValue("wallet");
        sessions.HasIndex(x => x.TokenHash).IsUnique();
        sessions.HasIndex(x => new { x.UserId, x.CreatedAt });
        sessions.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        sessions.HasOne<UserWallet>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.SetNull);
        sessions.HasOne<UserWallet>().WithMany().HasForeignKey(x => x.StepUpWalletId).OnDelete(DeleteBehavior.SetNull);
        var pairings = modelBuilder.Entity<DevicePairing>();
        pairings.ToTable("device_pairings", table =>
        {
            table.HasCheckConstraint("ck_device_pairings_status",
                "status IN ('pending', 'claimed', 'approved', 'completed', 'cancelled', 'contested', 'expired')");
            // Like sessions.wallet_id: an unfinished pairing always names the wallet that signed for it.
            table.HasCheckConstraint("ck_device_pairings_live_wallet",
                "signer_wallet_id IS NOT NULL OR status NOT IN ('pending', 'claimed', 'approved')");
        });
        pairings.Property(x => x.DeviceSummary).HasMaxLength(80);
        pairings.HasIndex(x => x.SecretHash).IsUnique();
        pairings.HasIndex(x => x.TypedCodeHash).IsUnique().HasFilter("status IN ('pending', 'claimed', 'approved')");
        pairings.HasIndex(x => x.ExpiresAt);
        pairings.HasIndex(x => new { x.UserId, x.Status });
        pairings.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        pairings.HasOne<IdentitySession>().WithMany().HasForeignKey(x => x.CreatedSessionId).OnDelete(DeleteBehavior.Cascade);
        pairings.HasOne<UserWallet>().WithMany().HasForeignKey(x => x.SignerWalletId).OnDelete(DeleteBehavior.SetNull);
        var invitations = modelBuilder.Entity<Invitation>();
        invitations.ToTable("invitations", table =>
            table.HasCheckConstraint("ck_invitations_uses", "max_uses > 0 AND uses >= 0 AND uses <= max_uses"));
        invitations.Property(x => x.Note).HasMaxLength(80);
        invitations.HasIndex(x => x.CodeHash).IsUnique();
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }

    public static void Configure(DbContextOptionsBuilder options, string connection, IdentitySchema schema) =>
        options.UseNpgsql(connection, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", schema.Name))
            .ReplaceService<IModelCacheKeyFactory, IdentityModelCacheKeyFactory>()
            .ReplaceService<IMigrationsAssembly, IdentityMigrationsAssembly>();
}

public sealed class IdentityModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => (context.GetType(), ((IdentityDbContext)context).Schema.Name, designTime);
}

public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Vessel");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Set ConnectionStrings__Vessel for migration tooling.");
        var schema = new IdentitySchema(Environment.GetEnvironmentVariable("Vessel__Identity__Schema") ?? "identity");
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContext.Configure(options, connection, schema);
        return new IdentityDbContext(options.Options, schema);
    }
}
