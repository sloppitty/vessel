using System.Net;
using System.Net.Http.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vessel.Application.Evidence;
using Vessel.Infrastructure;
using Vessel.Infrastructure.Evidence;

namespace Vessel.Tests;

public sealed class S3EvidenceTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("legacy/evidence", false, false)]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    public async Task Default_configuration_keeps_local_storage(string? localRoot, bool s3Credentials, bool apiDefaults)
    {
        var root = Path.Combine(Path.GetTempPath(), "vessel-default-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = root });
        builder.Configuration.Sources.Clear();
        if (apiDefaults)
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var settings = new Dictionary<string, string?>();
        if (localRoot is not null)
            settings["Vessel:Evidence:Storage:Local:RootPath"] = localRoot;
        if (s3Credentials)
        {
            settings["Vessel:Evidence:Storage:S3:AccessKeyId"] = "synthetic-access";
            settings["Vessel:Evidence:Storage:S3:SecretAccessKey"] = "synthetic-secret";
        }
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var host = builder.Build();
        try
        {
            await host.StartAsync();
            var options = host.Services.GetRequiredService<IOptions<EvidenceSettings>>().Value;
            Assert.Equal("Local", options.Storage.Provider);
            Assert.Equal(localRoot ?? "data/evidence", options.Storage.Local.RootPath);
            var store = Assert.IsType<LocalEvidenceObjectStore>(host.Services.GetRequiredService<IEvidenceObjectStore>());
            const string key = "owner/play/item.png";
            using var content = new MemoryStream(EvidenceFixtures.Png);
            await store.PutAsync(key, content, default);
            Assert.Equal(EvidenceFixtures.Png, await File.ReadAllBytesAsync(Path.Combine(root, localRoot ?? "data/evidence", key)));
            await host.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Explicit_s3_without_credentials_fails_startup_instead_of_using_local()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Evidence:Storage:Provider"] = "S3"
        });
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("AccessKeyId and SecretAccessKey are required", error.Message);
        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IEvidenceObjectStore>());
    }

    [Fact]
    public async Task Explicit_s3_outage_propagates_without_falling_back_to_local()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Evidence:Storage:Provider"] = "S3",
            ["Vessel:Evidence:Storage:S3:AccessKeyId"] = "synthetic-access",
            ["Vessel:Evidence:Storage:S3:SecretAccessKey"] = "synthetic-secret"
        });
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var client = new StubS3 { Failure = new AmazonS3Exception("synthetic outage") { StatusCode = HttpStatusCode.ServiceUnavailable } };
        builder.Services.AddSingleton<IAmazonS3>(client);
        using var host = builder.Build();
        await host.StartAsync();
        var store = Assert.IsType<S3EvidenceObjectStore>(host.Services.GetRequiredService<IEvidenceObjectStore>());
        // A transient outage is reported as such (503 in the API), still never as a local fallback.
        var outage = await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() => store.OpenReadAsync("owner/play/item.png", default));
        Assert.Same(client.Failure, outage.InnerException);
        await host.StopAsync();
    }

    [Fact]
    public async Task Adapter_preserves_create_only_keys_cancellation_and_stream_ownership()
    {
        using var client = new StubS3();
        var store = new S3EvidenceObjectStore(client, "test-evidence");
        using var content = new MemoryStream(EvidenceFixtures.Png);
        using var cancellation = new CancellationTokenSource();
        await store.PutAsync("owner/play/item.png", content, cancellation.Token);
        Assert.Equal("test-evidence", client.Put!.BucketName);
        Assert.Equal("owner/play/item.png", client.Put.Key);
        Assert.Equal("*", client.Put.IfNoneMatch);
        Assert.False(client.Put.AutoCloseStream);
        Assert.False(client.Put.UseChunkEncoding);
        Assert.Same(content, client.Put.InputStream);
        Assert.Equal(cancellation.Token, client.Cancellation);

        var read = await store.OpenReadAsync("owner/play/item.png", cancellation.Token);
        Assert.Equal("test-evidence", client.Get!.BucketName);
        Assert.Equal("owner/play/item.png", client.Get.Key);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy);
        Assert.Equal(EvidenceFixtures.Png, copy.ToArray());
        await read.DisposeAsync();
        Assert.False(client.Content.CanRead);
        await store.DeleteAsync("owner/play/item.png", cancellation.Token);
        Assert.Equal("test-evidence", client.Delete!.BucketName);
        Assert.Equal("owner/play/item.png", client.Delete.Key);
        Assert.Equal(cancellation.Token, client.Cancellation);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", true)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", false)]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", false)]
    public async Task Only_a_missing_key_becomes_null(HttpStatusCode status, string code, bool missing)
    {
        using var client = new StubS3 { Failure = new AmazonS3Exception("synthetic") { StatusCode = status, ErrorCode = code } };
        var store = new S3EvidenceObjectStore(client, "test-evidence");
        if (missing) Assert.Null(await store.OpenReadAsync("owner/play/item.png", default));
        else if (status == HttpStatusCode.ServiceUnavailable)
            Assert.Same(client.Failure, (await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() => store.OpenReadAsync("owner/play/item.png", default))).InnerException);
        else Assert.Same(client.Failure, await Assert.ThrowsAsync<AmazonS3Exception>(() => store.OpenReadAsync("owner/play/item.png", default)));
    }

    [Theory]
    [InlineData("../item.png")]
    [InlineData("owner/play/item.png\n")]
    [InlineData("owner\\item.png")]
    [InlineData("owner/item.png?x=1")]
    public async Task Invalid_keys_never_reach_s3(string key)
    {
        using var client = new StubS3();
        var store = new S3EvidenceObjectStore(client, "test-evidence");
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(key, Stream.Null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(key, default));
        Assert.Null(client.Put);
        Assert.Null(client.Get);
        Assert.Null(client.Delete);
    }

    [Theory]
    [InlineData("Endpoint", "https://user:private-secret@example.com")]
    [InlineData("Endpoint", "https://example.com?secret=private-secret")]
    [InlineData("Endpoint", "https://example.com/path")]
    [InlineData("Endpoint", "file:///private-secret")]
    [InlineData("Bucket", "private-secret/invalid")]
    [InlineData("Bucket", "127.0.0.1")]
    [InlineData("Region", "")]
    [InlineData("AccessKeyId", "")]
    [InlineData("SecretAccessKey", "")]
    public async Task Invalid_s3_configuration_fails_startup_without_echoing_values(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Evidence:Storage:Provider"] = "S3",
            ["Vessel:Evidence:Storage:S3:AccessKeyId"] = "synthetic-access",
            ["Vessel:Evidence:Storage:S3:SecretAccessKey"] = "private-secret",
        });
        builder.Configuration[$"Vessel:Evidence:Storage:S3:{key}"] = value;
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(key, error.Message);
        Assert.DoesNotContain("private-secret", error.Message);
        Assert.DoesNotContain("synthetic-access", error.Message);
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("Local")]
    public async Task Provider_selection_is_explicit_and_validates_only_active_settings(string provider)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Evidence:Storage:Provider"] = provider,
            ["Vessel:Evidence:Storage:S3:AccessKeyId"] = provider == "S3" ? "synthetic-access" : "",
            ["Vessel:Evidence:Storage:S3:SecretAccessKey"] = provider == "S3" ? "synthetic-secret" : "",
            ["Vessel:Evidence:Storage:Local:RootPath"] = provider == "S3" ? "" : "data/test-evidence"
        });
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        var objects = host.Services.GetRequiredService<IEvidenceObjectStore>();
        if (provider == "S3")
        {
            Assert.IsType<S3EvidenceObjectStore>(objects);
            var client = host.Services.GetRequiredService<IAmazonS3>();
            Assert.Equal("http://localhost:9000/", client.Config.ServiceURL);
            Assert.Equal("us-east-1", client.Config.AuthenticationRegion);
            Assert.True(((AmazonS3Config)client.Config).ForcePathStyle);
        }
        else Assert.IsType<LocalEvidenceObjectStore>(objects);
        await host.StopAsync();
    }

    private sealed class StubS3() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://localhost:9000", AuthenticationRegion = "us-east-1" })
    {
        public PutObjectRequest? Put { get; private set; }
        public GetObjectRequest? Get { get; private set; }
        public DeleteObjectRequest? Delete { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public MemoryStream Content { get; } = new(EvidenceFixtures.Png);
        public Exception? Failure { get; init; }
        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken ct = default)
        {
            Put = request; Cancellation = ct;
            return Task.FromResult(new PutObjectResponse());
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct = default)
        {
            Get = request; Cancellation = ct;
            return Failure is not null ? Task.FromException<GetObjectResponse>(Failure) :
                Task.FromResult(new GetObjectResponse { ResponseStream = Content });
        }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken ct = default)
        {
            Delete = request; Cancellation = ct;
            return Task.FromResult(new DeleteObjectResponse());
        }
    }
}

