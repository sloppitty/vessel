using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Vessel.Application.Evidence;
using Vessel.Infrastructure.Evidence;

namespace Vessel.Tests;

/// <summary>
/// Transient object-storage failures become <see cref="EvidenceStorageUnavailableException"/> (503 in the API);
/// configuration, permission, missing-object and create-only conflicts keep their original behaviour.
/// </summary>
public sealed class EvidenceStorageOutageTests : EvidenceStorageOutageTestsSupport
{

    private static readonly Dictionary<string, (Func<Exception> Create, bool Transient)> Failures = new()
    {
        ["s3-503-slowdown"] = (() => S3(HttpStatusCode.ServiceUnavailable, "SlowDown"), true),
        ["s3-500"] = (() => S3(HttpStatusCode.InternalServerError, "InternalError"), true),
        ["s3-502"] = (() => S3(HttpStatusCode.BadGateway, "BadGateway"), true),
        ["s3-429"] = (() => S3(HttpStatusCode.TooManyRequests, "TooManyRequests"), true),
        ["s3-400-request-timeout"] = (() => S3(HttpStatusCode.BadRequest, "RequestTimeout"), true),
        ["s3-403-access-denied"] = (() => S3(HttpStatusCode.Forbidden, "AccessDenied"), false),
        ["s3-404-no-such-bucket"] = (() => S3(HttpStatusCode.NotFound, "NoSuchBucket"), false),
        ["s3-400-invalid-argument"] = (() => S3(HttpStatusCode.BadRequest, "InvalidArgument"), false),
        ["s3-412-precondition"] = (() => S3(HttpStatusCode.PreconditionFailed, "PreconditionFailed"), false),
        ["s3-409-conditional-conflict"] = (() => S3(HttpStatusCode.Conflict, "ConditionalRequestConflict"), false),
        ["http-no-status"] = (() => new HttpRequestException("connect failed"), true),
        ["http-503"] = (() => new HttpRequestException("bad gateway", null, HttpStatusCode.ServiceUnavailable), true),
        ["http-401"] = (() => new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized), false),
        ["sdk-wrapped-transport"] = (() => new AmazonServiceException("wrapped", new HttpRequestException("reset")), true),
        ["sdk-wrapped-socket"] = (() => new AmazonClientException("wrapped", new SocketException((int)SocketError.ConnectionRefused)), true),
        ["socket"] = (() => new SocketException((int)SocketError.HostUnreachable), true),
        ["sdk-timeout"] = (() => new TaskCanceledException("HttpClient timeout"), true),
        ["timeout"] = (() => new TimeoutException(), true),
        ["transport-io"] = (() => new IOException("connection reset"), true),
        ["local-file-missing"] = (() => new FileNotFoundException("local"), false),
        ["sdk-credentials"] = (() => new AmazonClientException("no credentials"), false),
        ["argument"] = (() => new ArgumentException("bad"), false),
    };

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in Failures.Keys)
            foreach (var operation in new[] { "put", "get", "delete" })
                data.Add(name, operation);
        return data;
    }

    private static Task Run(S3EvidenceObjectStore store, string operation, CancellationToken ct = default) => operation switch
    {
        "put" => store.PutAsync(Key, new MemoryStream(EvidenceFixtures.Png), ct),
        "get" => store.OpenReadAsync(Key, ct),
        _ => store.DeleteAsync(Key, ct)
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Only_transient_failures_become_storage_unavailable(string failure, string operation)
    {
        var (create, transient) = Failures[failure];
        using var client = new FailingS3(create);
        var store = new S3EvidenceObjectStore(client, Bucket);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Run(store, operation));
        if (transient)
        {
            var outage = Assert.IsType<EvidenceStorageUnavailableException>(error);
            Assert.Same(client.Thrown, outage.InnerException);
            Assert.DoesNotContain(Bucket, outage.Message);
            Assert.DoesNotContain("private-endpoint", outage.Message);
            Assert.DoesNotContain(Key, outage.Message);
        }
        else Assert.Same(client.Thrown, error);
    }

    [Fact]
    public async Task Missing_key_is_still_null_and_not_an_outage()
    {
        using var client = new FailingS3(() => S3(HttpStatusCode.NotFound, "NoSuchKey"));
        Assert.Null(await new S3EvidenceObjectStore(client, Bucket).OpenReadAsync(Key, default));
    }

    [Theory]
    [InlineData("put")]
    [InlineData("get")]
    [InlineData("delete")]
    public async Task Caller_cancellation_is_not_reported_as_an_outage(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        foreach (var create in new Func<Exception>[]
        {
            () => new OperationCanceledException(cancellation.Token),
            () => new TaskCanceledException("cancelled"),
            () => new AmazonClientException("cancelled", new TaskCanceledException("cancelled"))
        })
        {
            using var client = new FailingS3(create);
            var store = new S3EvidenceObjectStore(client, Bucket);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Run(store, operation, cancellation.Token));
            Assert.Same(client.Thrown, error);
        }
    }
}
