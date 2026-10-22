using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Vessel.Api;
using Vessel.Application.Common;
using Vessel.Domain.Common;
using Vessel.Infrastructure;
using Vessel.Persistence;
using Vessel.Messaging;
using Vessel.Persistence.Notifications;
using Vessel.Identity;

if (args.FirstOrDefault() is "identity")
    return IdentityCommands.RunAsync(args[1..], Console.Out, verify: Program.VerifyIdentityAsync).GetAwaiter().GetResult();
if (args.FirstOrDefault() is "check-config")
    return ConfigurationCheckCommand.Run(args[1..], Console.Out);
if (args.FirstOrDefault() is "evidence-copy-to-s3" or "evidence-copy-to-local")
    return EvidenceCopyCommand.RunAsync(args[1..], Console.Out, toLocal: args[0] == "evidence-copy-to-local").GetAwaiter().GetResult();
if (args.FirstOrDefault() is { } command && MessagingCommands.Names.Contains(command))
    return MessagingCommands.RunAsync(args, Console.Out).GetAwaiter().GetResult();

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment()) ProductionConfiguration.Validate(builder.Configuration);
builder.Services.AddVesselInfrastructure(builder.Configuration);
builder.Services.AddVesselIdentityDirectory(builder.Configuration);
builder.Services.AddVesselIdentityApi(builder.Configuration);
builder.Services.AddVesselPersistence(builder.Configuration);
builder.Services.AddVesselApplication();
builder.Services.AddPaperTrading();
// The API publishes events and module commands; it runs no module and never provisions at startup.
builder.AddVesselMessaging(MessagingHosting.ApiDeployment);
builder.Services.AddVesselMonitoring(builder.Configuration);
builder.Services.AddVesselNotifications(builder.Configuration);
// Read-only first-use warm-up; readiness waits for it (bounded by its timeout).
builder.Services.AddSingleton<StartupWarmup>();
builder.Services.AddHostedService(services => services.GetRequiredService<StartupWarmup>());
builder.Services.AddOpenApi();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ["application/json"];
});
builder.Services.AddSingleton<ResponseCompressionProvider>();
builder.Services.AddSingleton<IResponseCompressionProvider, MarketDataCompression>();

// Loopback is the default. Explicit proxy configuration replaces that default trust.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    ForwardedHeadersConfiguration.Configure(options, builder.Configuration));