public sealed class S3IntegrationTests
{
    [S3Fact]
    public async Task Rustfs_round_trip_create_only_races_private_reads_and_deletes()
    {
        // Only scripts/verify-evidence-storage.py supplies these, for its disposable RustFS container.
        var settings = new Dictionary<string, string?>
        {
            ["Vessel:Evidence:Storage:Provider"] = "S3",
            ["Vessel:Evidence:Storage:S3:Endpoint"] = Environment.GetEnvironmentVariable("VESSEL_TEST_S3_ENDPOINT"),
            ["Vessel:Evidence:Storage:S3:AccessKeyId"] = Environment.GetEnvironmentVariable("VESSEL_TEST_S3_ACCESS_KEY"),
            ["Vessel:Evidence:Storage:S3:SecretAccessKey"] = Environment.GetEnvironmentVariable("VESSEL_TEST_S3_SECRET_KEY"),
            ["Vessel:Evidence:Storage:S3:Bucket"] = "vessel-test-" + Guid.NewGuid().ToString("N")
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IAmazonS3>();
        var store = host.Services.GetRequiredService<IEvidenceObjectStore>();
        var bucket = settings["Vessel:Evidence:Storage:S3:Bucket"]!;
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            const string key = "owner/play/image.png";
            await store.PutAsync(key, new MemoryStream(EvidenceFixtures.Png), default);
            var duplicate = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                store.PutAsync(key, new MemoryStream(EvidenceFixtures.Jpeg), default));
            Assert.Equal(HttpStatusCode.PreconditionFailed, duplicate.StatusCode);
            await using (var read = await store.OpenReadAsync(key, default))
            {
                using var copy = new MemoryStream();
                await read!.CopyToAsync(copy);
                Assert.Equal(EvidenceFixtures.Png, copy.ToArray());
            }
            Assert.Null(await store.OpenReadAsync("owner/play/missing.png", default));
            var missingBucket = new S3EvidenceObjectStore(client, bucket + "-absent");
            Assert.Equal("NoSuchBucket", (await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                missingBucket.OpenReadAsync(key, default))).ErrorCode);

