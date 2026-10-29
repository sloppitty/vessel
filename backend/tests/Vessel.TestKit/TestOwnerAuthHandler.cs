using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
