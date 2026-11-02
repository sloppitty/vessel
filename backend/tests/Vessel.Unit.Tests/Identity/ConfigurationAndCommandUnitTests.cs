
namespace Vessel.Tests.Identity;

public sealed class ConfigurationAndCommandUnitTests
{

    [Fact]
    public void Migration_script_keeps_argument_forwarding_only_on_the_journal_bundle()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy", "migrate"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var script = File.ReadAllText(Path.Combine(directory.FullName, "deploy", "migrate"));
        var expected = new[] { "Vessel.Api.dll check-config", "/app/migrate-identity-ef\n", "/app/migrate-ef \"$@\"", "Vessel.Api.dll identity ensure-owner", "Vessel.Api.dll provision-messaging" };
        var previous = -1;
        foreach (var step in expected) { var index = script.IndexOf(step, StringComparison.Ordinal); Assert.True(index > previous); previous = index; }
        Assert.Contains("set -eu", script);
    }
}
