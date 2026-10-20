using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Evidence;
using Vessel.Domain.Evidence;
using Vessel.Infrastructure.Evidence;

namespace Vessel.Tests;

/// <summary>
/// Transient object-storage failures become <see cref="EvidenceStorageUnavailableException"/> (503 in the API);
/// configuration, permission, missing-object and create-only conflicts keep their original behaviour.
/// </summary>
public sealed class EvidenceStorageOutageTests
{
    private const string Bucket = "private-bucket-name";
    private const string Key = "owner/play/item.png";

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

    private static AmazonS3Exception S3(HttpStatusCode status, string code) =>
        new($"{code} for {Bucket}/{Key} at https://private-endpoint.example") { StatusCode = status, ErrorCode = code };

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

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", HttpStatusCode.ServiceUnavailable, "The service is unavailable. Try again later.")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", HttpStatusCode.ServiceUnavailable, "The service is unavailable. Try again later.")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", HttpStatusCode.InternalServerError, "An unexpected error occurred.")]
    public async Task Api_reports_storage_outages_as_503_without_provider_details(HttpStatusCode s3Status, string code,
        HttpStatusCode expected, string detail)
    {
        var owner = Guid.NewGuid();
        var metadata = new MemoryEvidenceStore(owner);
        var play = EvidenceFixtures.PlayFor(owner);
        metadata.Plays.Add(play);
        using var s3 = new FailingS3(() => S3(s3Status, code));
        await using var factory = new CoreApiFactory(owner, configure: services =>
        {
            services.AddSingleton<IEvidenceMetadataStore>(metadata);
            services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            services.AddSingleton<IEvidenceObjectStore>(new S3EvidenceObjectStore(s3, Bucket));
        });
        using var client = factory.AuthorizedClient();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(EvidenceFixtures.Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "chart.png");
        using var response = await client.PostAsync($"/api/plays/{play.Id}/evidence", form);
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(detail, body);
        foreach (var secret in new[] { Bucket, "private-endpoint", owner.ToString("N"), play.Id.ToString("N"), code, "Amazon", "bucket" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(metadata.Items);
    }

    /// <summary>
    /// Phase 0.6 (D-10): a Play's evidence metadata comes from the database alone, so the journal opens during an
    /// outage; only the affected original fails, as 503 when transient and 404 when the object is gone, and a failed
    /// read never removes the record.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", HttpStatusCode.NotFound)]
    public async Task Api_lists_metadata_during_an_outage_and_fails_only_the_original_read(HttpStatusCode s3Status, string code,
        HttpStatusCode expected)
    {
        var owner = Guid.NewGuid();
        var metadata = new MemoryEvidenceStore(owner);
        var play = EvidenceFixtures.PlayFor(owner);
        metadata.Plays.Add(play);
        var id = Guid.NewGuid();
        metadata.Items.Add(new PlayEvidence(id, play, $"{owner:N}/{play.Id:N}/{id:N}.png", "image/png", EvidenceFixtures.Png.Length,
            new string('a', 64), EvidenceSource.Upload, "Range high", DateTimeOffset.Parse("2026-10-19T10:00:00Z")));
        using var s3 = new FailingS3(() => S3(s3Status, code));
        await using var factory = new CoreApiFactory(owner, configure: services =>
        {
            services.AddSingleton<IEvidenceMetadataStore>(metadata);
            services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            services.AddSingleton<IEvidenceObjectStore>(new S3EvidenceObjectStore(s3, Bucket));
        });
        using var client = factory.AuthorizedClient();

        using var list = await client.GetAsync($"/api/plays/{play.Id}/evidence");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = await list.Content.ReadAsStringAsync();
        Assert.Contains(id.ToString(), listed);
        Assert.Contains("Range high", listed);
        Assert.Null(s3.Thrown);

        using var content = await client.GetAsync($"/api/evidence/{id}/content");
        Assert.Equal(expected, content.StatusCode);
        Assert.NotNull(s3.Thrown);
        var body = await content.Content.ReadAsStringAsync();
        foreach (var secret in new[] { Bucket, "private-endpoint", owner.ToString("N"), play.Id.ToString("N"), code, "Amazon", "bucket" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(id, Assert.Single(metadata.Items).Id);
    }

    private sealed class FailingS3(Func<Exception> failure)
        : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://localhost:9000", AuthenticationRegion = "us-east-1" })
    {
        public Exception? Thrown { get; private set; }

        private Task<T> Fail<T>()
        {
            Thrown = failure();
            return Task.FromException<T>(Thrown);
        }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken ct = default) => Fail<PutObjectResponse>();
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct = default) => Fail<GetObjectResponse>();
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken ct = default) => Fail<DeleteObjectResponse>();
    }
}
