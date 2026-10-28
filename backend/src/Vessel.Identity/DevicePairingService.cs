using System.Linq.Expressions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;

namespace Vessel.Identity;

/// <summary>Shared pairing constants and the bulk operations other identity flows (wallet removal, suspension) reuse.</summary>
internal static class DevicePairings
{
    internal static readonly string[] Open = ["pending", "claimed", "approved"];
    internal const int MaxUnfinished = 1000;
    internal const int MaxCodeAttempts = 3;
    /// <summary>Every state's deadline, measured from its own transition.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    internal static bool IsOpen(DevicePairing pairing) => Open.Contains(pairing.Status);

    // Callers hold the identity write lock. A null user cancels every user's open pairings.
    internal static Task<int> CancelOpenAsync(IdentityDbContext db, Guid? userId, CancellationToken ct) =>
        db.Pairings.Where(x => (userId == null || x.UserId == userId) && Open.Contains(x.Status))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "cancelled"), ct);

    // Callers hold the identity write lock. Ending a session (sign-out, revocation) cancels the pairings it started.
    internal static Task<int> CancelOpenAsync(IdentityDbContext db, Expression<Func<DevicePairing, bool>> created, CancellationToken ct) =>
        db.Pairings.Where(created).Where(x => Open.Contains(x.Status))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "cancelled"), ct);

    // The 6-digit code is salted by the pairing so equal codes never share a hash.
    internal static byte[] CodeHash(Guid pairing, string code) => IdentityTokens.Hash(pairing.ToString("N") + ":" + code);
}

public static class PairingCodes
{
    /// <summary>Ten Crockford base32 characters (50 bits), stored and compared without the display dash.</summary>
    public static string CreateTyped()
    {
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++) chars[i] = InvitationCodes.Alphabet[RandomNumberGenerator.GetInt32(InvitationCodes.Alphabet.Length)];
        return new string(chars);
    }

    public static string Display(string typed) => $"{typed[..5]}-{typed[5..]}";

    /// <summary>Folds a typed code like an invitation (dashes, spaces, case, I/L as 1, O as 0); null when it cannot be a code.</summary>
    public static string? NormalizeTyped(string? text)
    {
        if (text is null || text.Length > 64) return null;
        var folded = InvitationCodes.Fold(text);
        return folded.Length == 10 && folded.All(c => InvitationCodes.Alphabet.Contains(c)) ? folded : null;
    }

    public static string CreateConfirmation() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The six digits the desktop user types; spaces from the phone's "482 913" display are ignored.</summary>
    public static string? NormalizeConfirmation(string? text)
    {
        if (text is null || text.Length > 32) return null;
        var digits = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return digits.Length == 6 && digits.All(char.IsAsciiDigit) ? digits : null;
    }
}

