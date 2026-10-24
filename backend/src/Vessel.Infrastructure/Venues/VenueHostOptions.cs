using Microsoft.Extensions.Options;

namespace Vessel.Infrastructure.Venues;

/// <summary>One venue's hosts. <see cref="StreamUrl"/> is only read for venues that stream.</summary>
public sealed class VenueHost
{
    public string? BaseUrl { get; set; }
    public string? StreamUrl { get; set; }
}

/// <summary>
/// Venue hosts under <c>Vessel:Venues</c>. Defaults are the production hosts, so an empty section changes nothing.
/// Validated at startup (<see cref="VenueHostValidation"/>); failures name the configuration key, never its value.
/// </summary>
public sealed class VenueHostOptions
{
    public const string Section = "Vessel:Venues";

    public VenueHost Hyperliquid { get; set; } = new() { BaseUrl = "https://api.hyperliquid.xyz/", StreamUrl = "wss://api.hyperliquid.xyz/ws" };
    public VenueHost Risex { get; set; } = new() { BaseUrl = "https://api.rise.trade/" };
    public VenueHost Lighter { get; set; } = new() { BaseUrl = "https://mainnet.zklighter.elliot.ai/" };
    public VenueHost LighterRh { get; set; } = new() { BaseUrl = "https://api.rh.lighter.xyz/" };
    public VenueHost Pacifica { get; set; } = new() { BaseUrl = "https://api.pacifica.fi/" };

    /// <summary>The REST host for a configuration section name (for example <c>LighterRh</c>).</summary>
    public Uri BaseUri(string section) => VenueHostValidation.Rest(Host(section).BaseUrl)
        ?? throw new InvalidOperationException($"{Section}:{section}:BaseUrl is invalid.");

    public Uri StreamUri(string section) => VenueHostValidation.Stream(Host(section).StreamUrl)
        ?? throw new InvalidOperationException($"{Section}:{section}:StreamUrl is invalid.");

    internal IEnumerable<(string Section, VenueHost Host)> Hosts()
    {
        yield return (nameof(Hyperliquid), Hyperliquid);
        yield return (nameof(Risex), Risex);
        yield return (nameof(Lighter), Lighter);
        yield return (nameof(LighterRh), LighterRh);
        yield return (nameof(Pacifica), Pacifica);
    }

    private VenueHost Host(string section) => Hosts().FirstOrDefault(h => h.Section == section).Host
        ?? throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown venue host section.");
}

/// <summary>
/// REST hosts are https origins (adapters send absolute request paths, so a base path would be silently dropped).
/// Streams are wss URLs and may carry a path. Neither may hold user info, a query or a fragment, which could carry
/// secrets or change what is requested. Messages name the key only.
/// </summary>
public sealed class VenueHostValidation : IValidateOptions<VenueHostOptions>
{
    public ValidateOptionsResult Validate(string? name, VenueHostOptions options)
    {
        var failures = new List<string>();
        foreach (var (section, host) in options.Hosts())
        {
            if (host is null || Rest(host.BaseUrl) is null)
                failures.Add($"{VenueHostOptions.Section}:{section}:BaseUrl must be an absolute https origin without user info, path, query or fragment.");
            if (section == nameof(VenueHostOptions.Hyperliquid) && (host is null || Stream(host.StreamUrl) is null))
                failures.Add($"{VenueHostOptions.Section}:{section}:StreamUrl must be an absolute wss URL without user info, query or fragment.");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The normalized origin with a trailing slash, or null when unacceptable.</summary>
    internal static Uri? Rest(string? value)
    {
        var uri = Parse(value, Uri.UriSchemeHttps);
        return uri is null || uri.AbsolutePath != "/" ? null : new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    internal static Uri? Stream(string? value) => Parse(value, Uri.UriSchemeWss);

    private static Uri? Parse(string? value, string scheme)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        // Raw checks as well as parsed ones: an empty "?" or "#" parses to an empty component.
        if (uri.Scheme != scheme || uri.UserInfo.Length > 0 || value.Contains('@') || value.Contains('?') || value.Contains('#') ||
            string.IsNullOrEmpty(uri.Host)) return null;
        return uri;
    }
}
