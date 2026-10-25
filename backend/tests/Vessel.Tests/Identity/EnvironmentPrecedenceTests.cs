using System.Diagnostics;
using Vessel.Application.Ownership;

namespace Vessel.Tests.Identity;

public sealed class EnvironmentPrecedenceTests
{
    [Theory]
    [InlineData("Development", "Production", false)]
    [InlineData("Production", "Development", true)]
    [InlineData("Development", null, true)]
    [InlineData(null, "Development", true)]
    [InlineData(null, null, false)]
    public async Task Offline_commands_and_the_actual_API_host_apply_the_same_environment_precedence(
        string? aspnet, string? dotnet, bool development)
    {
        // Child processes avoid mutating the shared test process's environment. Neither path opens a database:
        // Development reaches the deliberate missing-connection/broker checks; Production refuses the owner first.
        var cli = await RunAsync(aspnet, dotnet, command: true);
        Assert.Equal(1, cli.ExitCode);
        Assert.Contains(development ? "ConnectionStrings:Vessel" : "Vessel:Auth:OwnerId", cli.Output);
        var host = await RunAsync(aspnet, dotnet, command: false);
        Assert.NotEqual(0, host.ExitCode);
        Assert.Contains(development ? "Vessel:Messaging:RabbitMqUri" : "Vessel:Auth:OwnerId", host.Output);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string? aspnet, string? dotnet, bool command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet"))
        {
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Vessel__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) || key == "DOTNET_ENVIRONMENT").ToArray())
            start.Environment.Remove(key);
        if (aspnet is not null) start.Environment["ASPNETCORE_ENVIRONMENT"] = aspnet;
        if (dotnet is not null) start.Environment["DOTNET_ENVIRONMENT"] = dotnet;
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        if (command)
        {
            start.ArgumentList.Add("identity");
            start.ArgumentList.Add("ensure-owner");
        }
        foreach (var argument in new[]
        {
            $"--Vessel:Auth:OwnerId={JournalOwner.Default}",
            "--Vessel:Identity:AllowLegacyDefaultOwner=false", "--Vessel:Identity:Origins:0=http://localhost",
            "--ConnectionStrings:Vessel=", "--Vessel:Messaging:RabbitMqUri="
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
