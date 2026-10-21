using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.Common;
using Vessel.Application.Evidence;
using Vessel.Application.Ownership;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Common;

namespace Vessel.Application.Export;

/// <summary>
/// One Play as a zip (Phase 2, item 5c): <c>play.json</c> with the Play, its plan revisions and status history, its
/// executions (linked venue orders and fills with every retained link, the manual ledger and its corrections, or a paper
/// Play's simulation record through the paper readers), its review and history, its log and its evidence metadata with
/// markup and notes; then each evidence original under <c>evidence/</c>. It reuses the API's own read models, so nothing is
/// recomputed, and it holds no credential, storage key or internal path.
/// <para>
/// Every database read comes from one repeatable-read snapshot, so the parts agree (Sol round 1 on #40). The snapshot closes
/// before originals are read one at a time into an owner-only, delete-on-close temporary file; at most two bundles are built
/// at once and each original is cut off at its recorded size, so temporary storage stays bounded. An original that cannot
/// be opened is listed as unavailable rather than failing the bundle, and <c>play.json</c>, written last, says which.
/// </para>
/// </summary>
public sealed partial class PlayBundleService(PlayService plays, PlayExecutionService executions, DiaryService diary, EvidenceService evidence,
    IVenueRegistry venues, TimeProvider time, IUnitOfWork unitOfWork, PaperTradingService? paper = null)
{
    /// <summary>Bundles built at once in this process; later downloads wait their turn.</summary>
    private static readonly SemaphoreSlim Builds = new(2, 2);

    public const string Notice = "A copy of one Play as Vessel holds it: plan revisions, status history, executions, review, log and evidence. " +
        "Not a tax report. Amounts are exact decimal strings; null is unknown. Evidence originals are under evidence/, unchanged.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ExportFile> PrepareAsync(Guid playId, CancellationToken ct)
    {
        // Each read is owner-scoped and mode-checked by its service; a foreign or unknown Play is not found.
        var (play, history, log, images, isPaper, manual, executionFacts) = await unitOfWork.ReadSnapshotAsync(async () =>
        {
            var play = await plays.GetAsync(playId, ct);
            var summary = play.Summary;
            var manual = venues.Descriptor(summary.VenueId)?.Capabilities.ManualExecutions == true;
            var isPaper = summary.TradingMode == TradingMode.Paper;
            object? facts = isPaper ? paper is null ? null : await paper.ExportAsync(playId, ct)
                : manual ? await diary.ManualRecordAsync(playId, ct)
                : new { Execution = await executions.GetAsync(playId, ct), Links = await executions.LinkRecordAsync(playId, ct) };
            return (play, await plays.HistoryAsync(playId, ct), await plays.NotesAsync(playId, ct), await evidence.ListAsync(playId, ct),
                isPaper, manual, facts);
        }, ct);
        var summary = play.Summary;
        var exportedAt = time.GetUtcNow();
        var name = $"vessel-play-{Slug(summary.Title)}{summary.Id.ToString("N")[..8]}-{exportedAt.UtcDateTime:yyyyMMdd}.zip";
        return new(name, "application/zip", async (stream, token) =>
        {
            // The archive is built in a temporary file only this user can read, deleted when closed, then copied to the
            // response asynchronously: a zip written straight to a non-seekable response would need synchronous writes.
            await Builds.WaitAsync(token);
            try
            {
                await using var buffer = CreateBuffer(Path.GetTempPath());
                await WriteAsync(buffer, images, play, history, log, executionFacts, isPaper, manual, exportedAt, token);
                buffer.Position = 0;
                await buffer.CopyToAsync(stream, token);
            }
            finally
            {
                Builds.Release();
            }
        });
    }

    private async Task WriteAsync(Stream stream, IReadOnlyList<EvidenceDto> images, PlayDto play, PlayHistoryDto history,
        IReadOnlyList<PlayNoteDto> log, object? executionFacts, bool isPaper, bool manual, DateTimeOffset exportedAt, CancellationToken token)
    {
        await using var zip = await ZipArchive.CreateAsync(stream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, token);
        var originals = new List<object>();
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var file = $"evidence/{i + 1:D2}-{image.Id:N}{Extension(image.ContentType)}";
            EvidenceContent content;
            try { content = await evidence.ContentAsync(image.Id, token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Opening failed (missing object, storage outage): list it and go on. A failure while copying aborts
                // the download instead, so no entry is ever half written.
                originals.Add(new { image.Id, File = (string?)null, Included = false, Reason = "The original could not be read from storage." });
                continue;
            }
            await using (content.Content)
            {
                var entry = zip.CreateEntry(file, CompressionLevel.NoCompression);
                await using var target = await entry.OpenAsync(token);
                await CopyBoundedAsync(content.Content, target, image.SizeBytes, token);
            }
            originals.Add(new { image.Id, File = file, Included = true, Reason = (string?)null });
        }
        var document = new
        {
            Notice, ExportedAtUtc = exportedAt, Play = play, History = history, Executions = executionFacts,
            ExecutionSource = isPaper ? "paper" : manual ? "manual" : "venue", Log = log, Evidence = images, Originals = originals,
        };
        var json = zip.CreateEntry("play.json", CompressionLevel.Optimal);
        await using var writer = await json.OpenAsync(token);
        await JsonSerializer.SerializeAsync(writer, document, Json, token);
    }

    /// <summary>
    /// A new delete-on-close temporary archive only this user can read or write (Sol round 1 on #40: the default umask left
    /// it readable by other local users).
    /// </summary>
    public static FileStream CreateBuffer(string directory)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 81_920,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(directory, $"vessel-bundle-{Guid.NewGuid():N}.zip"), options);
    }

    /// <summary>Copies an original, failing the download if storage returns more than its recorded size.</summary>
    public static async Task CopyBoundedAsync(Stream source, Stream target, long recorded, CancellationToken token)
    {
        var buffer = new byte[81_920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            copied += read;
            if (copied > recorded) throw new InvalidOperationException("An evidence original is larger than its recorded size.");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static string Extension(string contentType) => contentType switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".bin",
    };

    /// <summary>A short file-name prefix from the title, letters and digits only; empty when nothing usable remains.</summary>
    private static string Slug(string title)
    {
        var slug = NonWord().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return slug.Length == 0 ? "" : slug + "-";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
