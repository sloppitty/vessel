using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ServerDiagnosticTests
{
    private const string Secret = "Authorization=Bearer private-token;Host=private-db;Password=private-password;raw-provider-body=private-value";
    private static readonly Guid PrivateId = Guid.Parse("12345678-1234-1234-1234-123456789abc");

    [Theory]
    [InlineData("overflow", 500)]
    [InlineData("invalid-operation", 500)]
    [InlineData("argument", 500)]
    [InlineData("database-network", 503)]
    [InlineData("database-update-network", 503)]
    [InlineData("database-query-bug", 500)]
    [InlineData("http-network", 503)]
    [InlineData("http-outage", 503)]
    [InlineData("http-bad-request", 500)]
    [InlineData("non-caller-cancellation", 500)]
    public async Task Unexpected_failure_has_safe_structured_diagnostic_and_generic_correlated_response(string kind, int status)
    {
        var error = kind switch
        {
            "overflow" => new OverflowException(Secret),
            "invalid-operation" => new InvalidOperationException(Secret),
            "argument" => new ArgumentException(Secret),
            "database-network" => new NpgsqlException(Secret, new IOException(Secret)),
            "database-update-network" => new DbUpdateException(Secret, new NpgsqlException(Secret, new IOException(Secret))),
            "database-query-bug" => new PostgresException(Secret, "ERROR", "ERROR", "42601", detail: Secret),
            "http-network" => new HttpRequestException(Secret),
            "http-outage" => new HttpRequestException(Secret, new Exception(Secret), HttpStatusCode.ServiceUnavailable),
            "http-bad-request" => new HttpRequestException(Secret, null, HttpStatusCode.BadRequest),
            _ => (Exception)new OperationCanceledException(Secret)
        };
        error.Data["private-data"] = Secret;
        error.Source = Secret;
        error.HelpLink = Secret;
        await using var factory = new DiagnosticFactory(error);
        using var client = factory.AuthorizedClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "private-untrusted-correlation");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{PrivateId}?address=private-wallet-address")
        {
            Content = new StringContent("private-request-body")
        };
        using var response = await client.SendAsync(request);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(status == 503 ? "The service is unavailable. Try again later." : "An unexpected error occurred.",
            problem.RootElement.GetProperty("detail").GetString());
        var traceId = problem.RootElement.GetProperty("traceId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.Equal(traceId, Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        var diagnostic = Assert.Single(factory.Logs.Entries);
        Assert.Equal(LogLevel.Error, diagnostic.Level);
        Assert.Equal("UnexpectedRequestFailure", diagnostic.EventId.Name);
        Assert.Null(diagnostic.Exception);
        Assert.Equal(traceId, diagnostic.Fields["TraceId"]);
        Assert.Equal("/api/accounts/{id:guid}", diagnostic.Fields["Operation"]);
        Assert.Equal(error.GetType().FullName, diagnostic.Fields["ExceptionType"]);
        Assert.Equal(status, diagnostic.Fields["StatusCode"]);
        Assert.Contains(nameof(ThrowingStore.AccountAsync), (string)diagnostic.Fields["SafeStack"]!);
        var output = body + diagnostic.Text + JsonSerializer.Serialize(diagnostic.Fields);
        foreach (var forbidden in new[] { Secret, "private-token", "private-db", "private-password", "private-value",
                     "private-data", PrivateId.ToString(), "private-wallet-address", "private-untrusted-correlation",
                     "private-request-body", "Authorization", "raw-provider-body",
                     "ConnectionStrings", ".cs:line", "/home/" })
            Assert.DoesNotContain(forbidden, output);
        Assert.DoesNotContain(error.GetType().Name, body);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(503)]
    public async Task Expected_workspace_errors_do_not_log_errors(int status)
    {
        await using var factory = new DiagnosticFactory(new ProblemException(status, "Expected safe detail."));
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync($"/api/accounts/{PrivateId}");
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Empty(factory.Logs.Entries);
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(true, 409)]
    public async Task Domain_failures_return_their_message_without_logging(bool rule, int status)
    {
        var detail = rule ? "Only a paused Play can be resumed." : "Titles are limited to 200 characters.";
        await using var factory = new DiagnosticFactory(rule ? new DomainRuleException(detail) : new DomainValidationException(detail));
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync($"/api/accounts/{PrivateId}");
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
        Assert.Empty(factory.Logs.Entries);
    }

    [Fact]
    public async Task Bad_request_does_not_log_errors()
    {
        await using var factory = new DiagnosticFactory(new Exception(Secret));
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsync("/api/accounts", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Logs.Entries);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_log_errors()
    {
        await using var factory = new DiagnosticFactory(new OperationCanceledException(Secret), cancelCaller: true);
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync($"/api/accounts/{PrivateId}");
        Assert.Empty(factory.Logs.Entries);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_database_configuration_remains_a_generic_dependency_outage()
    {
        await using var factory = new DiagnosticFactory(null);
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(503, Assert.Single(factory.Logs.Entries).Fields["StatusCode"]);
        Assert.DoesNotContain("ConnectionStrings", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("ConnectionStrings", factory.Logs.Entries.Single().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_database_configuration_does_not_depend_on_the_throwing_method(bool wrapped)
    {
        Exception error = new Vessel.Persistence.MissingDatabaseConfigurationException();
        if (wrapped) error = new InvalidOperationException(Secret, error);
        await using var factory = new DiagnosticFactory(error);
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync($"/api/accounts/{PrivateId}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var output = await response.Content.ReadAsStringAsync() + Assert.Single(factory.Logs.Entries).Text;
        Assert.DoesNotContain("ConnectionStrings", output);
        Assert.DoesNotContain(Secret, output);
    }

    [Fact]
    public async Task Unreachable_database_through_real_EF_is_a_503_without_framework_error_logs()
    {
        // Port 1 refuses immediately; EF wraps the transient NpgsqlException.
        const string connection = "Host=127.0.0.1;Port=1;Database=private-db-name;Username=private-user;Password=private-password;Timeout=2";
        await using var factory = new DiagnosticFactory(null, connection: connection);
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var diagnostic = Assert.Single(factory.Logs.Entries);
        Assert.Equal("UnexpectedRequestFailure", diagnostic.EventId.Name);
        Assert.Equal(503, diagnostic.Fields["StatusCode"]);
        var output = await response.Content.ReadAsStringAsync() + diagnostic.Text + JsonSerializer.Serialize(diagnostic.Fields);
        foreach (var forbidden in new[] { "private-db-name", "private-user", "private-password", "tcp://", "127.0.0.1:1" })
            Assert.DoesNotContain(forbidden, output);
    }

    private sealed class DiagnosticFactory(Exception? error, bool cancelCaller = false, string? connection = null) : WebApplicationFactory<Program>
    {
        private readonly Guid owner = Guid.NewGuid();
        public CaptureLoggerProvider Logs { get; } = new();
        protected override IHost CreateHost(IHostBuilder builder)
        {
            VesselEventTestHosting.ConfigureBootstrap(builder);
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vessel:Auth:OwnerId"] = owner.ToString(),
                ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
                ["Vessel:Evidence:Storage:Provider"] = "Local",
                ["ConnectionStrings:Vessel"] = connection
            }));
            builder.ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Error).AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                VesselEventTestHosting.ConfigureServices(services);
                TestAuthentication.Add(services);
                if (error is not null)
                {
                    services.AddSingleton<IWorkspaceStore>(new ThrowingStore(error));
                    services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
                }
                if (cancelCaller) services.AddSingleton<IStartupFilter>(new CancelCaller());
            });
        }
        public HttpClient AuthorizedClient()
        {
            var client = CreateClient();
            TestAuthentication.SignIn(client.DefaultRequestHeaders, owner);
            return client;
        }
    }

    private sealed class CancelCaller : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.RequestAborted = new CancellationToken(canceled: true);
                return continuation(context);
            });
            next(app);
        };
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception? Exception,
        string Text, Dictionary<string, object?> Fields);

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Entries);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                entries.Enqueue(new(level, eventId, exception, formatter(state, exception),
                    ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }
    }

    private sealed class ThrowingStore(Exception error) : IWorkspaceStore
    {
        private readonly MemoryWorkspaceStore other = new(Guid.NewGuid());
        public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => throw error;
        public Task<MarketDataAccount?> MarketDataAccountAsync(Guid id, CancellationToken ct) => throw error;
        public Task<AccountSnapshot?> SnapshotAsync(Guid id, CancellationToken ct) => other.SnapshotAsync(id, ct);
        public Task<bool> SourceExistsAsync(string venueId, string address, CancellationToken ct) => other.SourceExistsAsync(venueId, address, ct);
        public Task<List<Portfolio>> PortfoliosAsync(CancellationToken ct) => other.PortfoliosAsync(ct);
        public Task<List<Account>> AccountsAsync(CancellationToken ct) => other.AccountsAsync(ct);
        public Task<List<AccountSnapshot>> SnapshotsAsync(CancellationToken ct) => other.SnapshotsAsync(ct);
        public Task<List<ImportedFill>> FillsAsync(Guid? id, int limit, CancellationToken ct) => other.FillsAsync(id, limit, ct);
        public Task<int> FillCountAsync(CancellationToken ct) => other.FillCountAsync(ct);
        public Task AddPortfolioAsync(Portfolio portfolio, CancellationToken ct) => other.AddPortfolioAsync(portfolio, ct);
        public Task AddAccountAsync(Account account, CancellationToken ct) => other.AddAccountAsync(account, ct);
        public Task<T> WithAccountLockAsync<T>(Guid id, Func<Account, Task<T>> action, CancellationToken ct) => other.WithAccountLockAsync(id, action, ct);
        public Task SaveRefreshAsync(Account account, PerpetualVenueReadResult result, CancellationToken ct) => other.SaveRefreshAsync(account, result, ct);
        public Task<T> WithManagementLockAsync<T>(Func<Task<T>> action, CancellationToken ct) => other.WithManagementLockAsync(action, ct);
        public Task DeletePortfolioAsync(Guid id, CancellationToken ct) => other.DeletePortfolioAsync(id, ct);
        public Task DeleteAccountAsync(Account account, CancellationToken ct) => other.DeleteAccountAsync(account, ct);
        public Task SaveAsync(CancellationToken ct) => other.SaveAsync(ct);
    }
}
