using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Credentials;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

internal static class SecurityFixture
{
    public const string Token = "ro:123:single:1999999999:abcdef1234567890";
    public static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)83, 32).ToArray());
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    public static readonly TimeProvider Clock = new SecurityClock();
    public static readonly string Address = "0x" + new string('a', 40);
    public static readonly VenueDescriptor Venue = new("index-fixture", "Index venue", "read-only", VenueSources.AccountIndex,
        new(true, false, true, false, false, false, false, AccountDiscovery: true, ReadOnlyCredential: true));
    public static VenueRegistry Registry(params object[] adapters) => new([VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor, Venue],
        adapters.OfType<IPerpetualVenueReader>(), adapters.OfType<IVenueOrderReader>(), [], [], [],
        adapters.OfType<IVenueAccountDiscovery>(), adapters.OfType<IVenueCredentialVerifier>());
    public static AesGcmCredentialVault Vault() => new(new() { ActiveKeyId = "v1", Keys = new() { ["v1"] = Key } });
    public static Account Account(Guid owner, string source = "123")
    {
        var account = new Account(Guid.NewGuid(), owner, Venue.Id, "Index account"); account.Configure(null, null, null, source); return account;
    }
    public static AccountCredentialService Service(Guid owner, IWorkspaceStore accounts, IAccountCredentialStore credentials, ICredentialVault vault, SecurityVerifier? verifier = null, Vessel.Application.Ownership.IUnitOfWork? unitOfWork = null) =>
        new(accounts, credentials, new CoreOwner(owner), Registry(verifier ?? new()), vault, Clock, unitOfWork ?? TestUnitOfWork.None, MonitorServerState.Reads(time: Clock));
    private sealed class SecurityClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
