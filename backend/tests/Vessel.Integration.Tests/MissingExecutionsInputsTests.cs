using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// #92: the missing-executions rule reads an order only through <see cref="IMissingExecutionsInputs"/>, and the journal
/// search projection watches exactly that type's members. This checks that each member is the order entity's own mapped
/// property, so the property the capture watches is the one the rule reads. It opens no database connection.
/// </summary>
public sealed class MissingExecutionsInputsTests
{
    [Fact]
    public void Every_rule_input_is_the_same_mapped_property_of_the_order()
    {
        using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql("Host=model-only").Options,
            new CoreOwner(Guid.NewGuid()));
        var entity = db.Model.FindEntityType(typeof(ImportedOrder))!;
        var map = typeof(ImportedOrder).GetInterfaceMap(typeof(IMissingExecutionsInputs));
        var inputs = typeof(IMissingExecutionsInputs).GetProperties();
        Assert.Equal(inputs.Select(p => p.Name), ExecutionIntegrity.MissingExecutionsOrderInputs);
        Assert.All(inputs, input =>
        {
            var property = typeof(ImportedOrder).GetProperty(input.Name);
            Assert.NotNull(property);
            // An explicit implementation could return another property than the one the capture watches.
            Assert.Equal(property.GetMethod, map.TargetMethods[Array.IndexOf(map.InterfaceMethods, input.GetMethod)]);
            Assert.False(entity.FindProperty(input.Name)?.IsShadowProperty() ?? true, $"{input.Name} is not a mapped order property.");
        });
    }
}
