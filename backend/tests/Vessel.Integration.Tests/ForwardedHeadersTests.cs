using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vessel.Api;

namespace Vessel.Tests;

public sealed class ForwardedHeadersTests
{
    private static ForwardedHeadersOptions Configure(params (string Key, string? Value)[] entries)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value)).Build();
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersConfiguration.Configure(options, config);
        return options;
    }

    [Fact]
    public void Default_trust_is_loopback_only_and_keeps_the_single_hop_limit()
    {
        var options = Configure();
        Assert.Equal(1, options.ForwardLimit);
        Assert.Single(options.KnownProxies);
        Assert.All(options.KnownProxies, address => Assert.True(IPAddress.IsLoopback(address)));
        Assert.Single(options.KnownIPNetworks);
        Assert.True(options.KnownIPNetworks[0].Contains(IPAddress.Loopback));
    }

    [Theory]
    [InlineData("KnownNetworks:0", "0.0.0.0/0")]
    [InlineData("KnownNetworks:0", "::/0")]
    [InlineData("KnownNetworks:0", "172.30.86.0/33")]
    [InlineData("KnownNetworks:0", "172.30.86.0")]
    [InlineData("KnownNetworks:0", "not-a-network-secret")]
    [InlineData("KnownProxies:0", "0.0.0.0")]
    [InlineData("KnownProxies:0", "::")]
    [InlineData("KnownProxies:0", "proxy.example.invalid")]
    [InlineData("ForwardLimit", "0")]
    [InlineData("ForwardLimit", "6")]
    [InlineData("ForwardLimit", "unlimited")]
    public void Invalid_configuration_fails_without_echoing_values(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Configure(($"Vessel:ForwardedHeaders:{key}", value)));
        Assert.DoesNotContain(value, error.Message);
    }

    [Theory]
    [InlineData("172.30.86.10", false, false)]
    [InlineData("172.30.86.10", true, true)]
    [InlineData("192.0.2.10", true, false)]
    [InlineData("::ffff:172.30.86.10", true, true)]
    [InlineData("172.30.86.1", true, false)]
    [InlineData("172.30.86.11", true, false)]
    [InlineData("127.0.0.1", true, false)]
    [InlineData("::1", true, false)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    public async Task Only_trusted_peers_can_change_https_and_the_limiter_client_ip(string peer, bool configured, bool trusted)
    {
        var options = configured ? Configure(("Vessel:ForwardedHeaders:KnownProxies:0", "172.30.86.10")) : Configure();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(context);
        Assert.Equal(trusted, context.Request.IsHttps);
        Assert.Equal(IPAddress.Parse(trusted ? "198.51.100.7" : peer), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task Compose_trust_is_exactly_web_and_consumes_only_the_sanitized_hop()
    {
        var options = Configure(("Vessel:ForwardedHeaders:KnownProxies:0", "172.30.86.10"),
            ("Vessel:ForwardedHeaders:ForwardLimit", "1"));
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal(IPAddress.Parse("172.30.86.10"), Assert.Single(options.KnownProxies));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.30.86.10");
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https, http";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.99, 198.51.100.7";
        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options)).Invoke(context);
        Assert.False(context.Request.IsHttps);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), context.Connection.RemoteIpAddress);
        Assert.Equal("203.0.113.99", context.Request.Headers["X-Forwarded-For"]);
    }

    [Fact]
    public async Task Two_hops_require_both_proxies_to_be_trusted_and_never_consume_a_client_spoof()
    {
        var options = Configure(("Vessel:ForwardedHeaders:KnownNetworks:0", "172.30.86.0/24"),
            ("Vessel:ForwardedHeaders:KnownProxies:0", "192.0.2.10"), ("Vessel:ForwardedHeaders:ForwardLimit", "2"));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.30.86.10");
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "http, https, https";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.99, 198.51.100.7, 192.0.2.10";
        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options)).Invoke(context);
        Assert.True(context.Request.IsHttps);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), context.Connection.RemoteIpAddress);
        Assert.Equal("203.0.113.99", context.Request.Headers["X-Forwarded-For"]);
    }
}
