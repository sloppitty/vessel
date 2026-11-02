using System.Xml.Linq;

namespace Vessel.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Domain", "")]
    [InlineData("Application", "Domain")]
    [InlineData("Persistence", "Application,Domain")]
    [InlineData("Infrastructure", "Application")]
    [InlineData("Identity", "Application")]
    [InlineData("Messaging", "Application,Persistence")]
    [InlineData("Api", "Application,Identity,Infrastructure,Messaging,Persistence")]
    [InlineData("Worker", "Application,Identity,Infrastructure,Messaging,Persistence")]
    public void Project_dependencies_point_inward(string project, string expected)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vessel.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var xml = XDocument.Load(Path.Combine(directory.FullName, "src", $"Vessel.{project}", $"Vessel.{project}.csproj"));
        var references = xml.Descendants("ProjectReference")
            .Select(x => Path.GetFileNameWithoutExtension(x.Attribute("Include")!.Value).Replace("Vessel.", ""))
            .Order().ToArray();
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(), references);
        if (project is "Domain" or "Application")
        {
            Assert.Empty(xml.Descendants("PackageReference"));
            Assert.Empty(xml.Descendants("FrameworkReference"));
        }
        // Wolverine and RabbitMQ live in the one messaging adapter, never in the business layers or Persistence.
        var packages = xml.Descendants("PackageReference").Select(x => x.Attribute("Include")!.Value).ToArray();
        if (project is not "Messaging")
            Assert.DoesNotContain(packages, p => p.StartsWith("WolverineFx", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("RabbitMQ", StringComparison.OrdinalIgnoreCase));
    }
}
