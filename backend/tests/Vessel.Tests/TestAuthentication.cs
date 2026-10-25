using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Application.Ownership;
using Vessel.Identity;

namespace Vessel.Tests;

/// <summary>
/// Test-only authentication: the <see cref="Header"/> names the owner of each request and the handler issues the
/// owner claims the wallet session scheme issues, with the same authentication type. Not production code: the API
/// authenticates wallet sessions only, never a header. Requests without the header fall through to the real session
/// scheme (see <see cref="TestAuthentication"/>), so session cookies, challenges and the request guard still apply.
/// </summary>
internal sealed class TestOwnerAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "VesselTestOwner";
    public const string Header = "X-Vessel-Test-Owner";

    public static string NameOf(Guid owner) => "Owner " + owner.ToString("N")[..8];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!Guid.TryParseExact(value.ToString(), "D", out var owner) || owner == Guid.Empty)
            return Task.FromResult(AuthenticateResult.Fail("Invalid test owner."));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(TestAuthentication.Principal(owner, NameOf(owner)), SchemeName)));
    }
}

/// <summary>Registers <see cref="TestOwnerAuthHandler"/> beside the production session scheme of an API under test.</summary>
internal static class TestAuthentication
{
    public const string Origin = "https://vessel.test";
    private const string Selector = "VesselTestSelector";

    /// <summary>
    /// A request with the test header authenticates as that owner and its streams stay valid; any other request
    /// (sign-in, cookies, no credentials) goes to the session scheme exactly as in production.
    /// </summary>
    public static void Add(IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestOwnerAuthHandler>(TestOwnerAuthHandler.SchemeName, _ => { })
            .AddPolicyScheme(Selector, null, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(TestOwnerAuthHandler.Header) ? TestOwnerAuthHandler.SchemeName : SessionAuthentication.SchemeName);
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = Selector;
            options.DefaultAuthenticateScheme = Selector;
            options.DefaultChallengeScheme = Selector;
        });
        services.RemoveAll<IPrincipalValidity>();
        services.AddScoped<IPrincipalValidity>(sp =>
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers.ContainsKey(TestOwnerAuthHandler.Header) == true
                ? new AlwaysValidPrincipal() : sp.GetRequiredService<SessionPrincipal>());
    }

    /// <summary>The owner claims a wallet session issues, for code that reads a principal directly.</summary>
    public static ClaimsPrincipal Principal(Guid owner, string name) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, owner.ToString()),
        new Claim(ClaimTypes.Name, name)], SessionAuthentication.SchemeName));

    /// <summary>
    /// What a signed-in browser tab sends for <paramref name="owner"/>: the test credential, the tab's user binding
    /// (<c>X-Vessel-User</c>) and the accepted origin that guarded writes need.
    /// </summary>
    public static void SignIn(System.Net.Http.Headers.HttpRequestHeaders headers, Guid owner)
    {
        headers.Remove(TestOwnerAuthHandler.Header);
        headers.Remove("X-Vessel-User");
        headers.Remove("Origin");
        headers.Add(TestOwnerAuthHandler.Header, owner.ToString("D"));
        headers.Add("X-Vessel-User", owner.ToString("D"));
        headers.Add("Origin", Origin);
    }
}

/// <summary>A test stream principal that is never revoked.</summary>
internal sealed class AlwaysValidPrincipal : IPrincipalValidity
{
    public ValueTask<bool> StillValidAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

/// <summary>A test directory with one owner that is always active.</summary>
internal sealed class ConfiguredOwnerDirectory(Guid ownerId) : IJournalOwnerDirectory
{
    private readonly Guid owner = ownerId != Guid.Empty ? ownerId : throw new ArgumentException("Owner ID must be nonempty.", nameof(ownerId));

    public Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(ownerId == owner ? JournalOwnerStatus.Active : JournalOwnerStatus.Unknown);

    public Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        IReadOnlyList<Guid> owners = after is { } last && last.CompareTo(owner) >= 0 ? [] : [owner];
        return Task.FromResult(owners);
    }
}
