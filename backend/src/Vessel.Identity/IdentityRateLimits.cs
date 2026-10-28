using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Vessel.Identity;

public sealed class IdentityRateLimits : IDisposable
{
    public PartitionedRateLimiter<HttpContext> Requests { get; } = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, global: false)),
        PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, global: true)));
    private readonly PartitionedRateLimiter<HttpContext> invitations = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(Client(context.Connection.RemoteIpAddress), _ => Window(10, TimeSpan.FromHours(1))));

    public static string Client(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 ? Convert.ToHexString(bytes.AsSpan(0, 8)) : address.ToString();
    }

    private static RateLimitPartition<string> Partition(HttpContext context, bool global)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) return RateLimitPartition.GetNoLimiter("none");
        var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
        if (path is "/api/auth/step-up/challenge" or "/api/auth/step-up" or "/api/auth/wallets/challenge" or "/api/auth/wallets")
        {
            var session = context.User.FindFirst(SessionAuthentication.SessionClaim)?.Value;
            var bucket = path is "/api/auth/step-up/challenge" or "/api/auth/step-up" ? "step-up:" : "link:";
            return global || session is null ? RateLimitPartition.GetNoLimiter("none") :
                RateLimitPartition.GetFixedWindowLimiter(bucket + session, _ => Window(10, TimeSpan.FromMinutes(10)));
        }
        // Create and approve spend one per-session bucket, independent of the step-up and link buckets.
        if (path == "/api/auth/pairings" || (path is not null && path.StartsWith("/api/auth/pairings/", StringComparison.Ordinal) &&
            path.EndsWith("/approve", StringComparison.Ordinal)))
        {
            var session = context.User.FindFirst(SessionAuthentication.SessionClaim)?.Value;
            return global || session is null ? RateLimitPartition.GetNoLimiter("none") :
                RateLimitPartition.GetFixedWindowLimiter("pair:" + session, _ => Window(10, TimeSpan.FromMinutes(10)));
        }
        var kind = path switch
        {
            "/api/auth/challenge" => "challenge",
            "/api/auth/verify" or "/api/auth/register" => "complete",
            "/api/auth/pairings/claim" => "claim",
            "/api/auth/pairings/complete" => "pairing-complete",
            _ => null
        };
        if (kind is null) return RateLimitPartition.GetNoLimiter("none");
        // The phone polls complete every 2 s, so it has a per-client limit only; claim also has a global one that bounds typed-code guessing.
        if (global && kind == "pairing-complete") return RateLimitPartition.GetNoLimiter("none");
        var cap = global ? kind switch { "challenge" => 300, "claim" => 300, _ => 600 } :
            kind switch { "challenge" => 10, "claim" => 10, "pairing-complete" => 60, _ => 20 };
        return RateLimitPartition.GetFixedWindowLimiter(global ? kind : kind + ":" + Client(context.Connection.RemoteIpAddress),
            _ => Window(cap, TimeSpan.FromMinutes(1)));
    }

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) =>
        new() { PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true };

    public void CheckInvitation(HttpContext context)
    {
        using var lease = invitations.AttemptAcquire(context);
        if (!lease.IsAcquired) throw IdentityErrors.Error(429, "rate-limited", RetryAfter(lease));
    }

    public static int RetryAfter(RateLimitLease lease) => lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
        ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 60;

    public void Dispose() { Requests.Dispose(); invitations.Dispose(); }
}
