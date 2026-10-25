using System.Security.Claims;
using System.Data.Common;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Application.Common;
using Vessel.Application.Ownership;

namespace Vessel.Identity;

public sealed class SessionPrincipal : IPrincipalValidity
{
    public AuthenticatedSession? Authenticated { get; set; }
    public string? ErrorCode { get; set; }
    public int ErrorStatusCode { get; set; } = 401;
    private readonly SessionStore store;

    public SessionPrincipal(SessionStore store) => this.store = store;
    public ValueTask<bool> StillValidAsync(CancellationToken cancellationToken) =>
        Authenticated is { } session ? new(store.StillValidAsync(session.Session.Id, cancellationToken)) : ValueTask.FromResult(false);
}

public sealed class SessionAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> schemes, ILoggerFactory logger, UrlEncoder encoder,
    SessionStore sessions, IdentityCookies cookies, SessionPrincipal principal)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    public const string SchemeName = "VesselSession";
    public const string SessionClaim = "vessel:session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (IdentityHttp.PublicRoute(Request.Path) || Request.Path.StartsWithSegments("/health"))
            return AuthenticateResult.NoResult();
        var token = cookies.SessionToken(Context);
        if (token is null) return AuthenticateResult.NoResult();
        try
        {
            var authenticated = await sessions.AuthenticateAsync(token, refresh: true, Context.RequestAborted);
            principal.Authenticated = authenticated;
            // The journal owner claims (JournalOwnerClaims reads them back): the user ID is the owner.
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, authenticated.User.Id.ToString()),
                new Claim(ClaimTypes.Name, authenticated.User.DisplayName),
                new Claim(SessionClaim, authenticated.Session.Id.ToString())], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        catch (ProblemException error)
        {
            principal.ErrorCode = error.Code;
            principal.ErrorStatusCode = error.StatusCode;
            return AuthenticateResult.Fail("The session is not valid.");
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            throw IdentityErrors.Error(503, "identity-unavailable");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = principal.ErrorCode ?? "session-required";
        throw IdentityErrors.Error(principal.ErrorStatusCode, code);
    }
}

public sealed class IdentityCookies(IdentitySettings settings)
{
    private bool PlainHttp(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0) origin = $"http://{context.Request.Host}";
        return settings.Development && !context.Request.IsHttps && origin.StartsWith("http://", StringComparison.Ordinal) &&
            settings.AcceptsOrigin(origin);
    }

    public string SessionName(HttpContext context) => PlainHttp(context) ? "vessel-session" : settings.CookieName;
    public string BindingName(HttpContext context) => PlainHttp(context) ? "vessel-signin" : settings.SignInCookieName;
    public string? SessionToken(HttpContext context)
    {
        // Reads do not depend on Origin: GET may omit it, and a bad write origin must reach the request guard.
        var secure = context.Request.Cookies[settings.CookieName];
        if (secure is not null) return secure;
        return settings.Development && !context.Request.IsHttps && settings.Origins.Any(origin => origin.StartsWith("http://", StringComparison.Ordinal))
            ? context.Request.Cookies["vessel-session"] : null;
    }
    public string? BindingToken(HttpContext context) => context.Request.Cookies[BindingName(context)];

    private CookieOptions Options(HttpContext context, TimeSpan? maxAge = null) => new()
    {
        HttpOnly = true, Secure = !PlainHttp(context), SameSite = SameSiteMode.Strict, Path = "/", MaxAge = maxAge,
        IsEssential = true
    };

    public void SetBinding(HttpContext context, string token) =>
        context.Response.Cookies.Append(BindingName(context), token, Options(context, TimeSpan.FromMinutes(10)));
    public void ClearBinding(HttpContext context) => context.Response.Cookies.Delete(BindingName(context), Options(context));
    public void SetSession(HttpContext context, IssuedSession issued) =>
        context.Response.Cookies.Append(SessionName(context), issued.Token,
            Options(context, issued.Session.Session.Remember ? settings.AbsoluteLifetime(true) : null));
    public void ClearSession(HttpContext context) => context.Response.Cookies.Delete(SessionName(context), Options(context));
}
