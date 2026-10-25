using System.Globalization;

namespace Vessel.Application.Ownership;

/// <summary>
/// The configured installation owner: the operator that <c>identity ensure-owner</c> creates with this ID, so the
/// journal that existed before wallet sign-in keeps its owner. Every host and tool reads the same key and default.
/// </summary>
public static class JournalOwner
{
    public const string ConfigurationKey = "Vessel:Auth:OwnerId";
    public static readonly Guid Default = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Parses a configured owner; absent means the default, and an empty or invalid value is rejected.</summary>
    public static Guid Parse(string? configured)
    {
        if (configured is null) return Default;
        if (!Guid.TryParse(configured, CultureInfo.InvariantCulture, out var owner) || owner == Guid.Empty)
            throw new InvalidOperationException("Vessel:Auth:OwnerId must be a non-empty GUID.");
        return owner;
    }
}