            var races = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { await store.PutAsync("owner/play/race.png", new MemoryStream(EvidenceFixtures.Png), default); return true; }
                catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict) { return false; }
            }));
            Assert.Single(races, won => won);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync("owner/play/cancelled.png", new MemoryStream(EvidenceFixtures.Png), cancellation.Token));
            Assert.Null(await store.OpenReadAsync("owner/play/cancelled.png", default));
            using var anonymousS3 = new HttpClient();
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymousS3.GetAsync($"{settings["Vessel:Evidence:Storage:S3:Endpoint"]}/{bucket}/{key}")).StatusCode);

            var owner = Guid.NewGuid();
            var metadata = new MemoryEvidenceStore(owner);
            var play = EvidenceFixtures.PlayFor(owner);
            metadata.Plays.Add(play);
            await using var factory = new CoreApiFactory(owner, settings: settings, configure: services =>
            {
                services.AddSingleton<IEvidenceMetadataStore>(metadata);
                services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            });
            using var api = factory.AuthorizedClient();
            using var form = new MultipartFormDataContent { { new ByteArrayContent(EvidenceFixtures.Png), "file", "image.png" } };
            var upload = await api.PostAsync($"/api/plays/{play.Id}/evidence", form);
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
            var evidence = (await upload.Content.ReadFromJsonAsync<EvidenceDto>())!;
            var content = await api.GetAsync($"/api/evidence/{evidence.Id}/content");
            Assert.Equal(EvidenceFixtures.Png, await content.Content.ReadAsByteArrayAsync());
            Assert.True(content.Headers.CacheControl!.NoStore);
            using var anonymousApi = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousApi.GetAsync($"/api/evidence/{evidence.Id}/content")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/evidence/{Guid.NewGuid()}/content")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/evidence/{evidence.Id}")).StatusCode);
            await store.DeleteAsync(key, default);
            await store.DeleteAsync(key, default);
            Assert.Null(await store.OpenReadAsync(key, default));
        }
        finally
        {
            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            foreach (var item in objects.S3Objects ?? [])
                await client.DeleteObjectAsync(bucket, item.Key);
            await client.DeleteBucketAsync(bucket);
            await host.StopAsync();
        }
    }
}
