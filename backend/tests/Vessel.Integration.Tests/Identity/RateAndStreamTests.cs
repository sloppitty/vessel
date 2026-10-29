using System.Diagnostics;
using System.Security.Claims;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;
using Vessel.Api;
using Vessel.Application.Ownership;
using Vessel.Application.MarketData;
using Vessel.Domain.Accounts;
using Vessel.Identity;
using Vessel.Persistence.Events;

namespace Vessel.Tests.Identity;

public sealed class RateLimitTests
{
    [Fact]
    public void Client_partitions_group_IPv6_by_64_and_normalize_mapped_IPv4()
    {
        Assert.Equal(IdentityRateLimits.Client(IPAddress.Parse("2001:db8:1234:abcd::1")),
            IdentityRateLimits.Client(IPAddress.Parse("2001:db8:1234:abcd::ffff")));
        Assert.NotEqual(IdentityRateLimits.Client(IPAddress.Parse("2001:db8:1234:abcd::1")),
            IdentityRateLimits.Client(IPAddress.Parse("2001:db8:1234:abce::1")));
        Assert.Equal(IdentityRateLimits.Client(IPAddress.Parse("192.0.2.1")), IdentityRateLimits.Client(IPAddress.Parse("::ffff:192.0.2.1")));
    }

    [Fact]
    public void Client_and_global_limits_are_both_enforced_without_wallet_address_partitions()
    {
        using var limits = new IdentityRateLimits();
        static DefaultHttpContext Context(string path, string address)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path; context.Request.Method = "POST"; context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            return context;
        }
        var one = Context("/api/auth/challenge", "192.0.2.1");
        for (var i = 0; i < 10; i++) using (var lease = limits.Requests.AttemptAcquire(one)) Assert.True(lease.IsAcquired);
        using (var lease = limits.Requests.AttemptAcquire(one)) Assert.False(lease.IsAcquired);
        var other = Context("/api/auth/challenge", "192.0.2.2");
        using (var lease = limits.Requests.AttemptAcquire(other)) Assert.True(lease.IsAcquired);
        for (var i = 0; i < 289; i++)
        {
            using var lease = limits.Requests.AttemptAcquire(Context("/api/auth/challenge", $"10.{i / 256}.{i % 256}.1"));
            Assert.True(lease.IsAcquired);
        }
        using (var lease = limits.Requests.AttemptAcquire(Context("/api/auth/challenge", "192.0.2.3"))) Assert.False(lease.IsAcquired);
        var verify = Context("/api/auth/verify", "192.0.2.1");
        for (var i = 0; i < 20; i++) using (var lease = limits.Requests.AttemptAcquire(verify)) Assert.True(lease.IsAcquired);
        verify.Request.Path = "/api/auth/register";
        using (var lease = limits.Requests.AttemptAcquire(verify)) Assert.False(lease.IsAcquired);
        for (var i = 0; i < 10; i++) limits.CheckInvitation(verify);
        var error = Assert.Throws<Vessel.Application.Common.ProblemException>(() => limits.CheckInvitation(verify));
        Assert.Equal("rate-limited", error.Code);
        Assert.True(error.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task Missing_cookie_never_resolves_the_database_and_principal_checks_fail_closed()
    {
        var sessions = new SessionStore(() => throw new InvalidOperationException("Synthetic unavailable database"), new(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() => sessions.AuthenticateAsync(null, true, default));
        Assert.Equal("session-required", error.Code);
        Assert.False(await sessions.StillValidAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public void Step_up_and_link_limits_have_independent_session_buckets_not_IP_buckets()
    {
        using var limits = new IdentityRateLimits();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/step-up/challenge";
        context.User = new(new ClaimsIdentity([new Claim(SessionAuthentication.SessionClaim, Guid.NewGuid().ToString())], SessionAuthentication.SchemeName));
        for (var i = 0; i < 10; i++) using (var lease = limits.Requests.AttemptAcquire(context)) Assert.True(lease.IsAcquired);
        context.Request.Path = "/api/auth/step-up";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.3");
        using (var lease = limits.Requests.AttemptAcquire(context))
        {
            Assert.False(lease.IsAcquired);
            Assert.InRange(IdentityRateLimits.RetryAfter(lease), 1, 600);
        }
        context.Request.Path = "/api/auth/wallets";
        for (var i = 0; i < 10; i++) using (var lease = limits.Requests.AttemptAcquire(context)) Assert.True(lease.IsAcquired);
        context.Request.Path = "/api/auth/wallets/challenge";
        using (var lease = limits.Requests.AttemptAcquire(context))
        {
            Assert.False(lease.IsAcquired);
            Assert.InRange(IdentityRateLimits.RetryAfter(lease), 1, 600);
        }
        context.User = new(new ClaimsIdentity([new Claim(SessionAuthentication.SessionClaim, Guid.NewGuid().ToString())], SessionAuthentication.SchemeName));
        using (var lease = limits.Requests.AttemptAcquire(context)) Assert.True(lease.IsAcquired);
        context.Request.Path = "/api/auth/step-up/challenge";
        using (var lease = limits.Requests.AttemptAcquire(context)) Assert.True(lease.IsAcquired);
    }
}

[CollectionDefinition("Identity streams", DisableParallelization = true)]
public sealed class IdentityStreamCollection;

[Collection("Identity streams")]
public sealed class StreamRevalidationTests : StreamRevalidationTestsSupport
{

    [Fact]
    public async Task An_otherwise_idle_event_stream_stops_on_the_15_second_revalidation_timer()
    {
        var validity = new InvalidPrincipal();
        await using var provider = new ServiceCollection().AddSingleton<IPrincipalValidity>(validity)
            .AddSingleton(TimeProvider.System).BuildServiceProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider, RequestAborted = timeout.Token };
        context.Response.Body = body;
        var elapsed = Stopwatch.StartNew();
        await VesselEventEndpoints.StreamAsync("0", new EmptyReader(), new CoreOwner(Guid.NewGuid()),
            Options.Create(new VesselEventSettings { StreamPollSeconds = 60 }), Options.Create(new JsonOptions()), context);
        Assert.Equal(1, validity.Calls);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 14, 17);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task Revalidation_exceptions_end_the_response_instead_of_leaking_or_leaving_it_running()
    {
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        await using var validation = new PrincipalRevalidation(new ThrowingPrincipal(), clock, default);
        clock.Advance(TimeSpan.FromSeconds(15));
        await Eventually.True(() => validation.Token.IsCancellationRequested, "A failing revalidation must terminate the stream.");
        Assert.True(validation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_revalidation_check_that_never_completes_ends_the_response_at_its_deadline()
    {
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        var validity = new StalledPrincipal();
        await using var validation = new PrincipalRevalidation(validity, clock, default);
        clock.Advance(PrincipalRevalidation.CheckInterval);
        await Eventually.True(() => validity.Calls == 1, "The timer must start a check without traffic.");
        clock.Advance(PrincipalRevalidation.CheckTimeout - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.False(validation.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually.True(() => validation.Token.IsCancellationRequested,
            "A stalled check must fail closed at its deadline even when it ignores cancellation.");
        Assert.True(validity.Cancelled.IsCancellationRequested);
        Assert.Equal(1, validity.Calls);
    }

    [Fact]
    public async Task Ending_a_response_during_a_stalled_check_does_not_wait_for_the_check()
    {
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        var validity = new StalledPrincipal();
        var validation = new PrincipalRevalidation(validity, clock, default);
        clock.Advance(PrincipalRevalidation.CheckInterval);
        await Eventually.True(() => validity.Calls == 1, "The timer must start a check without traffic.");
        await validation.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Market_stream_revalidates_without_waiting_for_traffic_and_releases_its_subscription()
    {
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        store.Accounts.Add(account);
        var source = new FakeMarketStream();
        var limiter = new MarketStreamLimiter();
        var service = new MarketStreamService(store, TestVenues.With(source), limiter, TimeProvider.System);
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        var validity = new InvalidPrincipal();
        await using var provider = new ServiceCollection().AddSingleton<IPrincipalValidity>(validity)
            .AddSingleton<TimeProvider>(clock).BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = body;
        var stream = MarketStreamEndpoint.StreamAsync(account.Id, "BTC", "1m", service, Options.Create(new JsonOptions()), context);
        await Eventually.True(() => source.Subscriptions.Count > 0, "The market subscription must start.");
        Assert.False(stream.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(15));
        await stream.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, validity.Calls);
        Assert.True(source.Latest.Disposed);
        Assert.Equal(0, limiter.Active);
    }

    [PostgresFact]
    public async Task A_captured_wallet_principal_closes_an_open_event_stream_after_revocation()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        var principal = new SessionPrincipal(database.Sessions())
        {
            Authenticated = await database.Sessions().AuthenticateAsync(issued.Token, false, default)
        };
        var clock = new ManualTime(database.Clock.Now);
        await using var provider = new ServiceCollection().AddSingleton<IPrincipalValidity>(principal)
            .AddSingleton<TimeProvider>(clock).BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = body;
        var stream = VesselEventEndpoints.StreamAsync("0", new EmptyReader(), new CoreOwner(issued.Session.User.Id),
            Options.Create(new VesselEventSettings { StreamPollSeconds = 60 }), Options.Create(new JsonOptions()), context);
        Assert.False(stream.IsCompleted);
        await using (var db = database.CreateDbContext())
            await db.Sessions.ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, database.Clock.Now));
        clock.Advance(TimeSpan.FromSeconds(15));
        await stream.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [PostgresFact]
    public async Task A_revoked_session_stream_ends_at_the_check_deadline_while_the_sessions_table_is_locked()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        var principal = new CountingPrincipal(new SessionPrincipal(database.Sessions())
        {
            Authenticated = await database.Sessions().AuthenticateAsync(issued.Token, false, default)
        });
        var clock = new ManualTime(database.Clock.Now);
        await using var provider = new ServiceCollection().AddSingleton<IPrincipalValidity>(principal)
            .AddSingleton<TimeProvider>(clock).BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = body;
        var stream = VesselEventEndpoints.StreamAsync("0", new EmptyReader(), new CoreOwner(issued.Session.User.Id),
            Options.Create(new VesselEventSettings { StreamPollSeconds = 60 }), Options.Create(new JsonOptions()), context);
        await using var locker = database.CreateDbContext();
        await locker.Sessions.ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, database.Clock.Now));
        await using var transaction = await locker.Database.BeginTransactionAsync();
        var sessions = $"\"{database.Schema.Name}\".\"sessions\"";
        var lockTable = "LOCK TABLE " + sessions + " IN ACCESS EXCLUSIVE MODE";
        await locker.Database.ExecuteSqlRawAsync(lockTable);
        clock.Advance(PrincipalRevalidation.CheckInterval);
        await Eventually.True(() => principal.Calls == 1, "The timer must start a check without traffic.");
        await Eventually.True(() => Waiting(locker, sessions) > 0, "The check must block on the locked sessions table.");
        Assert.False(stream.IsCompleted);
        clock.Advance(PrincipalRevalidation.CheckTimeout);
        await stream.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.True(() => Waiting(locker, sessions) == 0, "The deadline must cancel the blocked database command.");
        await transaction.RollbackAsync();
    }

    private static int Waiting(IdentityDbContext db, string table)
    {
        var sql = "SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE NOT granted AND relation = '" + table + "'::regclass";
        return db.Database.SqlQueryRaw<int>(sql).AsEnumerable().Single();
    }
    private sealed class InvalidPrincipal : IPrincipalValidity
    {
        public int Calls { get; private set; }
        public ValueTask<bool> StillValidAsync(CancellationToken cancellationToken) { Calls++; return ValueTask.FromResult(false); }
    }
    private sealed class StalledPrincipal : IPrincipalValidity
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public CancellationToken Cancelled { get; private set; }
        // Never completes, even when cancelled, like a database read stuck behind a partition.
        public ValueTask<bool> StillValidAsync(CancellationToken cancellationToken)
        {
            Cancelled = cancellationToken;
            Interlocked.Increment(ref calls);
            return new(new TaskCompletionSource<bool>().Task);
        }
    }
    private sealed class CountingPrincipal(IPrincipalValidity inner) : IPrincipalValidity
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ValueTask<bool> StillValidAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return inner.StillValidAsync(cancellationToken);
        }
    }
    private sealed class ThrowingPrincipal : IPrincipalValidity
    {
        public ValueTask<bool> StillValidAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Synthetic outage");
    }
}
