using Vessel.Application.Ownership;
using Vessel.Infrastructure;
using Vessel.Persistence;

namespace Vessel.Api;

/// <summary>
/// Administrative CLI only (issue #108): builds the Activity read model's stored parts ahead of the first read, and checks
/// them against the in-memory aggregate. No HTTP listener and no schema migration; apply the ActivityReadModelTables
/// migration first.
/// </summary>
public static class ActivityReadModelCommand
{
    public const string Rebuild = "activity-rebuild", Verify = "activity-verify";

    private const string Usage = """
        Usage: dotnet Vessel.Api.dll activity-rebuild [--apply]
               dotnet Vessel.Api.dll activity-verify
        Reads ConnectionStrings__Vessel from server configuration. Apply the ActivityReadModelTables migration first.
        activity-rebuild counts each owner's enabled real accounts whose stored Activity part is missing or out of date; with
        --apply it rebuilds them, one account per transaction. Optional: Activity builds a missing part on its first read.
        activity-verify recomputes every owner's Activity in memory and compares each stored operation, account part,
        version and coverage with it. Exit code 0 means no difference and nothing out of date. Read-only.
        Both are safe beside running hosts and safe to run again; imported facts, Plays and links are never changed.
        """;

    public static async Task<int> RunAsync(string command, string[] args, TextWriter output, CancellationToken ct = default)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        return await RunAsync(command, args, configuration, output, ct);
    }

    public static async Task<int> RunAsync(string command, string[] args, IConfiguration configuration, TextWriter output,
        CancellationToken ct = default)
    {
        var apply = args is ["--apply"];
        if (command == Rebuild ? args.Length > 1 || args.Length == 1 && !apply : args.Length > 0)
        {
            await output.WriteLineAsync(Usage);
            return args is ["--help"] ? 0 : 2;
        }
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Vessel")))
        {
            await output.WriteLineAsync("ConnectionStrings__Vessel is required. Nothing was read.");
            return 2;
        }
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
            // The venue descriptors decide which accounts have funding, exactly as the API's aggregate reads them.
            services.AddVesselInfrastructure(configuration);
            services.AddVesselApplication();
            services.AddVesselOfflinePersistence(configuration);
            await using var provider = services.BuildServiceProvider();
            var inventory = provider.GetRequiredService<IJournalOwnerInventory>();
            var scopes = provider.GetRequiredService<IOwnerScopeFactory>();
            int owners = 0, accounts = 0, stale = 0, rebuilt = 0, operations = 0, differences = 0;
            Guid? after = null;
            while (true)
            {
                // Deliberately every journal owner: this offline operation covers the whole database's read model.
                var page = await inventory.DistinctOwnersAsync(after, 1000, ct);
                foreach (var owner in page)
                {
                    await using var scope = scopes.Create(owner);
                    var model = scope.Services.GetRequiredService<ActivityReadModel>();
                    if (command == Verify)
                    {
                        var result = await model.VerifyAsync(ct);
                        if (result.Accounts == 0) continue;
                        owners++; accounts += result.Accounts; stale += result.StaleAccounts; operations += result.Operations;
                        differences += result.Differences.Count;
                        await output.WriteLineAsync($"Owner {owner}: {result.Accounts} accounts, {result.Operations} operations compared, " +
                            $"{result.StaleAccounts} out of date, {result.Differences.Count} differences.");
                        foreach (var difference in result.Differences.Take(20)) await output.WriteLineAsync("  " + difference);
                        continue;
                    }
                    var (count, pending) = await model.StaleAsync(ct);
                    if (count == 0) continue;
                    owners++; accounts += count; stale += pending.Count;
                    if (apply)
                        foreach (var account in pending)
                            if (await model.RefreshAsync(account, ct)) rebuilt++;
                    await output.WriteLineAsync($"Owner {owner}: {count} accounts, {pending.Count} " +
                        (apply ? "rebuilt or already current." : "would be rebuilt."));
                }
                if (page.Count < 1000) break;
                after = page[^1];
            }
            if (command == Verify)
            {
                await output.WriteLineAsync(differences == 0 && stale == 0
                    ? $"Verified {operations} operations on {accounts} accounts for {owners} owner(s): no difference."
                    : $"{differences} differences and {stale} out-of-date accounts on {accounts} accounts for {owners} owner(s)." +
                      (stale > 0 ? " Run activity-rebuild --apply, then verify again." : ""));
                return differences == 0 && stale == 0 ? 0 : 1;
            }
            await output.WriteLineAsync(apply
                ? $"Rebuilt {rebuilt} of {accounts} accounts for {owners} owner(s). Imported facts were not changed."
                : $"Dry run: {stale} of {accounts} accounts for {owners} owner(s) would be rebuilt. Add --apply to rebuild them.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await output.WriteLineAsync("Cancelled. Accounts already rebuilt stay current; run the command again to finish.");
            return 130;
        }
        catch (Exception)
        {
            // Never echo connection strings or database exception payloads.
            await output.WriteLineAsync("The Activity read model command failed. Check database access and that the " +
                "ActivityReadModelTables migration is applied, then run it again.");
            return 1;
        }
    }
}
