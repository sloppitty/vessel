using System.Security.Cryptography;
using Vessel.Application.Common;
using Vessel.Application.Evidence;

namespace Vessel.Tests;

public sealed class EvidenceTests : EvidenceTestsSupport, IDisposable
{
    private readonly MemoryObjectStore objects = new();

    private EvidenceService Service(EvidenceLimits? limits = null) =>
        new(store, objects, limits ?? EvidenceLimits.Default, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None);

    private static async Task<ProblemException> Rejects(Func<Task> action) => await Assert.ThrowsAsync<ProblemException>(action);

    [Fact]
    public void Detects_formats_from_signatures_only()
    {
        Assert.Equal("image/png", EvidenceImage.Detect(EvidenceFixtures.Png)?.ContentType);
        Assert.Equal("image/jpeg", EvidenceImage.Detect(EvidenceFixtures.Jpeg)?.ContentType);
        Assert.Equal("image/webp", EvidenceImage.Detect(EvidenceFixtures.WebP)?.ContentType);
        Assert.Null(EvidenceImage.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(EvidenceImage.Detect("GIF89a"u8));
        Assert.Null(EvidenceImage.Detect([.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8]));
        Assert.Null(EvidenceImage.Detect([]));
    }

    [Fact]
    public async Task Upload_stores_bytes_then_metadata_with_hash_and_generated_key()
    {
        var result = await Service().UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "Breakout retest", "capture", null, default);
        var saved = Assert.Single(store.Items);
        Assert.Equal(("capture", "image/png", 12L, "Breakout retest"), (result.Source, result.ContentType, result.SizeBytes, result.Note));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(EvidenceFixtures.Png)), result.Sha256);
        Assert.Equal($"{owner:N}/{play.Id:N}/{result.Id:N}.png", saved.ObjectKey);
        Assert.Equal(EvidenceFixtures.Png, objects.Objects[saved.ObjectKey]);
        var upload = await Service().UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Jpeg), null, null, null, default);
        Assert.Equal(("upload", "image/jpeg", ""), (upload.Source, upload.ContentType, upload.Note));
    }

    [Fact]
    public async Task Upload_rejects_invalid_input_before_storing_anything()
    {
        var service = Service(new EvidenceLimits(16, 50));
        Assert.Equal(404, (await Rejects(() => service.UploadAsync(Guid.NewGuid(), new MemoryStream(EvidenceFixtures.Png), "", null, null, default))).StatusCode);
        var foreign = EvidenceFixtures.PlayFor(Guid.NewGuid());
        store.Plays.Add(foreign);
        Assert.Equal(404, (await Rejects(() => service.UploadAsync(foreign.Id, new MemoryStream(EvidenceFixtures.Png), "", null, null, default))).StatusCode);
        Assert.Equal(400, (await Rejects(() => service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", "screenshot", null, default))).StatusCode);
        Assert.Equal(400, (await Rejects(() => service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), new string('x', 4001), null, null, default))).StatusCode);
        Assert.Equal(400, (await Rejects(() => service.UploadAsync(play.Id, new MemoryStream(), "", null, null, default))).StatusCode);
        Assert.Equal(413, (await Rejects(() => service.UploadAsync(play.Id, new MemoryStream(new byte[17]), "", null, null, default))).StatusCode);
        Assert.Equal(415, (await Rejects(() => service.UploadAsync(play.Id, new MemoryStream("not an image"u8.ToArray()), "", null, null, default))).StatusCode);
        Assert.Empty(store.Items);
        Assert.Empty(objects.Objects);
    }

    [Fact]
    public async Task Upload_enforces_the_per_play_limit()
    {
        var service = Service(new EvidenceLimits(1024, 2));
        await service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", null, null, default);
        await service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", null, null, default);
        Assert.Equal(409, (await Rejects(() => service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", null, null, default))).StatusCode);
        Assert.Equal(2, objects.Objects.Count);
    }

    [Fact]
    public async Task Failed_metadata_write_removes_the_stored_object()
    {
        store.AddFailure = new InvalidOperationException("database down");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", null, null, default));
        Assert.Empty(objects.Objects);
    }

    [Fact]
    public async Task Notes_update_content_streams_and_delete_removes_record_then_object()
    {
        var service = Service();
        var created = await service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.WebP), "first", null, null, default);
        var updated = await service.UpdateNoteAsync(created.Id, new("Second thoughts"), default);
        Assert.Equal("Second thoughts", updated.Note);
        Assert.True(updated.UpdatedAtUtc >= created.UpdatedAtUtc);
        Assert.Equal(1, store.Saves);
        Assert.Equal(400, (await Rejects(() => service.UpdateNoteAsync(created.Id, new(new string('x', 4001)), default))).StatusCode);

        var content = await service.ContentAsync(created.Id, default);
        using (var copy = new MemoryStream()) { await content.Content.CopyToAsync(copy); Assert.Equal(EvidenceFixtures.WebP, copy.ToArray()); }
        Assert.Equal("image/webp", content.ContentType);
        Assert.Equal("Second thoughts", Assert.Single(await service.ListAsync(play.Id, default)).Note);

        objects.FailDeletes = true;
        await service.DeleteAsync(created.Id, default);
        Assert.Empty(store.Items);
        Assert.Single(objects.Objects); // Left unreferenced; the record is already gone.
        Assert.Equal(404, (await Rejects(() => service.ContentAsync(created.Id, default))).StatusCode);
        Assert.Equal(404, (await Rejects(() => service.DeleteAsync(created.Id, default))).StatusCode);
    }

    [Fact]
    public async Task Missing_object_reads_as_not_found()
    {
        var created = await Service().UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", null, null, default);
        objects.Objects.Clear();
        Assert.Equal(404, (await Rejects(() => Service().ContentAsync(created.Id, default))).StatusCode);
    }
}

public sealed class EvidenceMarkupUseCasesTests : EvidenceMarkupTestsValues
{

    [Fact]
    public async Task Service_replaces_and_clears_marks_without_touching_the_image()
    {
        var owner = Guid.NewGuid();
        var store = new MemoryEvidenceStore(owner);
        var objects = new MemoryObjectStore();
        var play = EvidenceFixtures.PlayFor(owner);
        store.Plays.Add(play);
        var service = new EvidenceService(store, objects, EvidenceLimits.Default, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None);
        var created = await service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", "capture",
            """{"width":800,"height":600,"shapes":[{"id":"a","kind":"box","color":"#ff5c5c","width":3,"from":{"x":1,"y":1},"to":{"x":9,"y":9},"text":"x"}]}""", default);
        Assert.Equal("box", Assert.Single(created.Markup!.Shapes).Kind);
        Assert.Null(created.Markup.Shapes[0].Text);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", null, """{"width":1}""", default))).StatusCode);

        var updated = await service.UpdateMarkupAsync(created.Id, With(Pen()), default);
        Assert.Equal("pen", Assert.Single(updated.Markup!.Shapes).Kind);
        Assert.Null((await service.UpdateMarkupAsync(created.Id, null, default)).Markup);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateMarkupAsync(created.Id, With(Pen() with { Color = "red" }), default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateMarkupAsync(Guid.NewGuid(), null, default))).StatusCode);
        Assert.Equal(EvidenceFixtures.Png, Assert.Single(objects.Objects).Value);
    }
}
