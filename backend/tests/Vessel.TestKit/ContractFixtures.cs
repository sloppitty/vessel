using System.Text.RegularExpressions;

namespace Vessel.Tests;

/// <summary>Real API JSON samples shared with the browser guards. Identifiers and times vary per run.</summary>
internal static partial class ContractFixtures
{
    public static string Path(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "frontend", "src", "test", "contracts");
            if (Directory.Exists(candidate)) return System.IO.Path.Combine(candidate, name);
        }
        throw new DirectoryNotFoundException("frontend/src/test/contracts was not found above the test output.");
    }

    /// <summary>
    /// Replaces GUIDs by first appearance and timestamps by a fixed instant, keeping each timestamp's
    /// offset form so a serializer change still shows. Sequences and liveness are run-dependent too.
    /// </summary>
    public static string Normalize(string json)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        json = Guid().Replace(json, match =>
        {
            if (!ids.TryGetValue(match.Value, out var stable))
                ids[match.Value] = stable = $"00000000-0000-4000-8000-{ids.Count + 1:D12}";
            return stable;
        });
        json = Timestamp().Replace(json, match => "2026-10-14T03:20:41.1234567" + match.Groups["offset"].Value);
        json = Sequence().Replace(json, match => match.Groups["name"].Value + "\"42\"");
        // Opaque {epoch}.{sequence} cursors: the stream epoch is random per database.
        json = Cursor().Replace(json, match => match.Groups["name"].Value + "\"00000000000040008000000000000000.42\"");
        return Online().Replace(json, match => match.Groups["name"].Value + "true");
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();
    [GeneratedRegex("""\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?<offset>Z|[+-]\d{2}:\d{2})""")]
    private static partial Regex Timestamp();
    [GeneratedRegex("(?<name>\"sequence\":\\s*)\"\\d+\"")]
    private static partial Regex Sequence();
    [GeneratedRegex("(?<name>\"(?:nextCursor|eventPageNextCursor)\":\\s*)\"[0-9a-f]{32}\\.\\d+\"")]
    private static partial Regex Cursor();
    [GeneratedRegex("""(?<name>"workerOnline":\s*)(?:true|false)""")]
    private static partial Regex Online();
}
