using Vessel.Application.Ownership;
using Vessel.Persistence;
using Vessel.Persistence.Search;

namespace Vessel.Api;

/// <summary>
/// Administrative CLI only (Phase 2, item 13): fills the journal search projection once after the upgrade that adds it.
/// No HTTP listener and no schema migration; apply the JournalSearch migration first.
/// </summary>
public static class PlaySearchCommand
{
    public const string Name = "play-search-rebuild";

    private const string Usage = """
        Usage: dotnet Vessel.Api.dll play-search-rebuild [--apply] [--all] [--batch 1-500]
        Run once after applying the JournalSearch migration. Reads ConnectionStrings__Vessel from server configuration.
        Dry run by default: counts each owner's Plays that are not indexed yet. --apply projects them in batches of
        --batch Plays (default 100) per transaction; --all projects every Play again. Safe beside running hosts and safe
        to run again: Plays already indexed are left alone. Plays, plans, reviews and other journal facts are never changed.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct = default)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        return await RunAsync(args, configuration, output, ct);
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output, CancellationToken ct = default)
    {
        if (!Options(args, out var apply, out var all, out var batch))
        {
            await output.WriteLineAsync(Usage);
            return args is ["--help"] ? 0 : 2;
        }
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Vessel")))
        {
            await output.WriteLineAsync("ConnectionStrings__Vessel is required. Nothing was indexed.");
            return 2;
        }
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
            services.AddVesselPersistence(configuration);
            await using var provider = services.BuildServiceProvider();
            var inventory = provider.GetRequiredService<IJournalOwnerInventory>();
            var scopes = provider.GetRequiredService<IOwnerScopeFactory>();
            int owners = 0, plays = 0, pending = 0, projected = 0;
            Guid? after = null;
            while (true)
            {
                // Deliberately every journal owner: this offline operation initializes the whole database's projection.
                var page = await inventory.DistinctOwnersAsync(after, 1000, ct);
                foreach (var owner in page)
                {
                    await using var scope = scopes.Create(owner);
                    var result = await scope.Services.GetRequiredService<PlaySearchRebuild>().RunAsync(apply, all, batch, ct);
                    if (result.Plays == 0) continue;
                    owners++; plays += result.Plays; pending += result.Pending; projected += result.Projected;
                    await output.WriteLineAsync($"Owner {owner}: {result.Plays} Plays, {result.Pending} " +
                        (apply ? $"to index, {result.Projected} indexed." : "would be indexed."));
                }
                if (page.Count < 1000) break;
                after = page[^1];
            }
            await output.WriteLineAsync(apply
                ? $"Indexed {projected} of {plays} Plays for {owners} owner(s). Journal facts were not changed."
                : $"Dry run: {pending} of {plays} Plays for {owners} owner(s) would be indexed. Add --apply to index them.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await output.WriteLineAsync("Indexing cancelled. Batches already committed stay indexed; run the command again to finish.");
            return 130;
        }
        catch (Exception)
        {
            // Never echo connection strings or database exception payloads.
            await output.WriteLineAsync("Indexing failed. Check database access and that the JournalSearch migration is applied. " +
                "Batches already committed stay indexed; run the command again after resolving the failure.");
            return 1;
        }
    }

    private static bool Options(string[] args, out bool apply, out bool all, out int batch)
    {
        apply = false; all = false; batch = PlaySearchRebuild.DefaultBatch;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!seen.Add(args[i])) return false;
            switch (args[i])
            {
                case "--apply": apply = true; break;
                case "--all": all = true; break;
                case "--batch" when i + 1 < args.Length && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= PlaySearchRebuild.MaxBatch:
                    batch = size; i++; break;
                default: return false;
            }
        }
        return true;
    }
}
