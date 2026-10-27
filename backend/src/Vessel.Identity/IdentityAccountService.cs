using Microsoft.EntityFrameworkCore;

namespace Vessel.Identity;

public sealed class IdentityAccountService(IdentityTransaction transactions, IDbContextFactory<IdentityDbContext> factory,
    IWalletSignatureVerifier signatures, TimeProvider clock)
{
    public async Task<IReadOnlyList<SessionRowDto>> SessionsAsync(AuthenticatedSession authenticated, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var rows = await (from session in db.Sessions.AsNoTracking()
                          join wallet in db.Wallets on session.WalletId equals wallet.Id
                          where session.UserId == authenticated.User.Id && wallet.UserId == session.UserId &&
                              session.RevokedAt == null && session.IdleExpiresAt > now && session.AbsoluteExpiresAt > now
                          orderby session.CreatedAt descending, session.Id
                          select new { Session = session, wallet.Address }).ToListAsync(ct);
        return rows.Select(row => new SessionRowDto(row.Session.Id, row.Session.Id == authenticated.Session.Id,
            row.Session.CreatedAt, row.Session.LastSeenAt, row.Session.Remember, row.Session.UserAgentSummary,
            WalletSignatures.Checksum(row.Address), row.Session.Source)).ToArray();
    }

    public Task<bool> RevokeSessionAsync(AuthenticatedSession authenticated, Guid id, CancellationToken ct) =>
        WithSessionAsync(authenticated, async (db, current, now) =>
        {
            var session = await db.Sessions.SingleOrDefaultAsync(x => x.UserId == current.User.Id && x.Id == id, ct)
                ?? throw IdentityErrors.Error(404, "not-found");
            session.RevokedAt ??= now;
            session.RevokeReason ??= session.Id == current.Session.Id ? "sign-out" : "revoked";
            IdentityWalletRemoval.ClearGrant(session);
            return session.Id == current.Session.Id;
        }, ct);

    public Task<bool> RevokeOthersAsync(AuthenticatedSession authenticated, CancellationToken ct) =>
        WithSessionAsync(authenticated, async (db, current, now) =>
        {
            await db.Sessions.Where(x => x.UserId == current.User.Id && x.Id != current.Session.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, "revoked"), ct);
            return true;
        }, ct);

    public Task<UserDto> UpdateProfileAsync(AuthenticatedSession authenticated, ProfileRequest request, CancellationToken ct)
    {
        var name = IdentityText.DisplayName(request.DisplayName);
        return WithSessionAsync(authenticated, (_, current, now) =>
        {
            if (current.User.Revision != request.ExpectedRevision) throw IdentityErrors.Error(409, "revision-conflict");
            current.User.DisplayName = name;
            current.User.UpdatedAt = now;
            current.User.Revision++;
            return Task.FromResult(new UserDto(current.User.Id, name, current.User.Role, current.User.Revision));
        }, ct);
    }

    public async Task<IReadOnlyList<WalletDto>> WalletsAsync(AuthenticatedSession authenticated, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wallets = await db.Wallets.AsNoTracking().Where(x => x.UserId == authenticated.User.Id)
            .OrderBy(x => x.AddedAt).ThenBy(x => x.Id).ToListAsync(ct);
        return wallets.Select(wallet => Wallet(wallet, authenticated.Session.WalletId)).ToArray();
    }

    public Task<WalletDto> UpdateWalletAsync(AuthenticatedSession authenticated, Guid id, WalletLabelRequest request, CancellationToken ct) =>
        WithSessionAsync(authenticated, async (db, current, _) =>
        {
            var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == current.User.Id && x.Id == id, ct)
                ?? throw IdentityErrors.Error(404, "not-found");
            wallet.Label = IdentityText.Label(request.Label);
            return Wallet(wallet, current.Session.WalletId);
        }, ct);

    public Task<ChallengeDto> StepUpChallengeAsync(AuthenticatedSession authenticated, StepUpChallengeRequest request,
        string origin, CancellationToken ct)
    {
        var address = WalletSignatures.NormalizeAddress(request.Address);
        if (request.Action is not ("wallet.link" or "wallet.remove" or "device.pair") ||
            (request.Action == "wallet.remove" ? request.Target is null || request.Target == Guid.Empty : request.Target is not null))
            throw IdentityErrors.Error(400, "request-invalid");
        return WithSessionAsync(authenticated, async (db, current, now) =>
        {
            if (!await db.Wallets.AnyAsync(x => x.UserId == current.User.Id && x.Address == address, ct))
                throw IdentityErrors.Error(400, "address-invalid");
            UserWallet? target = null;
            if (request.Target is { } targetId)
                target = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == current.User.Id && x.Id == targetId, ct)
                    ?? throw IdentityErrors.Error(404, "not-found");
            return await ChallengeAsync(db, current, address, origin, "step-up", request.Action,
                target?.Id.ToString(), target?.Address, now, ct);
        }, ct);
    }

    public Task<bool> StepUpAsync(AuthenticatedSession authenticated, StepUpRequest request, string origin, CancellationToken ct) =>
        WithSessionAsync(authenticated, async (db, current, now) =>
        {
            var challenge = await ChallengeAsync(db, current, request.ChallengeId, origin, "step-up", now, ct);
            if (challenge.Action is not ("wallet.link" or "wallet.remove" or "device.pair") ||
                (challenge.Action == "wallet.remove" ? challenge.Target is null : challenge.Target is not null))
                throw IdentityErrors.Error(400, "challenge-invalid");
            var signer = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == current.User.Id && x.Address == challenge.Address, ct)
                ?? throw IdentityErrors.Error(400, "address-invalid");
            if (challenge.Action == "wallet.remove" && (!Guid.TryParse(challenge.Target, out var target) ||
                !await db.Wallets.AnyAsync(x => x.UserId == current.User.Id && x.Id == target, ct)))
                throw IdentityErrors.Error(404, "not-found");
            signatures.Verify(challenge.Message, challenge.Address, request.Signature);
            challenge.ConsumedAt = now;
            current.Session.StepUpAction = challenge.Action;
            current.Session.StepUpTarget = challenge.Target;
            current.Session.StepUpWalletId = signer.Id;
            current.Session.StepUpExpiresAt = now.AddMinutes(5);
            return true;
        }, ct);

    public Task<ChallengeDto> WalletChallengeAsync(AuthenticatedSession authenticated, ChallengeRequest request, string origin, CancellationToken ct)
    {
        var address = WalletSignatures.NormalizeAddress(request.Address);
        return WithSessionAsync(authenticated, async (db, current, now) =>
        {
            await IdentityLimits.RequireWalletCapacityAsync(db, current.User.Id, ct);
            await RequireGrantAsync(db, current, "wallet.link", null, now, ct);
            // No lookup of the new address: ownership is disclosed only after its valid signature.
            return await ChallengeAsync(db, current, address, origin, "link", "wallet.link", null, null, now, ct);
        }, ct);
    }

    public Task<WalletDto> LinkWalletAsync(AuthenticatedSession authenticated, LinkWalletRequest request, string origin, CancellationToken ct)
    {
        var label = IdentityText.Label(request.Label);
        return WithSessionAsync(authenticated, async (db, current, now) =>
        {
            var challenge = await ChallengeAsync(db, current, request.ChallengeId, origin, "link", now, ct);
            if (challenge.Action != "wallet.link" || challenge.Target is not null) throw IdentityErrors.Error(400, "challenge-invalid");
            await IdentityLimits.RequireWalletCapacityAsync(db, current.User.Id, ct);
            await RequireGrantAsync(db, current, "wallet.link", null, now, ct);
            signatures.Verify(challenge.Message, challenge.Address, request.Signature);
            var existing = await db.Wallets.SingleOrDefaultAsync(x => x.Address == challenge.Address, ct);
            if (existing is not null)
                throw IdentityErrors.Error(409, existing.UserId == current.User.Id ? "wallet-already-linked" : "wallet-taken");
            var wallet = new UserWallet { Id = Guid.NewGuid(), UserId = current.User.Id, Address = challenge.Address,
                Label = label, AddedAt = now };
            db.Wallets.Add(wallet);
            challenge.ConsumedAt = now;
            IdentityWalletRemoval.ClearGrant(current.Session);
            return Wallet(wallet, current.Session.WalletId);
        }, ct);
    }

    public Task<bool> RemoveWalletAsync(AuthenticatedSession authenticated, Guid id, CancellationToken ct) =>
        WithSessionAsync(authenticated, async (db, current, now) =>
        {
            var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == current.User.Id && x.Id == id, ct)
                ?? throw IdentityErrors.Error(404, "not-found");
            await IdentityWalletRemoval.RequireRemovableAsync(db, wallet, ct);
            await RequireGrantAsync(db, current, "wallet.remove", id.ToString(), now, ct);
            await IdentityWalletRemoval.RemoveAsync(db, wallet, now, ct);
            IdentityWalletRemoval.ClearGrant(current.Session);
            return current.Session.WalletId == id;
        }, ct);

    private Task<T> WithSessionAsync<T>(AuthenticatedSession authenticated,
        Func<IdentityDbContext, AuthenticatedSession, DateTimeOffset, Task<T>> action, CancellationToken ct) =>
        WithSessionAsync(transactions, clock, authenticated, action, ct);

    // One identity transaction under the user's row lock, with the caller's session and wallet re-read inside it.
    internal static async Task<T> WithSessionAsync<T>(IdentityTransaction transactions, TimeProvider clock,
        AuthenticatedSession authenticated, Func<IdentityDbContext, AuthenticatedSession, DateTimeOffset, Task<T>> action,
        CancellationToken ct) =>
        await transactions.RunAsync(async db =>
        {
            var user = await IdentityTransaction.LockUserAsync(db, authenticated.User.Id, ct)
                ?? throw IdentityErrors.Error(401, "session-revoked");
            if (user.Status != "active") throw IdentityErrors.Error(403, "user-suspended");
            var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == authenticated.Session.Id && x.UserId == user.Id, ct);
            if (session is null || session.RevokedAt is not null) throw IdentityErrors.Error(401, "session-revoked");
            var now = clock.GetUtcNow();
            if (session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now) throw IdentityErrors.Error(401, "session-expired");
            var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == user.Id && x.Id == session.WalletId, ct)
                ?? throw IdentityErrors.Error(401, "session-revoked");
            return await action(db, new(session, user, wallet), now);
        }, ct);

    internal static async Task RequireGrantAsync(IdentityDbContext db, AuthenticatedSession current, string action,
        string? target, DateTimeOffset now, CancellationToken ct)
    {
        var session = current.Session;
        if (session.StepUpAction != action || session.StepUpTarget != target || session.StepUpExpiresAt is null ||
            session.StepUpExpiresAt <= now || !await db.Wallets.AnyAsync(x => x.UserId == current.User.Id && x.Id == session.StepUpWalletId, ct))
            throw IdentityErrors.StepUpRequired(action);
    }

    private static async Task<AuthChallenge> ChallengeAsync(IdentityDbContext db, AuthenticatedSession current, Guid id,
        string origin, string purpose, DateTimeOffset now, CancellationToken ct)
    {
        var challenge = await db.Challenges.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (challenge is null || challenge.ConsumedAt is not null || challenge.Purpose != purpose ||
            challenge.UserId != current.User.Id || challenge.SessionId != current.Session.Id)
            throw IdentityErrors.Error(400, "challenge-invalid");
        if (challenge.Origin != origin) throw IdentityErrors.Error(403, "origin-rejected");
        if (challenge.ExpiresAt.AddSeconds(30) < now || challenge.IssuedAt.AddSeconds(-30) > now)
            throw IdentityErrors.Error(400, "challenge-expired");
        return challenge;
    }

    private static async Task<ChallengeDto> ChallengeAsync(IdentityDbContext db, AuthenticatedSession current, string address,
        string origin, string purpose, string? action, string? target, string? targetAddress, DateTimeOffset now, CancellationToken ct)
    {
        var challenge = new AuthChallenge { Id = Guid.NewGuid(), UserId = current.User.Id, SessionId = current.Session.Id,
            Address = address, Origin = origin, Purpose = purpose, Action = action, Target = target, IssuedAt = now, ExpiresAt = now.AddMinutes(5) };
        challenge.Message = SignInMessage.Create(origin, address, SignInMessage.Nonce(), now, challenge.ExpiresAt,
            purpose, action, current.Wallet.Address, targetAddress);
        await IdentityService.AddChallengeAsync(db, challenge, now, ct);
        return new(challenge.Id, challenge.Message, challenge.ExpiresAt);
    }

    private static WalletDto Wallet(UserWallet wallet, Guid? currentWallet) => new(wallet.Id, WalletSignatures.Checksum(wallet.Address),
        wallet.Label, wallet.AddedAt, wallet.LastUsedAt, wallet.Id == currentWallet);
}

