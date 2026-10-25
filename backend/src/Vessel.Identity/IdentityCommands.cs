using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vessel.Application.Common;

namespace Vessel.Identity;

public delegate Task<int> IdentityVerification(IConfiguration configuration, IReadOnlyList<IdentityVerificationUser> users,
    TextWriter output, CancellationToken cancellationToken);

public static partial class IdentityCommands
{
    private const string Usage = """
        Usage: dotnet Vessel.Api.dll identity ensure-owner
               dotnet Vessel.Api.dll identity bind-wallet --user <guid> --address 0x… [--label <label>]
               dotnet Vessel.Api.dll identity invite [--note <note>] [--expires 7d] [--max-uses 1]
               dotnet Vessel.Api.dll identity unbind-wallet --wallet <guid>
               dotnet Vessel.Api.dll identity suspend|restore|promote|demote --user <guid>
               dotnet Vessel.Api.dll identity revoke-sessions --user <guid> | --all
               dotnet Vessel.Api.dll identity reset-after-restore [--keep-active <guid> …]
               dotnet Vessel.Api.dll identity verify
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, IdentityVerification? verify = null, CancellationToken ct = default)
    {
        try
        {
            // Match WebApplication.CreateBuilder: DOTNET_ENVIRONMENT overrides ASPNETCORE_ENVIRONMENT.
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            var commandArgs = new List<string>();
            var configArgs = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--Vessel:", StringComparison.Ordinal) || args[i].StartsWith("--ConnectionStrings:", StringComparison.Ordinal))
                {
                    configArgs.Add(args[i]);
                    if (!args[i].Contains('='))
                    {
                        if (i + 1 >= args.Length) { await output.WriteLineAsync(Usage); return 2; }
                        configArgs.Add(args[++i]);
                    }
                }
                else commandArgs.Add(args[i]);
            }
            var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true).AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables().AddCommandLine(configArgs.ToArray()).Build();
            return await RunAsync(commandArgs.ToArray(), configuration,
                string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase), output, verify, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await output.WriteLineAsync("The identity command failed. Check private configuration.");
            return 1;
        }
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, bool development,
        TextWriter output, IdentityVerification? verify = null, CancellationToken ct = default)
    {
        try
        {
            if (args.Length == 0 || !TryOptions(args[1..], out var options, out var keepActive) ||
                !ValidOptions(args[0], options, keepActive))
            {
                await output.WriteLineAsync(Usage); return 2;
            }
            var settings = IdentitySettings.Read(configuration, development);
            if (settings.LegacyOwnerOverride && settings.OwnerId == Vessel.Application.Ownership.JournalOwner.Default)
                await output.WriteLineAsync("Warning: Vessel:Identity:AllowLegacyDefaultOwner permits the legacy default owner.");
            var connection = configuration.GetConnectionString("Vessel");
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Vessel is required.");
            var schema = new IdentitySchema(settings.Schema);
            var dbOptions = new DbContextOptionsBuilder<IdentityDbContext>();
            IdentityDbContext.Configure(dbOptions, connection, schema);
            var factory = new CommandContextFactory(dbOptions.Options, schema);
            var transactions = new IdentityTransaction(factory);
            switch (args[0])
            {
                case "ensure-owner":
                    await transactions.RunAsync(async db =>
                    {
                        if (!await db.Users.AnyAsync(x => x.Id == settings.OwnerId, ct))
                        {
                            var now = DateTimeOffset.UtcNow;
                            db.Users.Add(new IdentityUser { Id = settings.OwnerId, DisplayName = IdentityText.DisplayName(settings.OwnerName),
                                Role = "operator", CreatedAt = now, UpdatedAt = now });
                        }
                        return true;
                    }, ct);
                    await output.WriteLineAsync($"Ensured identity user {settings.OwnerId}.");
                    return 0;
                case "bind-wallet":
                    if (!Guid.TryParse(options["--user"], out var userId) || userId == Guid.Empty)
                    { await output.WriteLineAsync(Usage); return 2; }
                    var address = WalletSignatures.NormalizeAddress(options["--address"]);
                    var label = IdentityText.Label(options.GetValueOrDefault("--label"));
                    var bound = await transactions.RunAsync(async db =>
                    {
                        if (await IdentityTransaction.LockUserAsync(db, userId, ct) is null) return false;
                        var existing = await db.Wallets.SingleOrDefaultAsync(x => x.Address == address, ct);
                        if (existing is not null) return existing.UserId == userId;
                        await IdentityLimits.RequireWalletCapacityAsync(db, userId, ct);
                        db.Wallets.Add(new UserWallet { Id = Guid.NewGuid(), UserId = userId, Address = address, Label = label, AddedAt = DateTimeOffset.UtcNow });
                        return true;
                    }, ct);
                    await output.WriteLineAsync(bound ? $"Bound {WalletSignatures.Checksum(address)} to {userId}." : "The user is absent or the wallet belongs to another user.");
                    return bound ? 0 : 3;
                case "invite":
                    var expires = options.GetValueOrDefault("--expires", "7d");
                    if (!expires.EndsWith('d') || !int.TryParse(expires[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var days) ||
                        days < 1 || !int.TryParse(options.GetValueOrDefault("--max-uses", "1"), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var maxUses) || maxUses < 1)
                    { await output.WriteLineAsync(Usage); return 2; }
                    var note = IdentityText.Note(options.GetValueOrDefault("--note"));
                    var code = InvitationCodes.Create();
                    await transactions.RunAsync(db =>
                    {
                        var now = DateTimeOffset.UtcNow;
                        db.Invitations.Add(new Invitation { Id = Guid.NewGuid(), CodeHash = IdentityTokens.Hash(InvitationCodes.Normalize(code)),
                            Note = note, CreatedAt = now, ExpiresAt = now.AddDays(days), MaxUses = maxUses });
                        return Task.FromResult(true);
                    }, ct);
                    await output.WriteLineAsync(code);
                    return 0;
                default:
                    return await RunAdministrationAsync(args[0], options, keepActive, configuration, factory, transactions, output, verify, ct);
            }
        }
        catch (ProblemException error) when (error.Code == "wallet-limit")
        {
            await output.WriteLineAsync(error.Message);
            return 3;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Only our own validation text is safe; driver errors can contain connection details.
            var safe = error.Source == "Vessel.Identity" && error is InvalidOperationException or ProblemException;
            await output.WriteLineAsync(safe ? error.Message : "The identity command failed. Check private configuration and migrations.");
            return 1;
        }
    }

    private static bool TryOptions(string[] args, out Dictionary<string, string> options, out List<Guid> keepActive)
    {
        options = [];
        keepActive = [];
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--keep-active")
            {
                var start = i;
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    if (!Guid.TryParse(args[++i], out var id) || id == Guid.Empty) return false;
                    keepActive.Add(id);
                }
                if (start == i) return false;
            }
            else if (key == "--all")
            {
                if (!options.TryAdd(key, "true")) return false;
            }
            else if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length ||
                args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(key, args[++i])) return false;
        }
        return true;
    }

    private static bool ValidOptions(string command, Dictionary<string, string> options, List<Guid> keepActive)
    {
        if (command != "reset-after-restore" && keepActive.Count != 0) return false;
        return command switch
        {
            "ensure-owner" or "verify" or "reset-after-restore" => options.Count == 0,
            "bind-wallet" => options.ContainsKey("--user") && options.ContainsKey("--address") &&
                options.Keys.All(x => x is "--user" or "--address" or "--label"),
            "invite" => options.Keys.All(x => x is "--note" or "--expires" or "--max-uses"),
            "unbind-wallet" => options.Count == 1 && options.ContainsKey("--wallet"),
            "suspend" or "restore" or "promote" or "demote" => options.Count == 1 && options.ContainsKey("--user"),
            "revoke-sessions" => options.Count == 1 && (options.ContainsKey("--user") || options.ContainsKey("--all")),
            _ => false
        };
    }

    private sealed class CommandContextFactory(DbContextOptions<IdentityDbContext> options, IdentitySchema schema) : IDbContextFactory<IdentityDbContext>
    {
        public IdentityDbContext CreateDbContext() => new(options, schema);
    }
}
