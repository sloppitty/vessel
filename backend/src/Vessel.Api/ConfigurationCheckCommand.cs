using Vessel.Identity;

namespace Vessel.Api;

/// <summary>
/// Configuration-only preflight for <c>deploy/migrate</c>: applies the API's startup validation (production settings
/// and identity settings, which the workers read too) without connecting to the database, so a first install with no
/// identity tables can be checked before either migration bundle runs. Messages name settings, never their values.
/// Exit codes: 0 valid, 1 invalid configuration, 2 usage.
/// </summary>
public static class ConfigurationCheckCommand
{
    private const string Usage = """
        Usage: dotnet Vessel.Api.dll check-config [--Vessel:Key=value …]
        Validates the authentication and identity configuration exactly as the API and workers do at startup,
        without opening a database or broker connection. Exit codes: 0 valid, 1 invalid, 2 usage.
        """;

    public static int Run(string[] args, TextWriter output)
    {
        if (args is ["--help"]) { output.WriteLine(Usage); return 0; }
        if (args.Any(arg => !arg.StartsWith("--", StringComparison.Ordinal))) { output.WriteLine(Usage); return 2; }
        try
        {
            // The API host's own configuration sources and environment, so nothing here can diverge from startup.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
            return Check(builder.Configuration, builder.Environment.IsDevelopment(), output);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            output.WriteLine("The configuration could not be read. Check private configuration.");
            return 1;
        }
    }

    public static int Check(IConfiguration configuration, bool development, TextWriter output)
    {
        try
        {
            // Program.cs order: production settings outside Development, then the identity settings every host resolves.
            if (!development) ProductionConfiguration.Validate(configuration);
            var settings = IdentitySettings.Read(configuration, development);
            if (settings.LegacyOwnerOverride && settings.OwnerId == Vessel.Application.Ownership.JournalOwner.Default)
                output.WriteLine("Warning: Vessel:Identity:AllowLegacyDefaultOwner permits the legacy default owner.");
            // Accepted by the code, but the delivery contract (§13) leaves them undocumented until per-user quotas exist.
            if (settings.Registration is "AllowList" or "Open")
                output.WriteLine($"Warning: Vessel:Identity:Registration={settings.Registration} is unsupported until per-user quotas exist; use Closed or Invitation.");
            output.WriteLine("Configuration is valid for wallet sign-in.");
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException && error.Source is "Vessel.Api" or "Vessel.Identity")
        {
            output.WriteLine(error.Message);
            return 1;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Binder and provider errors can quote configured values; print none of them.
            output.WriteLine("The configuration is invalid. Check private configuration.");
            return 1;
        }
    }
}
