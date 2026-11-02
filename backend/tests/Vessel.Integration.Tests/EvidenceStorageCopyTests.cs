using System.Security.Cryptography;
using Vessel.Api;
using Vessel.Application.Evidence;
using Vessel.Infrastructure.Evidence;

namespace Vessel.Tests;

public sealed class EvidenceStorageCopyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vessel-evidence-copy-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryObjectStore destination = new();
    private const string Key = "owner/play/image.png";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public EvidenceStorageCopyTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dry_run_then_apply_verifies_original_bytes_and_rerun_skips_without_deleting_source(bool trailingSeparator)
    {
        var source = new LocalEvidenceObjectStore(root);
        await source.PutAsync(Key, new MemoryStream(EvidenceFixtures.Png), default);
        var copy = new EvidenceStorageCopy(trailingSeparator ? root + Path.DirectorySeparatorChar : root, destination);

        Assert.True(await copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), apply: false, default));
        Assert.Empty(destination.Objects);
        Assert.True(await copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), apply: true, default));
        Assert.Equal(EvidenceFixtures.Png, destination.Objects[Key]);
        Assert.False(await copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), apply: true, default));
        Assert.Equal(EvidenceFixtures.Png, await File.ReadAllBytesAsync(Path.Combine(root, Key)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Invalid_source_size_never_writes_to_s3(int size)
    {
        await new LocalEvidenceObjectStore(root).PutAsync(Key, new MemoryStream(EvidenceFixtures.Png), default);
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(root, destination).CopyAsync(Key, size, Hash(EvidenceFixtures.Png), true, default));
        Assert.Empty(destination.Objects);
        Assert.True(File.Exists(Path.Combine(root, Key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_hash_mismatch_stops_both_dry_run_and_apply(bool apply)
    {
        await new LocalEvidenceObjectStore(root).PutAsync(Key, new MemoryStream(EvidenceFixtures.Png), default);
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(root, destination).CopyAsync(Key, EvidenceFixtures.Png.Length, new string('0', 64), apply, default));
        Assert.Empty(destination.Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_destination_is_never_overwritten(bool apply)
    {
        await new LocalEvidenceObjectStore(root).PutAsync(Key, new MemoryStream(EvidenceFixtures.Png), default);
        var different = (byte[])EvidenceFixtures.Png.Clone();
        different[^1] ^= 1;
        destination.Objects.Add(Key, different);
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(root, destination).CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), apply, default));
        Assert.Equal(different, destination.Objects[Key]);
        Assert.Equal(EvidenceFixtures.Png, await File.ReadAllBytesAsync(Path.Combine(root, Key)));
    }

    [Fact]
    public async Task Readback_corruption_fails_without_deleting_the_source()
    {
        await new LocalEvidenceObjectStore(root).PutAsync(Key, new MemoryStream(EvidenceFixtures.Png), default);
        var corrupt = new CorruptingStore();
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(root, corrupt).CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), true, default));
        Assert.Equal(EvidenceFixtures.Png, await File.ReadAllBytesAsync(Path.Combine(root, Key)));
    }

    [Fact]
    public async Task Missing_source_and_cancellation_do_not_write_objects()
    {
        var copy = new EvidenceStorageCopy(root, destination);
        await Assert.ThrowsAnyAsync<IOException>(() => copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), true, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), true, cancellation.Token));
        Assert.Empty(destination.Objects);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("/outside.png")]
    [InlineData("owner/play/../image.png")]
    public async Task Unsafe_keys_are_rejected_before_accessing_the_source_or_target(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new EvidenceStorageCopy(root, destination).CopyAsync(key, 1, new string('0', 64), true, default));
        Assert.Empty(destination.Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Symlink_files_and_directories_are_not_followed(bool directory)
    {
        var original = Path.Combine(root, "original.png");
        await File.WriteAllBytesAsync(original, EvidenceFixtures.Png);
        Directory.CreateDirectory(Path.Combine(root, "owner", "play"));
        var key = directory ? "linked/image.png" : Key;
        if (directory)
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "owner", "play", "image.png"), EvidenceFixtures.Png);
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), Path.Combine(root, "owner", "play"));
        }
        else File.CreateSymbolicLink(Path.Combine(root, Key), original);
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(root, destination).CopyAsync(key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), true, default));
        Assert.Empty(destination.Objects);
        Assert.Equal(EvidenceFixtures.Png, await File.ReadAllBytesAsync(original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonseekable_remote_source_exports_to_new_local_root_and_repeated_copy_skips(bool trailingSeparator)
    {
        var remote = new NonseekableStore(EvidenceFixtures.Png);
        var target = Path.Combine(root, "new-export");
        var copy = new EvidenceStorageCopy(remote, trailingSeparator ? target + Path.DirectorySeparatorChar : target);
        Assert.True(await copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), false, default));
        Assert.False(Directory.Exists(target));
        Assert.True(await copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), true, default));
        Assert.Equal(EvidenceFixtures.Png, await File.ReadAllBytesAsync(Path.Combine(target, Key)));
        Assert.False(await copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), true, default));
        Assert.Single(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
        Assert.Equal(3, remote.Reads);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "hash")]
    [InlineData(true, "hash")]
    [InlineData(false, "size")]
    [InlineData(true, "size")]
    public async Task Invalid_remote_original_never_creates_final_local_files(bool apply, string failure)
    {
        var remote = new NonseekableStore(failure == "missing" ? null : EvidenceFixtures.Png);
        var target = Path.Combine(root, "new-export");
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(remote, target).CopyAsync(Key,
                failure == "size" ? EvidenceFixtures.Png.Length - 1 : EvidenceFixtures.Png.Length,
                failure == "hash" ? new string('0', 64) : Hash(EvidenceFixtures.Png), apply, default));
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_local_destination_is_never_overwritten(bool apply)
    {
        var original = new byte[] { 1, 2, 3 };
        await new LocalEvidenceObjectStore(root).PutAsync(Key, new MemoryStream(original), default);
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(new NonseekableStore(EvidenceFixtures.Png), root).CopyAsync(
                Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), apply, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(root, Key)));
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    [InlineData("ancestor", true)]
    [InlineData("dangling", true)]
    public async Task Local_destination_links_are_rejected_before_reading_s3(string kind, bool apply)
    {
        var remote = new NonseekableStore(EvidenceFixtures.Png);
        var target = Path.Combine(root, "export");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        if (kind is "ancestor" or "dangling")
            Directory.CreateSymbolicLink(target, kind == "dangling" ? Path.Combine(root, "missing") : outside);
        else
        {
            Directory.CreateDirectory(Path.Combine(target, "owner", "play"));
            if (kind == "directory")
            {
                Directory.Delete(Path.Combine(target, "owner", "play"));
                Directory.CreateSymbolicLink(Path.Combine(target, "owner", "play"), outside);
            }
            else File.CreateSymbolicLink(Path.Combine(target, Key), Path.Combine(outside, "missing.png"));
        }
        await Assert.ThrowsAsync<EvidenceCopyIntegrityException>(() =>
            new EvidenceStorageCopy(remote, target).CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), apply, default));
        Assert.Equal(0, remote.Reads);
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public async Task Local_export_validates_keys_and_cancellation_before_reading_s3()
    {
        var remote = new NonseekableStore(EvidenceFixtures.Png);
        var copy = new EvidenceStorageCopy(remote, Path.Combine(root, "new-export"));
        await Assert.ThrowsAsync<ArgumentException>(() => copy.CopyAsync("../outside", 1, new string('0', 64), true, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            copy.CopyAsync(Key, EvidenceFixtures.Png.Length, Hash(EvidenceFixtures.Png), true, cancellation.Token));
        Assert.Equal(0, remote.Reads);
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--source")]
    [InlineData("--source|relative")]
    [InlineData("--source|/tmp|--overwrite")]
    [InlineData("--apply")]
    public async Task Cli_rejects_unsafe_or_ambiguous_arguments_without_loading_configuration(string input)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await EvidenceCopyCommand.RunAsync(input.Split('|', StringSplitOptions.RemoveEmptyEntries), output));
        Assert.Contains("Usage:", output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("--destination")]
    [InlineData("--destination|relative")]
    [InlineData("--destination|/tmp|--overwrite")]
    [InlineData("--source|/tmp")]
    [InlineData("--apply")]
    public async Task Local_cli_requires_an_explicit_absolute_destination(string input)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await EvidenceCopyCommand.RunAsync(input.Split('|', StringSplitOptions.RemoveEmptyEntries), output, toLocal: true));
        Assert.Contains("evidence-copy-to-local --destination", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_help_does_not_require_a_database_or_storage_configuration(bool toLocal)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await EvidenceCopyCommand.RunAsync(["--help"], output, toLocal: toLocal));
        Assert.Contains("Dry run by default", output.ToString());
    }

    private sealed class NonseekableStore(byte[]? bytes) : IEvidenceObjectStore
    {
        public int Reads { get; private set; }
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<Stream?>(bytes is null ? null : new NonseekableStream(bytes));
        }
        public Task PutAsync(string key, Stream content, CancellationToken ct) =>
            throw new InvalidOperationException("An export must never write to its source.");
        public Task DeleteAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("Copying must never delete objects.");
    }

    private sealed class NonseekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class CorruptingStore : IEvidenceObjectStore
    {
        private readonly MemoryObjectStore inner = new();
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public Task PutAsync(string key, Stream content, CancellationToken ct) =>
            inner.PutAsync(key, new MemoryStream([0]), ct);
        public Task DeleteAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("Copying must never delete objects.");
    }
}
