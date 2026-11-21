using System.Security.Cryptography;
using System.Text;

namespace Vessel.Persistence.Events.Subscriptions;

/// <summary>Starting defaults from the monitor plan. A module may tighten them per subscription.</summary>
public sealed record EventSubscriptionLimits
{
    /// <summary>Attempt cap shared by live delivery and catch-up.</summary>
    public int MaxAttempts { get; init; } = 12;
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan RetryCap { get; init; } = TimeSpan.FromHours(1);
    public double RetryJitter { get; init; } = 0.2;
    /// <summary>Catch-up discovery waits this long after detection so it does not race live delivery.</summary>
    public TimeSpan DiscoveryGrace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CatchUpInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int BatchSize { get; init; } = 100;
    /// <summary>The most extra attempts one explicit admin retry may grant.</summary>
    public int MaxAdminRetryBudget { get; init; } = 12;
    /// <summary>Events with a higher payload version are recorded as a visible subscription failure.</summary>
    public int SupportedVersion { get; init; } = 1;

    public static EventSubscriptionLimits Default { get; } = new();

    internal bool Valid() => MaxAttempts is >= 1 and <= 100 && RetryBase > TimeSpan.Zero && RetryCap >= RetryBase &&
        RetryJitter is >= 0 and < 1 && DiscoveryGrace >= TimeSpan.Zero && CatchUpInterval >= TimeSpan.FromSeconds(1) &&
        BatchSize is >= 1 and <= 1000 && MaxAdminRetryBudget is >= 1 and <= 100 && SupportedVersion >= 1;

    /// <summary>Backoff after the given number of failed attempts: base doubling to the cap, with symmetric jitter.</summary>
    public TimeSpan RetryDelay(int failedAttempts, double unitRandom)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 30);
        var delay = Math.Min(RetryCap.TotalMilliseconds, RetryBase.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(delay * (1 + RetryJitter * (2 * unitRandom - 1)));
    }
}

/// <summary>
/// A subscriber's declaration: its name, owning module and immutable topic filter. Live delivery and catch-up match
/// events against the same filters; PostgreSQL records the fingerprint, and a different filter needs a different name.
/// </summary>
public sealed record EventSubscription
{
    public EventSubscription(string name, string module, IReadOnlyList<string> filters, EventSubscriptionLimits? limits = null)
    {
        if (!ValidName(name) || !ValidName(module) || filters is not { Count: >= 1 and <= 32 } || !filters.All(TopicFilter.Valid))
            throw new ArgumentException("Invalid event subscription declaration.");
        Name = name; Module = module;
        Filters = filters.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Limits = limits ?? EventSubscriptionLimits.Default;
        if (!Limits.Valid()) throw new ArgumentException("Invalid event subscription limits.");
    }

    public string Name { get; }
    public string Module { get; }
    public IReadOnlyList<string> Filters { get; }
    public EventSubscriptionLimits Limits { get; }

    public string Fingerprint => Fingerprints(Filters);

    public static string Fingerprints(IEnumerable<string> filters) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', filters.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))));

    public bool Matches(string type) => Filters.Any(filter => TopicFilter.Matches(filter, type));

    internal static bool ValidName(string name) => name.Length is >= 1 and <= 48 && char.IsAsciiLetterLower(name[0]) &&
        name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}

/// <summary>AMQP topic semantics: words split on '.', '*' matches one word and '#' zero or more.</summary>
public static class TopicFilter
{
    public static bool Valid(string filter) => filter.Length is >= 1 and <= 200 && filter.Split('.').All(word =>
        word is "*" or "#" || word.Length >= 1 && word.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'));

    public static bool Matches(string filter, string routingKey) => Matches(filter.Split('.'), 0, routingKey.Split('.'), 0);

    private static bool Matches(string[] pattern, int p, string[] words, int w)
    {
        if (p == pattern.Length) return w == words.Length;
        if (pattern[p] == "#")
        {
            for (var skip = w; skip <= words.Length; skip++)
                if (Matches(pattern, p + 1, words, skip)) return true;
            return false;
        }
        return w < words.Length && (pattern[p] == "*" || pattern[p] == words[w]) && Matches(pattern, p + 1, words, w + 1);
    }
}