internal static class IdentityWalletRemoval
{
    // Both callers hold the user's row lock in the identity transaction.
    internal static async Task RequireRemovableAsync(IdentityDbContext db, UserWallet wallet, CancellationToken ct)
    {
        // Keeping every user's last wallet also keeps an active operator's last sign-in wallet.
        if (await db.Wallets.CountAsync(x => x.UserId == wallet.UserId, ct) <= 1) throw IdentityErrors.Error(409, "last-wallet");
    }

    internal static async Task RemoveAsync(IdentityDbContext db, UserWallet wallet, DateTimeOffset now, CancellationToken ct)
    {
        await RequireRemovableAsync(db, wallet, ct);
        // An open pairing names this user's signer wallet; cancel them all (as revoked sessions are) before the wallet goes.
        await DevicePairings.CancelOpenAsync(db, wallet.UserId, ct);
        await db.Sessions.Where(x => x.UserId == wallet.UserId && x.StepUpWalletId == wallet.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.StepUpAction, (string?)null).SetProperty(x => x.StepUpTarget, (string?)null)
            .SetProperty(x => x.StepUpWalletId, (Guid?)null).SetProperty(x => x.StepUpExpiresAt, (DateTimeOffset?)null), ct);
        await db.Sessions.Where(x => x.UserId == wallet.UserId && x.WalletId == wallet.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, "wallet-removed"), ct);
        await db.Wallets.Where(x => x.UserId == wallet.UserId && x.Id == wallet.Id).ExecuteDeleteAsync(ct);
    }

    internal static void ClearGrant(IdentitySession session)
    {
        session.StepUpAction = null;
        session.StepUpTarget = null;
        session.StepUpWalletId = null;
        session.StepUpExpiresAt = null;
    }
}
