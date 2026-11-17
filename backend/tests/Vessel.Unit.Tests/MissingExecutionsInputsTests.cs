using System.Reflection;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// #92: the journal search projection re-projects an order's Plays only when one of
/// <see cref="ExecutionIntegrity.MissingExecutionsOrderInputs"/> changes. This ties that list to the rule by reflection: over
/// every combination of the listed inputs and retained quantity, no other order property changes the rule's answer, and each
/// listed one does somewhere. A new input the rule starts reading fails here until it is listed.
/// </summary>
public sealed class MissingExecutionsInputsTests
{
    private static readonly PropertyInfo[] Properties = typeof(ImportedOrder).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite).ToArray();

    private static readonly decimal[] Amounts = [0m, 1m, 2m, 3m];

    // The values each property takes here; an order property of a new type needs its values added.
    private static object?[] Values(Type type)
    {
        if (type == typeof(bool)) return [false, true];
        if (type == typeof(string)) return ["open", "filled", "triggered", "canceled", "rejected", "other", "x"];
        if (type == typeof(decimal)) return [.. Amounts.Cast<object?>()];
        if (type == typeof(decimal?)) return [null, .. Amounts.Cast<object?>()];
        if (type == typeof(Guid)) return [Guid.Empty, new Guid("00000000-0000-0000-0000-000000000001")];
        if (type == typeof(DateTimeOffset)) return [DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero)];
        throw new InvalidOperationException($"Add test values for {type} to {nameof(MissingExecutionsInputsTests)}.");
    }

    private static ImportedOrder Template()
    {
        var order = new ImportedOrder();
        foreach (var p in Properties) p.SetValue(order, Values(p.PropertyType)[0]);
        return order;
    }

    private static ImportedOrder With(ImportedOrder source, PropertyInfo property, object? value)
    {
        var order = new ImportedOrder();
        foreach (var p in Properties) p.SetValue(order, p.GetValue(source));
        property.SetValue(order, value);
        return order;
    }

    // Every combination of the listed inputs, over the template.
    private static IEnumerable<ImportedOrder> Baselines()
    {
        IEnumerable<ImportedOrder> orders = [Template()];
        foreach (var input in ExecutionIntegrity.MissingExecutionsOrderInputs.Select(n => typeof(ImportedOrder).GetProperty(n)!))
            orders = orders.SelectMany(o => Values(input.PropertyType).Select(v => With(o, input, v))).ToList();
        return orders;
    }

    [Fact]
    public void Every_listed_input_is_an_order_property_and_changes_the_rule_somewhere()
    {
        foreach (var name in ExecutionIntegrity.MissingExecutionsOrderInputs)
        {
            var property = Assert.Single(Properties, p => p.Name == name);
            Assert.True(Baselines().Any(order => Amounts.Any(retained => Values(property.PropertyType).Any(value =>
                ExecutionIntegrity.MissingExecutions(With(order, property, value), retained) != ExecutionIntegrity.MissingExecutions(order, retained)))),
                $"{name} is listed but never changes whether executions are missing.");
        }
    }

    [Fact]
    public void No_unlisted_order_property_changes_the_rule()
    {
        var unlisted = Properties.Where(p => !ExecutionIntegrity.MissingExecutionsOrderInputs.Contains(p.Name)).ToList();
        Assert.Contains(unlisted, p => p.Name == nameof(ImportedOrder.RemainingSize));
        foreach (var order in Baselines())
            foreach (var retained in Amounts)
            {
                var expected = ExecutionIntegrity.MissingExecutions(order, retained);
                foreach (var property in unlisted)
                    foreach (var value in Values(property.PropertyType))
                        Assert.True(expected == ExecutionIntegrity.MissingExecutions(With(order, property, value), retained),
                            $"{property.Name} = {value ?? "null"} changes whether executions are missing; add it to " +
                            $"{nameof(ExecutionIntegrity.MissingExecutionsOrderInputs)} so the Plays list follows it.");
            }
    }
}
