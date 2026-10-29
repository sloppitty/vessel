
namespace Vessel.Tests;

internal sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VESSEL_TEST_S3_ENDPOINT") is null)
            Skip = "Run scripts/verify-evidence-storage.py for isolated RustFS integration.";
    }
}
