using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Common;

namespace Vessel.Identity;

public sealed class IdentityService(IdentityTransaction transactions, IdentitySettings settings,
    IWalletSignatureVerifier signatures, TimeProvider clock)
{
    public async Task<ChallengeDto> ChallengeAsync(string? address, string origin, string binding, CancellationToken ct)
    {
        var normalized = WalletSignatures.NormalizeAddress(address);
        return await transactions.RunAsync(async db =>
        {
            var now = clock.GetUtcNow();
            var challenge = new AuthChallenge
            {
                Id = Guid.NewGuid(), Address = normalized, Origin = origin, BindingHash = IdentityTokens.Hash(binding),
                IssuedAt = now, ExpiresAt = now.AddMinutes(5)
            };
            challenge.Message = SignInMessage.Create(origin, normalized, SignInMessage.Nonce(), now, challenge.ExpiresAt);
            await AddChallengeAsync(db, challenge, now, ct);
            return new ChallengeDto(challenge.Id, challenge.Message, challenge.ExpiresAt);
        }, ct);
    }

    public async Task<VerificationResult> VerifyAsync(VerifyRequest request, string origin, string? binding,
        string? previousSession, string? userAgent, CancellationToken ct)
    {
        // Even failed signature/binding/expiry checks commit consumption; throwing inside the transaction would undo it.
        var outcome = await transactions.RunAsync<(VerificationResult? Result, ProblemException? Error)>(async db =>
        {
            var now = clock.GetUtcNow();
            var challenge = await FindChallengeAsync(db, request.ChallengeId, ct);
            if (challenge is null || challenge.ConsumedAt is not null)
                return (Result: (VerificationResult?)null, Error: IdentityErrors.Error(400, "challenge-invalid"));
            challenge.ConsumedAt = now;
            try
            {
                if (challenge.Origin != origin) throw IdentityErrors.Error(403, "origin-rejected");
                if (challenge.Purpose != "sign-in" || !IdentityTokens.Matches(challenge.BindingHash, binding))
                    throw IdentityErrors.Error(400, "challenge-invalid");
                if (challenge.ExpiresAt.AddSeconds(30) < now || challenge.IssuedAt.AddSeconds(-30) > now)
                    throw IdentityErrors.Error(400, "challenge-expired");
                signatures.Verify(challenge.Message, challenge.Address, request.Signature);
                var wallet = await FindWalletAsync(db, challenge.Address, ct);
                if (wallet is not null)
                {
                    var user = await IdentityTransaction.LockUserAsync(db, wallet.UserId, ct);
                    if (user is null || user.Status != "active") throw IdentityErrors.Error(403, "user-suspended");
                    wallet.LastUsedAt = now;
                    var issued = await IssueSessionAsync(settings, db, user, wallet, request.Remember, previousSession, userAgent, now, "wallet", ct);
                    return (new VerificationResult("signed-in", issued), (ProblemException?)null);
                }
                CheckRegistrationPolicy(challenge.Address);
                var token = IdentityTokens.Create();
                await db.Registrations.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
                var open = db.Registrations.Where(x => x.ConsumedAt == null);
                var excess = await open.CountAsync(ct) - settings.MaxPendingRegistrations + 1;
                if (excess > 0)
                    await open.Where(x => open.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                        .Take(excess).Select(row => row.Id).Contains(x.Id)).ExecuteDeleteAsync(ct);
                var expires = now.AddMinutes(10);
                db.Registrations.Add(new PendingRegistration
                {
                    Id = Guid.NewGuid(), TokenHash = IdentityTokens.Hash(token), Address = challenge.Address, Origin = origin,
                    BindingHash = challenge.BindingHash!, CreatedAt = now, ExpiresAt = expires
                });
                return (new VerificationResult("registration-required", Registration: token,
                    RequiresInvitation: settings.Registration == "Invitation", ExpiresAt: expires), (ProblemException?)null);
            }
            catch (ProblemException error) { return ((VerificationResult?)null, error); }
        }, ct);
        if (outcome.Error is not null) throw outcome.Error;
        return outcome.Result!;
    }

    public async Task<IssuedSession> RegisterAsync(RegisterRequest request, string origin, string? binding,
        string? previousSession, string? userAgent, CancellationToken ct)
    {
        var name = IdentityText.DisplayName(request.DisplayName);
        // These checks never consume a valid pending registration or invitation.
        if (settings.Registration == "Closed") throw IdentityErrors.Error(403, "registration-closed");
        var invitation = request.Invitation is not null || settings.Registration == "Invitation"
            ? InvitationCodes.Normalize(request.Invitation) : null;
        if (!IdentityTokens.Valid(request.Registration)) throw IdentityErrors.Error(400, "registration-invalid");
        return await transactions.RunAsync(async db =>
        {
            var now = clock.GetUtcNow();
            var hash = IdentityTokens.Hash(request.Registration!);
            var pending = await db.Registrations.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
            if (pending is null || pending.ConsumedAt is not null || pending.ExpiresAt <= now ||
                pending.Origin != origin || !IdentityTokens.Matches(pending.BindingHash, binding))
                throw IdentityErrors.Error(400, "registration-invalid");
            CheckRegistrationPolicy(pending.Address);
            if (await db.Wallets.AnyAsync(x => x.Address == pending.Address, ct))
                throw IdentityErrors.Error(409, "wallet-taken");
            if (settings.Registration == "Invitation")
            {
                var codeHash = IdentityTokens.Hash(invitation!);
                // Only the schema identifier is substituted; IdentitySchema validates it before any SQL is built.
                var sql = $"""
                    UPDATE "{db.Schema.Name}".invitations SET uses = uses + 1
                    WHERE code_hash = @hash AND revoked_at IS NULL AND expires_at > @now AND uses < max_uses
                    RETURNING id AS "Value"
                    """;
                var consumed = await db.Database.SqlQueryRaw<Guid>(sql, new NpgsqlParameter("hash", codeHash), new NpgsqlParameter("now", now))
                    .ToListAsync(ct);
                if (consumed.Count != 1) throw IdentityErrors.Error(403, "invitation-invalid");
            }
            pending.ConsumedAt = now;
            var user = new IdentityUser { Id = Guid.NewGuid(), DisplayName = name, CreatedAt = now, UpdatedAt = now };
            var wallet = new UserWallet { Id = Guid.NewGuid(), UserId = user.Id, Address = pending.Address, AddedAt = now, LastUsedAt = now };
            db.Users.Add(user);
            db.Wallets.Add(wallet);
            return await IssueSessionAsync(settings, db, user, wallet, request.Remember, previousSession, userAgent, now, "wallet", ct);
        }, ct);
    }

    public async Task SignOutAsync(AuthenticatedSession authenticated, Guid expectedSession, CancellationToken ct)
    {
        if (expectedSession != authenticated.Session.Id) throw IdentityErrors.Error(409, "session-changed");
        await transactions.RunAsync(async db =>
        {
            var now = clock.GetUtcNow();
            await db.Sessions.Where(x => x.Id == expectedSession && x.UserId == authenticated.User.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, "sign-out"), ct);
            return true;
        }, ct);
    }

    // Shared with IdentityWarmup, which runs these exact tracked shapes once with values that match nothing.
    internal static Task<AuthChallenge?> FindChallengeAsync(IdentityDbContext db, Guid id, CancellationToken ct) =>
        db.Challenges.SingleOrDefaultAsync(x => x.Id == id, ct);

    internal static Task<UserWallet?> FindWalletAsync(IdentityDbContext db, string address, CancellationToken ct) =>
        db.Wallets.SingleOrDefaultAsync(x => x.Address == address, ct);

    internal static async Task AddChallengeAsync(IdentityDbContext db, AuthChallenge challenge, DateTimeOffset now, CancellationToken ct)
    {
        await db.Challenges.Where(x => x.ExpiresAt < now.AddSeconds(-30)).ExecuteDeleteAsync(ct);
        var open = db.Challenges.Where(x => x.ConsumedAt == null);
        var excess = await open.CountAsync(ct) - 9999;
        if (excess > 0)
            await open.Where(x => open.OrderBy(row => row.IssuedAt).ThenBy(row => row.Id)
                .Take(excess).Select(row => row.Id).Contains(x.Id)).ExecuteDeleteAsync(ct);
        db.Challenges.Add(challenge);
    }

    private void CheckRegistrationPolicy(string address)
    {
        if (settings.Registration == "Closed" ||
            (settings.Registration == "AllowList" && !settings.AllowList.Contains(address, StringComparer.Ordinal)))
            throw IdentityErrors.Error(403, "registration-closed");
    }

    // Shared with device pairing, which issues the same row with source "pairing" and the signer's wallet.
    internal static async Task<IssuedSession> IssueSessionAsync(IdentitySettings settings, IdentityDbContext db, IdentityUser user,
        UserWallet wallet, bool remember, string? previous, string? userAgent, DateTimeOffset now, string source, CancellationToken ct)
    {
        if (IdentityTokens.Valid(previous))
        {
            var hash = IdentityTokens.Hash(previous!);
            await db.Sessions.Where(x => x.TokenHash == hash && x.RevokedAt == null).ExecuteUpdateAsync(update =>
                update.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, "replaced"), ct);
        }
        var live = await db.Sessions.Where(x => x.UserId == user.Id && x.RevokedAt == null &&
            x.IdleExpiresAt > now && x.AbsoluteExpiresAt > now).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        if (live.Count >= settings.MaxSessionsPerUser)
            await db.Sessions.Where(x => live.Take(live.Count - settings.MaxSessionsPerUser + 1).Contains(x.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, "session-cap"), ct);
        var token = IdentityTokens.Create();
        var session = new IdentitySession
        {
            Id = Guid.NewGuid(), UserId = user.Id, WalletId = wallet.Id, TokenHash = IdentityTokens.Hash(token),
            Remember = remember, CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now + settings.IdleLifetime(remember),
            AbsoluteExpiresAt = now + settings.AbsoluteLifetime(remember), UserAgentSummary = IdentityText.UserAgent(userAgent),
            Source = source
        };
        db.Sessions.Add(session);
        return new(token, new AuthenticatedSession(session, user, wallet).ToDto(settings));
    }
}