/// <summary>
/// Connecting a phone without a wallet (device-pairing-plan.md): a signed-in desktop creates a short-lived pairing, the
/// phone claims it, the desktop approves the phone's confirmation code and the phone completes into its own session.
/// Every transition runs in one identity transaction. Only SHA-256 hashes of the secrets are stored; no code is logged.
/// </summary>
public sealed class DevicePairingService(IdentityTransaction transactions, IDbContextFactory<IdentityDbContext> factory,
    IdentitySettings settings, TimeProvider clock)
{
    public Task<PairingCreatedDto> CreateAsync(AuthenticatedSession authenticated, string origin, CancellationToken ct) =>
        IdentityAccountService.WithSessionAsync(transactions, clock, authenticated, async (db, current, now) =>
        {
            await IdentityAccountService.RequireGrantAsync(db, current, "device.pair", null, now, ct);
            var signer = current.Session.StepUpWalletId!.Value;
            // The grant is spent by this pairing, atomically with its creation.
            IdentityWalletRemoval.ClearGrant(current.Session);
            // Expired rows stop counting and free their typed-code slot.
            await db.Pairings.Where(x => DevicePairings.Open.Contains(x.Status) && x.ExpiresAt <= now)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "expired"), ct);
            await DevicePairings.CancelOpenAsync(db, current.User.Id, ct);
            var open = db.Pairings.Where(x => DevicePairings.Open.Contains(x.Status));
            var excess = await open.CountAsync(ct) - DevicePairings.MaxUnfinished + 1;
            if (excess > 0)
                await open.Where(x => open.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                    .Take(excess).Select(row => row.Id).Contains(x.Id))
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "cancelled"), ct);
            string typed;
            byte[] typedHash;
            do
            {
                typed = PairingCodes.CreateTyped();
                typedHash = IdentityTokens.Hash(typed);
            } while (await db.Pairings.AnyAsync(x => x.TypedCodeHash == typedHash && DevicePairings.Open.Contains(x.Status), ct));
            var secret = IdentityTokens.Create();
            var pairing = new DevicePairing
            {
                Id = Guid.NewGuid(), UserId = current.User.Id, CreatedSessionId = current.Session.Id, SignerWalletId = signer,
                SecretHash = IdentityTokens.Hash(secret), TypedCodeHash = typedHash, Origin = origin, Status = "pending",
                CreatedAt = now, ExpiresAt = now + DevicePairings.Window
            };
            db.Pairings.Add(pairing);
            return new PairingCreatedDto(pairing.Id, secret, PairingCodes.Display(typed), pairing.ExpiresAt);
        }, ct);

    // Polled every 2 s by the desktop: a plain read, since the session was already validated for this request.
    public async Task<PairingStatusDto> StatusAsync(AuthenticatedSession authenticated, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var pairing = await db.Pairings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
            x.UserId == authenticated.User.Id && x.CreatedSessionId == authenticated.Session.Id, ct)
            ?? throw IdentityErrors.Error(404, "not-found");
        return new(Effective(pairing, clock.GetUtcNow()), pairing.ExpiresAt, pairing.DeviceSummary);
    }

    public async Task ApproveAsync(AuthenticatedSession authenticated, Guid id, ApprovePairingRequest request, CancellationToken ct)
    {
        // A wrong code counts, and the count must commit; only the final answer is thrown afterwards.
        var error = await IdentityAccountService.WithSessionAsync(transactions, clock, authenticated, async (db, current, now) =>
        {
            var pairing = await db.Pairings.SingleOrDefaultAsync(x => x.Id == id && x.UserId == current.User.Id &&
                x.CreatedSessionId == current.Session.Id, ct) ?? throw IdentityErrors.Error(404, "not-found");
            if (DevicePairings.IsOpen(pairing) && pairing.ExpiresAt <= now) throw IdentityErrors.Error(400, "pairing-expired");
            if (pairing.Status != "claimed") throw IdentityErrors.Error(400, "pairing-invalid");
            if (!await db.Wallets.AnyAsync(x => x.UserId == current.User.Id && x.Id == pairing.SignerWalletId, ct))
                throw IdentityErrors.Error(400, "pairing-invalid");
            var code = PairingCodes.NormalizeConfirmation(request.Code) ?? throw IdentityErrors.Error(400, "request-invalid");
            if (CryptographicOperations.FixedTimeEquals(pairing.CodeHash!, DevicePairings.CodeHash(pairing.Id, code)))
            {
                pairing.Status = "approved";
                pairing.ApprovedAt = now;
                pairing.ExpiresAt = now + DevicePairings.Window;
                return (ProblemException?)null;
            }
            pairing.CodeAttempts++;
            var left = Math.Max(0, DevicePairings.MaxCodeAttempts - pairing.CodeAttempts);
            if (left == 0) pairing.Status = "cancelled";
            var wrong = IdentityErrors.Error(400, "pairing-code-invalid");
            wrong.Data["attemptsLeft"] = left;
            return wrong;
        }, ct);
        if (error is not null) throw error;
    }

    public Task<bool> CancelAsync(AuthenticatedSession authenticated, Guid id, CancellationToken ct) =>
        IdentityAccountService.WithSessionAsync(transactions, clock, authenticated, async (db, current, _) =>
        {
            var pairing = await db.Pairings.SingleOrDefaultAsync(x => x.Id == id && x.UserId == current.User.Id &&
                x.CreatedSessionId == current.Session.Id, ct) ?? throw IdentityErrors.Error(404, "not-found");
            if (DevicePairings.IsOpen(pairing)) pairing.Status = "cancelled";
            return true;
        }, ct);

    /// <summary>The phone claims by the QR's secret or by the typed code; <paramref name="binding"/> is its sign-in binding cookie value.</summary>
    public async Task<PairingClaimedDto> ClaimAsync(ClaimPairingRequest request, string origin, string binding, string? userAgent, CancellationToken ct)
    {
        if ((request.Secret is null) == (request.TypedCode is null)) throw IdentityErrors.Error(400, "request-invalid");
        var typed = request.TypedCode is not null;
        byte[] hash;
        if (typed) hash = IdentityTokens.Hash(PairingCodes.NormalizeTyped(request.TypedCode) ?? throw IdentityErrors.Error(400, "pairing-invalid"));
        else if (IdentityTokens.Valid(request.Secret)) hash = IdentityTokens.Hash(request.Secret!);
        else throw IdentityErrors.Error(400, "pairing-invalid");
        // Contesting must commit, so refusals are returned and thrown after the transaction.
        var outcome = await transactions.RunAsync<(PairingClaimedDto? Result, ProblemException? Error)>(async db =>
        {
            var now = clock.GetUtcNow();
            var pairing = typed
                ? await db.Pairings.SingleOrDefaultAsync(x => x.TypedCodeHash == hash && DevicePairings.Open.Contains(x.Status), ct)
                : await db.Pairings.SingleOrDefaultAsync(x => x.SecretHash == hash, ct);
            if (pairing is null || pairing.Origin != origin) return (null, IdentityErrors.Error(400, "pairing-invalid"));
            if (DevicePairings.IsOpen(pairing) && pairing.ExpiresAt <= now) return (null, IdentityErrors.Error(400, "pairing-expired"));
            // Someone else holds the scan: neither phone gets in. A typed code never contests, so a blind guess cannot cancel a claim.
            if (pairing.Status == "claimed" && !typed)
            {
                pairing.Status = "contested";
                return (null, IdentityErrors.Error(409, "pairing-contested"));
            }
            if (pairing.Status != "pending") return (null, IdentityErrors.Error(400, "pairing-invalid"));
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pairing.UserId, ct);
            var wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == pairing.UserId && x.Id == pairing.SignerWalletId, ct);
            if (user is not { Status: "active" } || wallet is null) return (null, IdentityErrors.Error(400, "pairing-invalid"));
            var code = PairingCodes.CreateConfirmation();
            pairing.Status = "claimed";
            pairing.ClaimedAt = now;
            pairing.ExpiresAt = now + DevicePairings.Window;
            pairing.BindingHash = IdentityTokens.Hash(binding);
            pairing.CodeHash = DevicePairings.CodeHash(pairing.Id, code);
            pairing.DeviceSummary = IdentityText.UserAgent(userAgent);
            return (new PairingClaimedDto(code, pairing.ExpiresAt, new(user.DisplayName, WalletSignatures.Checksum(wallet.Address))), null);
        }, ct);
        if (outcome.Error is not null) throw outcome.Error;
        return outcome.Result!;
    }

    /// <summary>Polled by the claiming phone. Once approved, issues its own session and revokes any session it sent.</summary>
    public async Task<PairingCompletionResult> CompleteAsync(CompletePairingRequest request, string? binding, string? previousSession,
        string? userAgent, CancellationToken ct)
    {
        if (!IdentityTokens.Valid(binding)) throw IdentityErrors.Error(400, "pairing-invalid");
        var bindingHash = IdentityTokens.Hash(binding!);
        return await transactions.RunAsync(async db =>
        {
            var now = clock.GetUtcNow();
            // The newest claim of this browser; an older one it left behind does not answer for it.
            var pairing = await db.Pairings.Where(x => x.BindingHash == bindingHash).OrderByDescending(x => x.ClaimedAt)
                .FirstOrDefaultAsync(ct);
            if (pairing is null || pairing.Status == "completed") throw IdentityErrors.Error(400, "pairing-invalid");
            var status = Effective(pairing, now);
            if (status != "approved") return new PairingCompletionResult(status == "claimed" ? "waiting" : status);
            var user = await IdentityTransaction.LockUserAsync(db, pairing.UserId, ct);
            var wallet = pairing.SignerWalletId is { } signer
                ? await db.Wallets.SingleOrDefaultAsync(x => x.UserId == pairing.UserId && x.Id == signer, ct) : null;
            // The approving desktop must still be signed in: a revoked or ended session's approval grants nothing.
            var creator = await db.Sessions.AnyAsync(x => x.Id == pairing.CreatedSessionId && x.UserId == pairing.UserId &&
                x.RevokedAt == null && x.IdleExpiresAt > now && x.AbsoluteExpiresAt > now, ct);
            if (user is not { Status: "active" } || wallet is null || !creator)
            {
                pairing.Status = "cancelled";
                return new PairingCompletionResult("cancelled");
            }
            pairing.Status = "completed";
            pairing.CompletedAt = now;
            wallet.LastUsedAt = now;
            var issued = await IdentityService.IssueSessionAsync(settings, db, user, wallet, request.Remember, previousSession,
                userAgent, now, "pairing", ct);
            return new PairingCompletionResult("signed-in", issued);
        }, ct);
    }

    // An unfinished pairing past its current deadline is expired even while the row still holds its earlier status.
    private static string Effective(DevicePairing pairing, DateTimeOffset now) =>
        DevicePairings.IsOpen(pairing) && pairing.ExpiresAt <= now ? "expired" : pairing.Status;
}
