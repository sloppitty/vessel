using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Ownership;
using Vessel.Identity;

namespace Vessel.Tests;

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
