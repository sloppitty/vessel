namespace Vessel.Api;

/// <summary>
/// Outside Development, refuse to start without the settings every request needs, instead of running as
/// an installation that only ever answers 401 or 503. Messages name settings, never their values.
/// </summary>
public static class ProductionConfiguration
{
    public static void Validate(IConfiguration config)
    {
        // The wallet sign-in settings, including the owner GUID, and a refusal of the removed Token sign-in's settings.
        _ = Vessel.Identity.IdentitySettings.Read(config, development: false);
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("Vessel")))
            throw new InvalidOperationException("Vessel cannot start: ConnectionStrings__Vessel is required.");
    }
}
