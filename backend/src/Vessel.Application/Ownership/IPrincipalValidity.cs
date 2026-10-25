namespace Vessel.Application.Ownership;

/// <summary>
/// Whether the current request's principal may still receive data. Long-lived responses (event and market streams)
/// re-check it on a timer (at least every 15 seconds, regardless of traffic) and end when it turns false, so sign-out,
/// revocation and suspension reach streams that are already open. The session check captures the session that
/// authenticated the request (not the latest cookie), reads fresh state on every call without tracked entities,
/// never extends idle expiry, and returns false when the check itself fails.
/// </summary>
public interface IPrincipalValidity
{
    ValueTask<bool> StillValidAsync(CancellationToken cancellationToken);
}
