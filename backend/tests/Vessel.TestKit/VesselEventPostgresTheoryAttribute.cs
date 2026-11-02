
namespace Vessel.Tests;

public sealed class VesselEventPostgresTheoryAttribute : TheoryAttribute
{
    public VesselEventPostgresTheoryAttribute() => Skip = new VesselEventPostgresFactAttribute().Skip;
}
