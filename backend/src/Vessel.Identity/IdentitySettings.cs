using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Vessel.Application.Common;
using Vessel.Application.Ownership;

namespace Vessel.Identity;

public static class IdentityRegistrationGate
{
    public const bool MultiOwnerReady = true;
}

public sealed record IdentitySettings
{
    public bool Development { get; init; }
    public Guid OwnerId { get; init; } = JournalOwner.Default;
    public string OwnerName { get; init; } = "Owner";
    public string Schema { get; init; } = "identity";
    public string[] Origins { get; init; } = [];
    public string Registration { get; init; } = "Invitation";
    public string[] AllowList { get; init; } = [];
    public int IdleHours { get; init; } = 12;
    public int AbsoluteHours { get; init; } = 24;
    public int RememberIdleDays { get; init; } = 10;
    public int RememberAbsoluteDays { get; init; } = 30;
    public int MaxSessionsPerUser { get; init; } = 20;
    public int MaxPendingRegistrations { get; init; } = 10000;
    public int MaxEventStreams { get; init; } = 64;
    public string CookieName { get; init; } = "__Host-vessel-session";
    public string SignInCookieName { get; init; } = "__Host-vessel-signin";
    public bool LegacyOwnerOverride { get; init; }

    public static IdentitySettings Read(IConfiguration config, bool development)
    {
        // Wallet sign-in is the only authentication. The retired Token mode's settings refuse startup rather than
        // being ignored, so an installation that still configures them is told to finish moving to wallets.
        if (config["Vessel:Auth:Mode"] is { } mode && mode != "Wallet") throw Retired("Vessel:Auth:Mode");
        if (!string.IsNullOrEmpty(config["Vessel:Auth:Token"])) throw Retired("Vessel:Auth:Token");
        var settings = new IdentitySettings();
        config.GetSection("Vessel:Identity").Bind(settings);
        settings = settings with
        {
            Development = development,
            OwnerId = WalletOwner(config["Vessel:Auth:OwnerId"]),
            OwnerName = config["Vessel:Auth:OwnerName"] ?? "Owner",
            IdleHours = Number(config, "Vessel:Identity:Session:IdleHours", 12, 1, 720),
            AbsoluteHours = Number(config, "Vessel:Identity:Session:AbsoluteHours", 24, 1, 720),
            RememberIdleDays = Number(config, "Vessel:Identity:Session:RememberIdleDays", 10, 1, 365),
            RememberAbsoluteDays = Number(config, "Vessel:Identity:Session:RememberAbsoluteDays", 30, 1, 365),
            MaxSessionsPerUser = Number(config, "Vessel:Identity:MaxSessionsPerUser", 20, 1, 200),
            MaxPendingRegistrations = Number(config, "Vessel:Identity:MaxPendingRegistrations", 10000, 1, int.MaxValue),
            MaxEventStreams = Number(config, "Vessel:Events:MaxStreams", 64, 1, 1024),
            LegacyOwnerOverride = config.GetValue<bool>("Vessel:Identity:AllowLegacyDefaultOwner")
        };
        _ = new IdentitySchema(settings.Schema);
        if (settings.Registration is not ("Closed" or "Invitation" or "AllowList" or "Open"))
            throw Invalid("Vessel:Identity:Registration");
        if (settings.IdleHours > settings.AbsoluteHours || settings.RememberIdleDays > settings.RememberAbsoluteDays)
            throw Invalid("Vessel:Identity:Session");
        if (!ValidCookie(settings.CookieName) || !ValidCookie(settings.SignInCookieName) || settings.CookieName == settings.SignInCookieName)
            throw Invalid("Vessel:Identity:CookieName / Vessel:Identity:SignInCookieName");
        var origins = settings.Origins.Where(origin => origin is not null)
            .Select(origin => NormalizeOrigin(origin) ?? throw Invalid("Vessel:Identity:Origins")).Distinct().ToArray();
        string[] allowList;
        try { allowList = settings.AllowList.Select(address => WalletSignatures.NormalizeAddress(address)).Distinct().ToArray(); }
        catch (ProblemException) { throw Invalid("Vessel:Identity:AllowList"); }
        if (!development && config["Vessel:Auth:OwnerId"] is null) throw Invalid("Vessel:Auth:OwnerId");
        if (!development && settings.OwnerId == JournalOwner.Default && !settings.LegacyOwnerOverride)
            throw Invalid("Vessel:Auth:OwnerId / Vessel:Identity:AllowLegacyDefaultOwner");
        if (origins.Length == 0 || (!development && origins.Any(origin => !origin.StartsWith("https://", StringComparison.Ordinal))))
            throw Invalid("Vessel:Identity:Origins");
        try { _ = IdentityText.DisplayName(settings.OwnerName); }
        catch (ProblemException) { throw Invalid("Vessel:Auth:OwnerName"); }
        return settings with
        {
            Origins = origins, AllowList = allowList,
            Registration = IdentityRegistrationGate.MultiOwnerReady ? settings.Registration : "Closed"
        };
    }

    public bool AcceptsOrigin(string? origin) => origin is not null && Origins.Contains(origin, StringComparer.Ordinal);
    public TimeSpan IdleLifetime(bool remember) => remember ? TimeSpan.FromDays(RememberIdleDays) : TimeSpan.FromHours(IdleHours);
    public TimeSpan AbsoluteLifetime(bool remember) => remember ? TimeSpan.FromDays(RememberAbsoluteDays) : TimeSpan.FromHours(AbsoluteHours);

    public static string? NormalizeOrigin(string? origin)
    {
        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    private static int Number(IConfiguration config, string key, int fallback, int min, int max)
    {
        var text = config[key];
        if (text is null) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw Invalid(key);
        return value;
    }

    private static bool ValidCookie(string name) => name.StartsWith("__Host-", StringComparison.Ordinal) &&
        name.Length > 7 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static Guid WalletOwner(string? configured)
    {
        try { return JournalOwner.Parse(configured); }
        catch (InvalidOperationException) { throw Invalid("Vessel:Auth:OwnerId"); }
    }
    private static InvalidOperationException Invalid(string key) => new($"{key} is invalid for wallet sign-in.");
    private static InvalidOperationException Retired(string key) =>
        new($"{key} must not be set: Token sign-in was removed and Vessel signs in with wallets only.");
}

public sealed class IdentitySchema
{
    public string Name { get; }
    public IdentitySchema(string name)
    {
        if (!Regex.IsMatch(name, "^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Vessel:Identity:Schema must be a lower-case PostgreSQL identifier.");
        Name = name;
    }
}
