using System.Text.Json;

namespace Vessel.Tests;

/// <summary>The plan number list shared with the browser: <c>frontend/src/test/contracts/plan-numbers.json</c>.</summary>
internal static class PlanNumberFixture
{
    private static readonly JsonElement Root = Load();
    public static IReadOnlyList<string> Accepted => List("accepted");
    public static IReadOnlyList<string> Rejected => List("rejected");
    /// <summary>Refused texts that a stop or target in % refuses with the negative-percentage reason.</summary>
    public static IReadOnlyList<string> NegativePercent => List("negativePercent");

    private static JsonElement Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ContractFixtures.Path("plan-numbers.json")));
        return document.RootElement.Clone();
    }

    private static List<string> List(string name) => Root.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToList();
}
