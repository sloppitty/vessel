using Microsoft.EntityFrameworkCore;

namespace Vessel.Identity;

public sealed class SessionStore(Func<IDbContextFactory<IdentityDbContext>> factory, IdentitySettings settings, TimeProvider clock)
{
    public async Task<AuthenticatedSession> AuthenticateAsync(string? token, bool refresh, CancellationToken ct)
    {
        if (!IdentityTokens.Valid(token)) throw IdentityErrors.Error(401, "session-required");
        await using var db = await factory().CreateDbContextAsync(ct);
        var hash = IdentityTokens.Hash(token!);
        var result = Check(await Read(db, db.Sessions.Where(x => x.TokenHash == hash)).SingleOrDefaultAsync(ct));
        var session = result.Session;
        if (refresh)
        {
            var now = clock.GetUtcNow();
            // LastSeenAt only ever moves forward to a refresh time, so a row just read as recent cannot match the
            // conditional UPDATE: skip that write. When a refresh is due, the UPDATE's predicates still settle races.
            if (session.LastSeenAt >= now.AddMinutes(-5)) return result;
            var until = now + settings.IdleLifetime(session.Remember);
            if (until > session.AbsoluteExpiresAt) until = session.AbsoluteExpiresAt;
            var changed = await db.Sessions.Where(x => x.Id == session.Id && x.LastSeenAt < now.AddMinutes(-5) &&
                    x.RevokedAt == null && x.IdleExpiresAt > now && x.AbsoluteExpiresAt > now)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastSeenAt, now).SetProperty(x => x.IdleExpiresAt, until), ct);
            if (changed > 0) { session.LastSeenAt = now; session.IdleExpiresAt = until; }
        }
        return result;
    }

    public async Task<bool> StillValidAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await using var db = await factory().CreateDbContextAsync(ct);
            _ = Check(await Read(db, db.Sessions.Where(x => x.Id == sessionId)).SingleOrDefaultAsync(ct));
            return true;
        }
        catch (Exception) { return false; } // An outage terminates a stream rather than extending access.
    }

    // Session, user and wallet in one statement; a missing user or wallet stays null and Check refuses it.
    private static IQueryable<SessionRow> Read(IdentityDbContext db, IQueryable<IdentitySession> sessions) =>
        from session in sessions.AsNoTracking()
        from user in db.Users.AsNoTracking().Where(user => user.Id == session.UserId).DefaultIfEmpty()
        from wallet in db.Wallets.AsNoTracking()
            .Where(wallet => wallet.Id == session.WalletId && wallet.UserId == session.UserId).DefaultIfEmpty()
        select new SessionRow(session, user, wallet);

    private AuthenticatedSession Check(SessionRow? row)
    {
        if (row is null) throw IdentityErrors.Error(401, "session-revoked");
        var (session, user, wallet) = row;
        var now = clock.GetUtcNow();
        if (session.RevokedAt is not null) throw IdentityErrors.Error(401, "session-revoked");
        if (session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now) throw IdentityErrors.Error(401, "session-expired");
        if (user is null) throw IdentityErrors.Error(401, "session-revoked");
        if (user.Status != "active") throw IdentityErrors.Error(403, "user-suspended");
        if (wallet is null) throw IdentityErrors.Error(401, "session-revoked");
        return new(session, user, wallet);
    }

    private sealed record SessionRow(IdentitySession Session, IdentityUser? User, UserWallet? Wallet);
}
