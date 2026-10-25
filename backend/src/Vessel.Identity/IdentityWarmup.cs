using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Common;

namespace Vessel.Identity;

/// <summary>
/// Read-only startup warm-up: builds the Identity model and runs the cookie-session and sign-in lookup shapes once and recovers a fixed public signature. Lookups use a never-issued token, a fresh random ID and the zero
/// address, so they match nothing. It never issues or consumes a challenge or session, never saves, and keeps no
/// result, so every request still authenticates against the database.
/// </summary>
public static class IdentityWarmup
{
    // A public test vector for private key 0x…01, a well-known key that must never hold funds. It authorizes nothing:
    // the message is no sign-in message and the address is no registered wallet.
    internal const string VectorAddress = "0x7e5f4552091a69125d5dfcb7b8c2659029395bdf";
    internal const string VectorMessage = "Vessel startup warm-up. A public test vector that authorizes nothing.";
    internal const string VectorSignature = "0xe9f3e2ec03aeced6f1951e1cf28fbd45e0d3a2a742d1880365ef64a7c60778bc" +
        "6b2b0414676a75dee24b76ac85ae4f7fb1cf9255ca1548e24b4c353456ef15f51b";
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    /// <summary>Runs inside a caller-owned scope; the steps are named for the caller's safe failure log.</summary>
    public static async Task RunAsync(IServiceProvider services, Func<string, Func<Task>, Task> step, CancellationToken ct)
    {
        var contexts = services.GetRequiredService<IDbContextFactory<IdentityDbContext>>();
        await step("identity-model", async () =>
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            _ = db.Model;
        });
        await step("identity-sessions", async () =>
        {
            var sessions = services.GetRequiredService<SessionStore>();
            try { await sessions.AuthenticateAsync(IdentityTokens.Create(), refresh: true, ct); }
            catch (ProblemException) { } // The expected session-revoked outcome; database failures propagate.
            _ = await sessions.StillValidAsync(Guid.NewGuid(), ct);
        });
        await step("identity-sign-in", async () =>
        {
            // Tracked like the verify path's own reads; the context is discarded without saving.
            await using var db = await contexts.CreateDbContextAsync(ct);
            _ = await IdentityService.FindChallengeAsync(db, Guid.NewGuid(), ct);
            _ = await IdentityService.FindWalletAsync(db, ZeroAddress, ct);
        });
        await step("identity-signatures", () =>
        {
            services.GetRequiredService<IWalletSignatureVerifier>().Verify(VectorMessage, VectorAddress, VectorSignature);
            var now = DateTimeOffset.UnixEpoch;
            _ = SignInMessage.Create("https://vessel.invalid", VectorAddress, "warmup", now, now.AddMinutes(5));
            return Task.CompletedTask;
        });
    }
}
