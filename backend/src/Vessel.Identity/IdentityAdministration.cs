using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vessel.Application.Common;

namespace Vessel.Identity;

public static partial class IdentityCommands
{
    private static async Task<int> RunAdministrationAsync(string command, Dictionary<string, string> options, List<Guid> keepActive,
        IConfiguration configuration, IDbContextFactory<IdentityDbContext> factory, IdentityTransaction transactions,
        TextWriter output, IdentityVerification? verify, CancellationToken ct)
    {
        var id = Guid.Empty;
        if ((options.TryGetValue("--user", out var raw) || options.TryGetValue("--wallet", out raw)) &&
            (!Guid.TryParse(raw, out id) || id == Guid.Empty))
        { await output.WriteLineAsync(Usage); return 2; }
        if (command == "verify")
        {
            if (verify is null) throw new InvalidOperationException("identity verify requires API composition.");
            await using var db = await factory.CreateDbContextAsync(ct);
            var users = await db.Users.AsNoTracking().OrderBy(x => x.Id)
                .Select(user => new IdentityVerificationUser(user.Id, db.Wallets.Any(wallet => wallet.UserId == user.Id))).ToListAsync(ct);
            return await verify(configuration, users, output, ct);
        }
        if (command == "reset-after-restore")
        {
            var reset = await transactions.RunAsync(async db =>
            {
                var users = await db.Users.OrderBy(x => x.Id).ToListAsync(ct);
                if (keepActive.Any(userId => users.All(user => user.Id != userId))) return false;
                var now = DateTimeOffset.UtcNow;
                foreach (var user in users)
                {
                    await IdentityTransaction.LockUserAsync(db, user.Id, ct);
                    // Exemption is not reactivation: only an explicit restore may reactivate a suspended user.
                    if (!keepActive.Contains(user.Id) && user.Status != "suspended")
                    { user.Status = "suspended"; user.SuspendedAt = now; user.UpdatedAt = now; user.Revision++; }
                }
                await RevokeSessionsAsync(db, null, "reset-after-restore", now, ct);
                await db.Sessions.ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.StepUpAction, (string?)null).SetProperty(x => x.StepUpTarget, (string?)null)
                    .SetProperty(x => x.StepUpWalletId, (Guid?)null).SetProperty(x => x.StepUpExpiresAt, (DateTimeOffset?)null), ct);
                await db.Challenges.ExecuteDeleteAsync(ct);
                await db.Registrations.ExecuteDeleteAsync(ct);
                await db.Pairings.ExecuteDeleteAsync(ct);
                await db.Invitations.Where(x => x.RevokedAt == null && x.ExpiresAt > now && x.Uses < x.MaxUses)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
                return true;
            }, ct);
            if (!reset) { await output.WriteLineAsync("A --keep-active user does not exist; nothing changed."); return 3; }
            await output.WriteLineAsync("Reset complete. Keep all hosts stopped until reconciliation against trusted post-backup records is complete.");
            await using var report = await factory.CreateDbContextAsync(ct);
            if (!await HasActiveOperatorWithWalletAsync(report, null, ct))
                await output.WriteLineAsync("WARNING: No active operator with a linked wallet remains. Restore operator access before starting hosts.");
            var wallets = await report.Wallets.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
            await output.WriteLineAsync("USER ID | ROLE | STATUS | WALLET ID | ADDRESS");
            foreach (var user in await report.Users.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct))
            {
                await output.WriteLineAsync($"{user.Id} | {user.Role} | {user.Status}");
                foreach (var wallet in wallets.Where(x => x.UserId == user.Id))
                    await output.WriteLineAsync($"  {wallet.Id} | {WalletSignatures.Checksum(wallet.Address)}");
            }
            return 0;
        }
        try
        {
            var changed = await transactions.RunAsync(async db =>
            {
                var now = DateTimeOffset.UtcNow;
                if (command == "revoke-sessions" && options.ContainsKey("--all"))
                    return await RevokeSessionsAsync(db, null, "operator", now, ct);
                if (command == "unbind-wallet")
                {
                    var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (wallet is null) throw IdentityErrors.Error(404, "not-found");
                    await IdentityTransaction.LockUserAsync(db, wallet.UserId, ct);
                    await IdentityWalletRemoval.RemoveAsync(db, wallet, now, ct);
                    return 1;
                }
                var user = await IdentityTransaction.LockUserAsync(db, id, ct) ?? throw IdentityErrors.Error(404, "not-found");
                switch (command)
                {
                    case "revoke-sessions":
                        return await RevokeSessionsAsync(db, id, "operator", now, ct);
                    case "suspend":
                        if (await IsLastActiveOperatorWithWalletAsync(db, user, ct))
                        { await output.WriteLineAsync("Suspension would leave no active operator with a linked wallet."); return -1; }
                        await RevokeSessionsAsync(db, id, "suspended", now, ct);
                        if (user.Status == "suspended") return 0;
                        user.Status = "suspended"; user.SuspendedAt = now;
                        break;
                    case "restore":
                        if (user.Status == "active") return 0;
                        user.Status = "active"; user.SuspendedAt = null;
                        break;
                    case "promote":
                        if (user.Role == "operator") return 0;
                        user.Role = "operator";
                        break;
                    case "demote":
                        if (user.Role != "operator") return 0;
                        if (await IsLastActiveOperatorWithWalletAsync(db, user, ct))
                        { await output.WriteLineAsync("The last active operator with a linked wallet cannot be demoted."); return -1; }
                        user.Role = "member";
                        break;
                }
                user.UpdatedAt = now;
                user.Revision++;
                return 1;
            }, ct);
            if (changed < 0) return 3;
            await output.WriteLineAsync($"{command}: {changed} affected" + (id == Guid.Empty ? "." : $"; ID {id}."));
            return 0;
        }
        catch (ProblemException error) when (error.Code is "not-found" or "last-wallet")
        {
            await output.WriteLineAsync(error.Code == "last-wallet" ? "The last wallet cannot be removed." : "The identity record does not exist.");
            return 3;
        }
    }

    // Only a user who is one of the active operators with a wallet can remove the last of them; members and
    // wallet-less operators can always be suspended or demoted, even while no such operator exists.
    private static async Task<bool> IsLastActiveOperatorWithWalletAsync(IdentityDbContext db, IdentityUser user, CancellationToken ct) =>
        user.Status == "active" && user.Role == "operator" && await db.Wallets.AnyAsync(wallet => wallet.UserId == user.Id, ct) &&
        !await HasActiveOperatorWithWalletAsync(db, user.Id, ct);

    private static Task<bool> HasActiveOperatorWithWalletAsync(IdentityDbContext db, Guid? excludingUser, CancellationToken ct) =>
        db.Users.AnyAsync(user => (excludingUser == null || user.Id != excludingUser) &&
            user.Role == "operator" && user.Status == "active" && db.Wallets.Any(wallet => wallet.UserId == user.Id), ct);

    private static async Task<int> RevokeSessionsAsync(IdentityDbContext db, Guid? userId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        // Suspension and revoke-sessions also end any phone being connected, in the same transaction.
        await DevicePairings.CancelOpenAsync(db, userId, ct);
        return await db.Sessions.Where(x => (userId == null || x.UserId == userId) && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, reason)
                .SetProperty(x => x.StepUpAction, (string?)null).SetProperty(x => x.StepUpTarget, (string?)null)
                .SetProperty(x => x.StepUpWalletId, (Guid?)null).SetProperty(x => x.StepUpExpiresAt, (DateTimeOffset?)null), ct);
    }
}