var app = builder.Build();
app.UseForwardedHeaders();
// Every response is API data or an image; none should be framed, sniffed or leak the page as a referrer.
// Applied when the response starts, so error responses rebuilt after Response.Clear() carry them too.
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        // Owner data must not linger in browser or proxy caches; endpoints may set a stricter policy.
        if (context.Request.Path.StartsWithSegments("/api") && !headers.ContainsKey("Cache-Control"))
            headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    return next(context);
});
// Do not let provider/DB details or development exception pages escape the API.
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        var expected = ex is ProblemException or DomainValidationException or DomainRuleException or BadHttpRequestException;
        // Generate locally: neither a correlation header nor a raw URL is diagnostic input.
        var traceId = System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
        var (status, detail) = ex switch
        {
            ProblemException error => (error.StatusCode, error.Message),
            DomainValidationException error => (400, error.Message),
            DomainRuleException error => (409, error.Message),
            BadHttpRequestException { StatusCode: 413 } => (413, "The request body is too large."),
            BadHttpRequestException => (400, "The request body or parameters are invalid."),
            _ when SafeExceptionDiagnostics.IsDependencyOutage(ex) => (503, "The service is unavailable. Try again later."),
            _ => (500, "An unexpected error occurred.")
        };
        if (!expected)
            SafeExceptionDiagnostics.Log(app.Logger, context, ex, traceId, status);
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        if (context.IsCredentialEndpoint()) context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Correlation-ID"] = traceId;
        var extensions = new Dictionary<string, object?> { ["traceId"] = traceId };
        if (ex is ProblemException problem)
        {
            if (problem.Code is not null) extensions["code"] = problem.Code;
            if (problem.Code == "step-up-required" && problem.Data["action"] is "wallet.link" or "wallet.remove")
                extensions["action"] = problem.Data["action"];
            if (problem.RetryAfterSeconds is { } retryAfter)
                context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (context.Request.Path.StartsWithSegments("/api/auth") && !extensions.ContainsKey("code"))
            extensions["code"] = status switch { 413 => "request-too-large", 503 => "identity-unavailable", _ => "request-invalid" };
        await Results.Problem(statusCode: status, detail: detail,
            extensions: extensions).ExecuteAsync(context);
    }
});
app.UseResponseCompression();
app.UseAuthentication();
app.UseVesselIdentity();
app.UseAuthorization();
// Credential HTTPS, no-store and body limits come from endpoint metadata (CredentialEndpoint, RequestBodyLimit).
app.UseEndpointTransport();
app.UseTradingModeBoundary();

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
// Readiness for a proxy or orchestrator: the database answers and every migration is applied. No details.
app.MapGet("/health/ready", async (IServiceProvider services, CancellationToken ct) =>
{
    if (!services.GetRequiredService<StartupWarmup>().Completed)
        return Results.Json(new { status = "unavailable", reason = "warming-up" }, statusCode: 503);
    try
    {
        var db = services.GetRequiredService<VesselDbContext>();
        var ready = await db.Database.CanConnectAsync(ct) && !(await db.Database.GetPendingMigrationsAsync(ct)).Any();
        var identity = services.GetRequiredService<IdentityDbContext>();
        ready = ready && await identity.Database.CanConnectAsync(ct) && !(await identity.Database.GetPendingMigrationsAsync(ct)).Any();
        if (ready && services.GetRequiredService<IdentitySettings>().WalletMode &&
            !await (from user in identity.Users
                    join wallet in identity.Wallets on user.Id equals wallet.UserId
                    where user.Role == "operator" && user.Status == "active"
                    select user.Id).AnyAsync(ct))
            return Results.Json(new { status = "unavailable", reason = "no-operator-wallet" }, statusCode: 503);
        return ready ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "unavailable" }, statusCode: 503);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new { status = "unavailable" }, statusCode: 503);
    }
}).AllowAnonymous();
app.MapVesselIdentity();
app.MapSystem();
app.MapWorkspace();
app.MapEvidence();
app.MapPlays();
app.MapPaperEndpoints();
app.MapSizing();
app.MapRisk();
app.MapSettings();
app.MapExports();
app.MapReview();
app.MapVesselEvents();
app.MapMonitor();
app.MapNotifications();
if (app.Environment.IsDevelopment()) app.MapOpenApi().RequireAuthorization();

app.Run();
return 0;

public partial class Program
{
    public static async Task<int> VerifyIdentityAsync(Microsoft.Extensions.Configuration.IConfiguration configuration,
        IReadOnlyList<IdentityVerificationUser> users, TextWriter output, CancellationToken ct)
    {
        await using var services = new ServiceCollection().AddVesselPersistence(configuration).BuildServiceProvider();
        return await VerifyIdentityAsync(users, services.GetRequiredService<Vessel.Application.Ownership.IJournalOwnerInventory>(), output, ct);
    }

    public static async Task<int> VerifyIdentityAsync(IReadOnlyList<IdentityVerificationUser> users,
        Vessel.Application.Ownership.IJournalOwnerInventory inventory, TextWriter output, CancellationToken ct)
    {
        var identities = users.Select(x => x.Id).ToHashSet();
        var missingUsers = 0;
        Guid? after = null;
        while (true)
        {
            var page = await inventory.DistinctOwnersAsync(after, 1000, ct);
            foreach (var owner in page.Where(owner => !identities.Contains(owner)))
            {
                await output.WriteLineAsync($"Journal owner without identity user: {owner}");
                missingUsers++;
            }
            if (page.Count < 1000) break;
            after = page[^1];
        }
        foreach (var user in users.Where(x => !x.HasWallet))
            await output.WriteLineAsync($"Identity user without wallet: {user.Id}");
        var missingWallets = users.Count(x => !x.HasWallet);
        await output.WriteLineAsync($"Identity verification: {missingUsers} journal owners without users; {missingWallets} users without wallets.");
        return missingUsers == 0 && missingWallets == 0 ? 0 : 3;
    }
}
