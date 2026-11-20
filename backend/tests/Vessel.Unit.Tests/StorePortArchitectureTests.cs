using System.Reflection;
using System.Text.RegularExpressions;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Issue #114: a store port member with a default body lets a production store miss it silently. Every member of an
/// Application port that Persistence implements is abstract; test fakes take their defaults from Vessel.TestKit.
/// </summary>
public sealed class StorePortArchitectureTests
{
    [Fact]
    public void Store_ports_declare_no_default_members()
    {
        var ports = StorePorts();
        // The source scan must find the ports it guards, including a base list on its own line.
        Assert.Superset(new HashSet<Type>
        {
            typeof(IPlayExecutionStore), typeof(IWorkspaceStore), typeof(ISizingStore), typeof(IUnitOfWork), typeof(IVesselEventSink),
        }, ports.ToHashSet());

        var defaults = ports
            .SelectMany(port => port.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsAbstract)
                .Select(method => $"{port.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(defaults.Length == 0,
            "Store ports must declare every member abstract; put test defaults in a Vessel.TestKit *Defaults interface " +
            "and shared logic in a static helper. Default members found:\n" + string.Join("\n", defaults));
    }

    /// <summary>Application interfaces named in the base list of a Persistence class, record or struct.</summary>
    private static Type[] StorePorts()
    {
        var implemented = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(SourceRoot(), "Vessel.Persistence"), "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains("/obj/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal) &&
                                 !p.Contains("/Migrations/", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(path);
            foreach (Match declaration in Regex.Matches(source, @"\b(?:class|record|struct)\s+\w+[^{;]*[{;]"))
            foreach (Match name in Regex.Matches(declaration.Value,
                         @"(?:\b[\w.]+\.)?\b(I[A-Z]\w*)\b(?!\s*<)(?!\??\s+(?!where\b)[A-Za-z_@])"))
                implemented.Add(name.Groups[1].Value);
        }
        return typeof(IPlayExecutionStore).Assembly.GetTypes()
            .Where(type => type.IsInterface && implemented.Contains(type.Name))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vessel.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src");
    }
}
