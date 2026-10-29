using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Vessel.Tests.Identity;

public partial class ConfigurationAndCommandTestsSupport
{
    internal static IConfiguration Config(Dictionary<string, string?>? overrides = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Auth:OwnerId"] = Guid.NewGuid().ToString(),
            ["Vessel:Identity:Origins:0"] = "https://vessel.test"
        }.Concat(overrides ?? []).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Last().Value)).Build();
}
