using System.Reflection;
using System.Runtime.CompilerServices;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>
/// Issue #113: a call that forgets the trading mode must not compile into a real-journal read. No production method may
/// take an optional or nullable <see cref="TradingMode"/>, and no production type may declare a sibling overload that is
/// identical except for a missing <see cref="TradingMode"/> parameter. The request boundary maps an omitted wire value
/// to Real from a string, never through a mode-free overload.
/// </summary>
public sealed class TradingModeSignatureTests
{
    private static readonly string[] Production =
    [
        "Vessel.Domain", "Vessel.Application", "Vessel.Persistence", "Vessel.Infrastructure", "Vessel.Identity",
        "Vessel.Messaging", "Vessel.Api", "Vessel.Worker",
    ];

    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void No_production_method_selects_a_trading_mode_implicitly()
    {
        var types = Production.Select(Assembly.Load).SelectMany(a => a.GetTypes())
            .Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), false)).ToArray();
        // The scan must reach the ports and stores the issue names.
        Assert.Contains(types, t => t.FullName == "Vessel.Application.Workspace.IWorkspaceStore");
        Assert.Contains(types, t => t.FullName == "Vessel.Persistence.WorkspaceStore");

        var found = new List<string>();
        foreach (var type in types)
        {
            var methods = type.GetMethods(Declared).Where(m => !m.IsSpecialName && !m.IsDefined(typeof(CompilerGeneratedAttribute), false)).ToArray();
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                foreach (var p in parameters.Where(p => p.ParameterType == typeof(TradingMode) && p.HasDefaultValue))
                    found.Add($"{Name(type, method)}: optional {p.Name}");
                foreach (var p in parameters.Where(p => p.ParameterType == typeof(TradingMode?)))
                    found.Add($"{Name(type, method)}: nullable {p.Name}");
                foreach (var sibling in methods.Where(s => s != method && s.Name == method.Name))
                {
                    var full = sibling.GetParameters().Select(p => p.ParameterType).ToList();
                    for (var i = 0; i < full.Count; i++)
                    {
                        if (full[i] != typeof(TradingMode)) continue;
                        var without = full.Where((_, j) => j != i).ToArray();
                        if (without.SequenceEqual(parameters.Select(p => p.ParameterType)))
                            found.Add($"{Name(type, method)}: mode-free sibling of {Name(type, sibling)}");
                    }
                }
            }
        }

        Assert.True(found.Count == 0,
            "Pass a TradingMode explicitly; map an omitted wire value at the request boundary only. Found:\n" +
            string.Join("\n", found.Distinct().Order(StringComparer.Ordinal)));
    }

    private static string Name(Type type, MethodInfo method) =>
        $"{type.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
}
