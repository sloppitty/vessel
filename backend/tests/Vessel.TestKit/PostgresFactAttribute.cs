
namespace Vessel.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES")))
            Skip = "Set Vessel_TEST_POSTGRES to a real PostgreSQL connection with CREATE SCHEMA permission.";
    }
}
