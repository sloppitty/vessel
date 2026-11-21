using System.Globalization;
using Microsoft.Extensions.Configuration;
using Vessel.Persistence.Events;

namespace Vessel.Messaging;

/// <summary>
/// Offline administrator commands. They never start a host, listen, migrate the EF schema or print secrets.
/// Stop every host before rotating the epoch; provisioning is idempotent and insert-only.
/// </summary>
public static class MessagingCommands
{
    public static readonly string[] Names =
        ["provision-messaging", "rotate-event-epoch", "deregister-event-subscription", "retry-event-receipts"];

    private const string Usage = """
        Usage: dotnet Vessel.Api.dll provision-messaging
               dotnet Vessel.Api.dll rotate-event-epoch
               dotnet Vessel.Api.dll deregister-event-subscription <name>
               dotnet Vessel.Api.dll retry-event-receipts <name> (--owner <guid> | --all-owners) [--limit 1-1000] [--attempts n]
        Reads ConnectionStrings__Vessel and Vessel__Messaging__* from server configuration.
        provision-messaging runs after the EF migrations: insert-only subscription registrations of every module.
        Restore procedure: stop every host, restore the database, rotate-event-epoch, provision-messaging, then start
        the hosts.
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
        try
        {
            switch (args)
            {
                case ["provision-messaging"]:
                    var result = await MessagingProvisioning.ProvisionAsync(configuration, ct);
                    await output.WriteLineAsync($"Registered subscriptions: {List(result.Registered)}; already registered: {List(result.Existing)}.");
                    return 0;
                case ["rotate-event-epoch"]:
                    var connection = configuration.GetConnectionString("Vessel");
                    if (string.IsNullOrWhiteSpace(connection)) return await Fail(output, "ConnectionStrings__Vessel is required.");
                    var rotated = await EventStreamEpoch.RotateAsync(connection, ct);
                    await output.WriteLineAsync($"Rotated the event stream epoch for {rotated} owner stream(s). Browser cursors from before now reset.");
                    return 0;
                case ["deregister-event-subscription", var name]:
                    var removed = await MessagingProvisioning.DeregisterAsync(configuration, name, ct);
                    await output.WriteLineAsync(removed ? $"Deregistered subscription {name}; its progress and receipts were removed."
                        : $"Subscription {name} was not registered.");
                    return 0;
                case ["retry-event-receipts", var name, .. var options] when Options(options, out var limit, out var attempts, out var owner, out var allOwners):
                    var retried = await MessagingProvisioning.RetryFailedAsync(configuration, name, limit, attempts, owner, allOwners, ct);
                    await output.WriteLineAsync($"Returned {retried} failed receipt(s) of {name} to pending with {attempts} more attempt(s).");
                    return 0;
                default:
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Driver errors can contain hosts or credentials. Only Vessel's own text is shown.
            var safe = error.Source is "Vessel.Messaging" or "Vessel.Persistence" or "Vessel.Application" &&
                error is InvalidOperationException or ArgumentException or Vessel.Application.Common.ProblemException;
            return await Fail(output, safe ? error.Message : $"The command failed ({error.GetType().Name}). Check private configuration.");
        }
    }

    private static bool Options(string[] options, out int limit, out int attempts, out Guid? owner, out bool allOwners)
    {
        limit = 100; attempts = 3; owner = null; allOwners = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Length; i++)
        {
            var option = options[i];
            if (!seen.Add(option)) return false;
            if (option == "--all-owners") { allOwners = true; continue; }
            if (++i >= options.Length) return false;
            if (option == "--owner")
            {
                if (!Guid.TryParse(options[i], out var id) || id == Guid.Empty) return false;
                owner = id;
                continue;
            }
            if (!int.TryParse(options[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            switch (option)
            {
                case "--limit": limit = value; break;
                case "--attempts": attempts = value; break;
                default: return false;
            }
        }
        return allOwners != owner.HasValue && limit is >= 1 and <= 1000 && attempts >= 1;
    }

    private static string List(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    private static async Task<int> Fail(TextWriter output, string message)
    {
        await output.WriteLineAsync(message);
        return 1;
    }
}
