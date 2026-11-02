
namespace Vessel.Tests;

public sealed class VesselEventPostgresFactAttribute : FactAttribute
{
    public VesselEventPostgresFactAttribute()
    {
        var connection = Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Skip = "Requires an explicit disposable Vessel_TEST_POSTGRES database.";
    }
}
