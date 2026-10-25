using System.Security.Claims;
using Vessel.Application.Metadata;

namespace Vessel.Infrastructure.Auth;

/// <summary>
/// Reads the journal owner back from the signed-in principal. The wallet session scheme issues the user's ID as
/// <see cref="ClaimTypes.NameIdentifier"/> and the display name as <see cref="ClaimTypes.Name"/>; the user ID is the owner.
/// </summary>
public static class JournalOwnerClaims
{
    /// <summary>The authenticated owner's ID. Throws when the principal is not an authenticated owner.</summary>
    public static Guid OwnerId(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true &&
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) &&
        id != Guid.Empty ? id : throw MissingOwner();

    /// <summary>The authenticated owner's ID and display name.</summary>
    public static JournalOwner Owner(ClaimsPrincipal? user) =>
        new(OwnerId(user), user!.FindFirstValue(ClaimTypes.Name) is { Length: > 0 } name ? name : throw MissingOwner());

    private static UnauthorizedAccessException MissingOwner() => new("An authenticated owner is required.");
}
