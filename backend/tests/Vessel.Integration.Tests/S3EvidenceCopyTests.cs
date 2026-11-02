using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Accounts;
using Vessel.Domain.Evidence;
using Vessel.Domain.Markets;

namespace Vessel.Tests;

public sealed class S3EvidenceCopyTests
{
    // Local to this test: the adapter-only S3 tests do not need PostgreSQL.
    private sealed class S3FactAttribute : FactAttribute
    {
        public S3FactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VESSEL_TEST_S3_ENDPOINT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES")))
                Skip = "Run scripts/verify-evidence-storage.py for isolated RustFS/PostgreSQL integration.";
        }
    }

    [S3Fact]
    public async Task Actual_cli_copies_database_originals_without_changing_sources_or_metadata()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "vessel-evidence-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new Dictionary<string, string?>
        {
            ["Vessel:Evidence:Storage:Provider"] = "S3",
            ["Vessel:Evidence:Storage:S3:Endpoint"] = Environment.GetEnvironmentVariable("VESSEL_TEST_S3_ENDPOINT"),
            ["Vessel:Evidence:Storage:S3:AccessKeyId"] = Environment.GetEnvironmentVariable("VESSEL_TEST_S3_ACCESS_KEY"),
            ["Vessel:Evidence:Storage:S3:SecretAccessKey"] = Environment.GetEnvironmentVariable("VESSEL_TEST_S3_SECRET_KEY"),
            ["Vessel:Evidence:Storage:S3:Bucket"] = "vessel-copy-" + Guid.NewGuid().ToString("N"),
            ["Vessel:Evidence:Storage:S3:Region"] = "us-east-1",
            ["Vessel:Evidence:Storage:S3:ForcePathStyle"] = "true"
        };
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(settings["Vessel:Evidence:Storage:S3:AccessKeyId"],
                settings["Vessel:Evidence:Storage:S3:SecretAccessKey"]),
            new AmazonS3Config { ServiceURL = settings["Vessel:Evidence:Storage:S3:Endpoint"],
                AuthenticationRegion = "us-east-1", ForcePathStyle = true });
        var bucket = settings["Vessel:Evidence:Storage:S3:Bucket"]!;
        var secondBucket = "vessel-copy-" + Guid.NewGuid().ToString("N");
        var secondBucketCreated = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = timeout.Token;
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct);
        try
        {
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic copy account");
            var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
            // These exact valid pictures were also decoded/downloaded in the recorded browser round-trip.
            byte[] Fixture(string extension) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Evidence", "original." + extension));
            var fixtures = new[]
            {
                (Extension: "png", Type: "image/png", Bytes: Fixture("png")),
                (Extension: "jpg", Type: "image/jpeg", Bytes: Fixture("jpg")),
                (Extension: "webp", Type: "image/webp", Bytes: Fixture("webp"))
            };
            var images = fixtures.Select((fixture, index) =>
            {
                var item = new PlayEvidence(Guid.NewGuid(), play,
                    $"{owner:N}/{play.Id:N}/{index}.{fixture.Extension}", fixture.Type, fixture.Bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes)), EvidenceSource.Upload,
                    $"Original note {index}", DateTimeOffset.Parse("2026-10-02T10:00:00Z"));
                item.UpdateMarkup("""{"width":10,"height":10,"shapes":[{"id":"box","kind":"box","color":"#ffffff","width":1,"from":{"x":1,"y":1},"to":{"x":9,"y":9}}]}""",
                    DateTimeOffset.Parse("2026-10-02T11:00:00Z"));
                return (Item: item, fixture.Bytes);
            }).ToArray();
            await using (var db = database.Context(owner))
            {
                db.Accounts.Add(account);
                db.Plays.Add(play);
                db.Evidence.AddRange(images.Select(image => image.Item));
                await db.SaveChangesAsync(ct);
            }
            foreach (var image in images)
            {
                var file = Path.Combine(root, image.Item.ObjectKey);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, image.Bytes, ct);
            }
            var before = await Metadata();

            var dryRun = await RunCli(database.ConnectionString, settings, root);
            Assert.Equal(0, dryRun.Code);
            Assert.Contains("Dry run complete: 3 referenced images", dryRun.Output);
            Assert.Contains("3 would be copied, 0 already verified", dryRun.Output);
            Assert.Empty((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, ct)).S3Objects ?? []);
            await AssertUnchanged();

            var applied = await RunCli(database.ConnectionString, settings, root, apply: true);
            Assert.Equal(0, applied.Code);
            Assert.Contains("3 copied and verified, 0 already verified", applied.Output);
            await using (var factory = new CoreApiFactory(owner, connection: database.ConnectionString, settings: settings))
            {
                using var api = factory.AuthorizedClient();
                using var anonymous = factory.CreateClient();
                foreach (var image in images)
                {
                    Assert.Equal(image.Bytes, await ReadObject(image.Item.ObjectKey));
                    using var response = await api.GetAsync($"/api/evidence/{image.Item.Id}/content", ct);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(image.Item.ContentType, response.Content.Headers.ContentType!.MediaType);
                    Assert.Equal(image.Bytes, await response.Content.ReadAsByteArrayAsync(ct));
                    Assert.Equal(HttpStatusCode.Unauthorized,
                        (await anonymous.GetAsync($"/api/evidence/{image.Item.Id}/content", ct)).StatusCode);
                }
            }
            await AssertUnchanged();
            var repeated = await RunCli(database.ConnectionString, settings, root, apply: true);
            Assert.Equal(0, repeated.Code);
            Assert.Contains("0 copied and verified, 3 already verified", repeated.Output);
            await AssertUnchanged();

            // Same length, different hash: a conflict must never overwrite the destination.
            var conflict = images[0];
            var different = (byte[])conflict.Bytes.Clone();
            different[^1] ^= 1;
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = conflict.Item.ObjectKey,
                InputStream = new MemoryStream(different) }, ct);
            Assert.Equal(1, (await RunCli(database.ConnectionString, settings, root, apply: true)).Code);
            Assert.Equal(different, await ReadObject(conflict.Item.ObjectKey));
            await AssertUnchanged();
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = conflict.Item.ObjectKey,
                InputStream = new MemoryStream(conflict.Bytes) }, ct);

            // Keep the removed original in memory and restore it even if the assertion fails.
            var missing = images[2];
            var missingFile = Path.Combine(root, missing.Item.ObjectKey);
            File.Delete(missingFile);
            try
            {
                Assert.Equal(1, (await RunCli(database.ConnectionString, settings, root, apply: true)).Code);
                Assert.False(File.Exists(missingFile));
                Assert.Equal(before, await Metadata());
                foreach (var image in images.Take(2))
                    Assert.Equal(image.Bytes, await File.ReadAllBytesAsync(Path.Combine(root, image.Item.ObjectKey), ct));
                foreach (var image in images)
                    Assert.Equal(image.Bytes, await ReadObject(image.Item.ObjectKey));
            }
            finally { await File.WriteAllBytesAsync(missingFile, missing.Bytes); }
            await AssertUnchanged();

            // Stage a provider switch through a new local root, without changing runtime settings.
            var exported = Path.Combine(root, "exported");
            var exportDryRun = await RunCli(database.ConnectionString, settings, exported,
                command: "evidence-copy-to-local");
            Assert.Equal(0, exportDryRun.Code);
            Assert.Contains("3 would be copied, 0 already verified", exportDryRun.Output);
            Assert.False(Directory.Exists(exported));
            await AssertSourcesUnchanged();

            var exportApplied = await RunCli(database.ConnectionString, settings, exported,
                apply: true, command: "evidence-copy-to-local");
            Assert.Equal(0, exportApplied.Code);
            Assert.Contains("3 copied and verified, 0 already verified", exportApplied.Output);
            foreach (var image in images)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(exported, image.Item.ObjectKey), ct);
                Assert.Equal(image.Bytes, bytes);
                Assert.Equal(image.Item.SizeBytes, bytes.LongLength);
                Assert.Equal(image.Item.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            var exportRepeated = await RunCli(database.ConnectionString, settings, exported,
                apply: true, command: "evidence-copy-to-local");
            Assert.Equal(0, exportRepeated.Code);
            Assert.Contains("0 copied and verified, 3 already verified", exportRepeated.Output);
            await AssertSourcesUnchanged();

            var localConflict = Path.Combine(exported, conflict.Item.ObjectKey);
            await File.WriteAllBytesAsync(localConflict, different, ct);
            try
            {
                foreach (var apply in new[] { false, true })
                {
                    Assert.Equal(1, (await RunCli(database.ConnectionString, settings, exported,
                        apply: apply, command: "evidence-copy-to-local")).Code);
                    Assert.Equal(different, await File.ReadAllBytesAsync(localConflict, ct));
                    await AssertSourcesUnchanged();
                }
            }
            finally { await File.WriteAllBytesAsync(localConflict, conflict.Bytes); }

            // Neither a missing remote original nor a same-size corrupt one may leave a final file.
            foreach (var corrupt in new[] { false, true })
            {
                if (corrupt)
                    await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = conflict.Item.ObjectKey,
                        InputStream = new MemoryStream(different) }, ct);
                else
                    await s3.DeleteObjectAsync(bucket, conflict.Item.ObjectKey, ct);
                try
                {
                    foreach (var apply in new[] { false, true })
                    {
                        var failedExport = Path.Combine(root, $"failed-export-{corrupt}-{apply}");
                        Assert.Equal(1, (await RunCli(database.ConnectionString, settings, failedExport,
                            apply: apply, command: "evidence-copy-to-local")).Code);
                        Assert.False(File.Exists(Path.Combine(failedExport, conflict.Item.ObjectKey)));
                        await AssertUnchanged();
                        if (corrupt)
                            Assert.Equal(different, await ReadObject(conflict.Item.ObjectKey));
                        else
                        {
                            var remaining = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, ct);
                            Assert.DoesNotContain(remaining.S3Objects ?? [], item => item.Key == conflict.Item.ObjectKey);
                        }
                        foreach (var image in images.Skip(1))
                            Assert.Equal(image.Bytes, await ReadObject(image.Item.ObjectKey));
                    }
                }
                finally
                {
                    await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = conflict.Item.ObjectKey,
                        InputStream = new MemoryStream(conflict.Bytes) }, CancellationToken.None);
                }
                await AssertSourcesUnchanged();
            }

            await s3.PutBucketAsync(new PutBucketRequest { BucketName = secondBucket }, ct);
            secondBucketCreated = true;
            var secondSettings = new Dictionary<string, string?>(settings)
            {
                ["Vessel:Evidence:Storage:S3:Bucket"] = secondBucket
            };
            var staged = await RunCli(database.ConnectionString, secondSettings, exported, apply: true);
            Assert.Equal(0, staged.Code);
            Assert.Contains("3 copied and verified, 0 already verified", staged.Output);
            await using (var factory = new CoreApiFactory(owner, connection: database.ConnectionString, settings: secondSettings))
            {
                using var api = factory.AuthorizedClient();
                foreach (var image in images)
                {
                    using var response = await api.GetAsync($"/api/evidence/{image.Item.Id}/content", ct);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(image.Item.ContentType, response.Content.Headers.ContentType!.MediaType);
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    Assert.Equal(image.Bytes, bytes);
                    Assert.Equal(image.Item.SizeBytes, bytes.LongLength);
                    Assert.Equal(image.Item.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                    Assert.Equal(image.Bytes, await File.ReadAllBytesAsync(Path.Combine(exported, image.Item.ObjectKey), ct));
                }
            }
            await AssertSourcesUnchanged();

            async Task<string> Metadata()
            {
                await using var db = database.Context(owner);
                return JsonSerializer.Serialize(new
                {
                    Accounts = await db.Accounts.AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct),
                    Plays = await db.Plays.AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct),
                    Evidence = await db.Evidence.AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct)
                });
            }
            async Task AssertUnchanged()
            {
                Assert.Equal(before, await Metadata());
                foreach (var image in images)
                    Assert.Equal(image.Bytes, await File.ReadAllBytesAsync(Path.Combine(root, image.Item.ObjectKey), ct));
            }
            async Task AssertSourcesUnchanged()
            {
                await AssertUnchanged();
                foreach (var image in images)
                    Assert.Equal(image.Bytes, await ReadObject(image.Item.ObjectKey));
            }
            async Task<byte[]> ReadObject(string key)
            {
                using var response = await s3.GetObjectAsync(bucket, key, ct);
                using var bytes = new MemoryStream();
                await response.ResponseStream.CopyToAsync(bytes, ct);
                return bytes.ToArray();
            }
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                try { if (secondBucketCreated) await DeleteBucket(secondBucket); }
                finally { await DeleteBucket(bucket); }
            }
            finally { Directory.Delete(root, recursive: true); }

            async Task DeleteBucket(string name)
            {
                var objects = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = name }, cleanupTimeout.Token);
                foreach (var item in objects.S3Objects ?? [])
                    await s3.DeleteObjectAsync(name, item.Key, cleanupTimeout.Token);
                await s3.DeleteBucketAsync(name, cleanupTimeout.Token);
            }
        }
    }

    private static async Task<(int Code, string Output)> RunCli(string connection,
        Dictionary<string, string?> settings, string path, bool apply = false, string command = "evidence-copy-to-s3")
    {
        var assembly = typeof(Program).Assembly.Location;
        if (!File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
        {
            // Project references may copy the DLL but not its executable runtime configuration.
            // Select only the matching build configuration/TFM, never a stale arbitrary build.
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var framework = output.Name;
            var configuration = output.Parent!.Name;
            DirectoryInfo? backend = output;
            while (backend is not null && !File.Exists(Path.Combine(backend.FullName, "Vessel.slnx")))
                backend = backend.Parent;
            Assert.NotNull(backend);
            assembly = Path.Combine(backend.FullName, "src", "Vessel.Api", "bin", configuration, framework, "Vessel.Api.dll");
        }
        Assert.True(File.Exists(assembly) && File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")),
            "Build the API executable before running the CLI integration test.");
        var workingDirectory = command == "evidence-copy-to-local" ? Path.GetDirectoryName(path)! : path;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Allowlist runtime plumbing instead of inheriting app settings, connection aliases,
        // AWS profiles, startup hooks or credentials. In particular, never read the repo .env.
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "DOTNET_ROOT", "LD_LIBRARY_PATH", "SystemRoot", "WINDIR", "TMPDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["HOME"] = workingDirectory;
        start.Environment["DOTNET_CLI_HOME"] = workingDirectory;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["AWS_EC2_METADATA_DISABLED"] = "true";
        start.Environment["ConnectionStrings__Vessel"] = connection;
        foreach (var (key, value) in settings)
            start.Environment[key.Replace(":", "__")] = value!;
        // The command must override this for itself, not edit active-provider configuration.
        start.Environment["Vessel__Evidence__Storage__Provider"] = "Local";
        start.Environment["Vessel__Evidence__Storage__Local__RootPath"] = path;
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(command == "evidence-copy-to-local" ? "--destination" : "--source");
        start.ArgumentList.Add(path);
        if (apply) start.ArgumentList.Add("--apply");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        // Drain but never print provider stderr or a credential-bearing subprocess environment.
        await stderr;
        return (process.ExitCode, await stdout);
    }
}
