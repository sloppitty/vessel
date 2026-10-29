using Vessel.Application.Ownership;

namespace Vessel.Tests;

/// <summary>A test stream principal that is never revoked.</summary>
internal sealed class AlwaysValidPrincipal : IPrincipalValidity
{
    public ValueTask<bool> StillValidAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
