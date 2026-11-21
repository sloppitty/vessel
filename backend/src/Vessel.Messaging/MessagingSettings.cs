namespace Vessel.Messaging;

/// <summary><c>Vessel:Messaging</c>: which modules are compiled in. Delivery itself needs no settings.</summary>
public sealed class MessagingSettings
{
    public const string Section = "Vessel:Messaging";
    /// <summary>Compiles the verification subscription into the registry, for release checks and tests only.</summary>
    public bool Verification { get; set; }
}

/// <summary>Validated module, subscription and deployment names.</summary>
public static class MessagingNames
{
    public static bool ValidName(string name) => name.Length is >= 1 and <= 48 &&
        char.IsAsciiLetterLower(name[0]) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
