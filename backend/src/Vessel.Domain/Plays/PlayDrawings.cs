using Vessel.Domain.Common;

namespace Vessel.Domain.Plays;

/// <summary>
/// A Play's chart drawings, an application-validated JSON document keyed by instrument, in their own row (issue #110) so
/// that list, Review, Sizing and export reads of Plays never load them. Only the Play editor and the chart read them. A
/// Play without a row has no drawings (<c>{}</c>). Changes go through <see cref="Play.ReplaceDrawings"/>, which advances
/// the Play's version, so the Play's optimistic concurrency covers them.
/// </summary>
public sealed class PlayDrawings : IOwned
{
    public const string Empty = "{}";

    public Guid OwnerId { get; private set; }
    public Guid PlayId { get; private set; }
    public string Document { get; internal set; } = Empty;
    private PlayDrawings() { }

    /// <summary>A Play's first drawings row, empty until <see cref="Play.ReplaceDrawings"/> sets it.</summary>
    public PlayDrawings(Play play)
    {
        ArgumentNullException.ThrowIfNull(play);
        OwnerId = play.OwnerId;
        PlayId = play.Id;
    }
}
