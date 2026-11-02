namespace Vessel.Application.Plays.Execution;

/// <summary><c>Vessel:Plays</c> settings for linking (Phase 2, item 4).</summary>
public sealed class PlayExecutionOptions
{
    public const string Section = "Vessel:Plays";

    /// <summary>
    /// How long before a Play was created a compatible retained order is still offered for the owner to link, as "placed
    /// before this Play was saved". The automatic matcher never reaches back; this only widens the owner's candidates.
    /// </summary>
    public TimeSpan EarlierOrderLookback { get; set; } = TimeSpan.FromHours(24);

    public void Validate()
    {
        if (EarlierOrderLookback < TimeSpan.Zero || EarlierOrderLookback > TimeSpan.FromDays(7))
            throw new InvalidOperationException($"{Section}:EarlierOrderLookback must be between 0 and 7 days.");
    }
}
