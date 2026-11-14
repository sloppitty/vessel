namespace Vessel.Application.Venues;

/// <summary>The one rule for "the venue read failed", shared by every use case that degrades instead of failing.</summary>
public static class VenueFailures
{
    /// <summary>
    /// A venue or transport failure, or the read's own deadline. A cancellation the caller asked for is not a venue
    /// failure and must keep propagating.
    /// </summary>
    public static bool IsReadFailure(Exception error, CancellationToken ct) =>
        error is VenueReadException or HttpRequestException ||
        error is OperationCanceledException && !ct.IsCancellationRequested;
}
