using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Evidence;

namespace Vessel.Tests;

public sealed class EvidenceApiTests : EvidenceApiTestsSupport, IDisposable
{
    private readonly MemoryObjectStore objects = new();

    private CoreApiFactory Factory(Dictionary<string, string?>? settings = null, bool memoryObjects = true) => new(owner,
        settings: new Dictionary<string, string?> { ["Vessel:Evidence:Storage:Local:RootPath"] = root }
            .Concat(settings ?? []).ToDictionary(),
        configure: services =>
        {
            services.AddSingleton<IEvidenceMetadataStore>(store);
            services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            if (memoryObjects) services.AddSingleton<IEvidenceObjectStore>(objects);
        });

    private static MultipartFormDataContent Form(byte[] bytes, string? note = null, string? source = null, string type = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", "chart.png");
        if (note is not null) form.Add(new StringContent(note), "note");
        if (source is not null) form.Add(new StringContent(source), "source");
        return form;
    }

    [Theory]
    [InlineData("GET", "/api/plays/11111111-1111-1111-1111-111111111111/evidence")]
    [InlineData("POST", "/api/plays/11111111-1111-1111-1111-111111111111/evidence")]
    [InlineData("GET", "/api/evidence/11111111-1111-1111-1111-111111111111/content")]
    [InlineData("PATCH", "/api/evidence/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/evidence/11111111-1111-1111-1111-111111111111")]
    public async Task Routes_require_authentication(string method, string path)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new(new HttpMethod(method), path))).StatusCode);
    }

    [Fact]
    public async Task Upload_list_read_edit_and_delete_through_local_storage()
    {
        await using var factory = Factory(memoryObjects: false);
        using var client = factory.AuthorizedClient();
        var upload = await client.PostAsync($"/api/plays/{play.Id}/evidence", Form(EvidenceFixtures.Png, "Retest of the range high", "capture", type: "text/plain"));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var created = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var body = created.RootElement;
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/evidence/{id}", upload.Headers.Location?.OriginalString);
        Assert.Equal(new[] { "id", "playId", "source", "contentType", "sizeBytes", "sha256", "note", "createdAtUtc", "updatedAtUtc", "markup" },
            body.EnumerateObject().Select(p => p.Name));
        // The declared type is ignored; the signature decides.
        Assert.Equal("image/png", body.GetProperty("contentType").GetString());
        Assert.Equal("capture", body.GetProperty("source").GetString());
        Assert.True(File.Exists(Path.Combine(root, $"{owner:N}", $"{play.Id:N}", $"{id:N}.png")));

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/plays/{play.Id}/evidence");
        Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());

        var content = await client.GetAsync($"/api/evidence/{id}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal(EvidenceFixtures.Png, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(content.Headers.CacheControl?.NoStore);

        var patched = await client.PatchAsJsonAsync($"/api/evidence/{id}", new { note = "Edited" });
        Assert.Equal("Edited", (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("note").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/evidence/{id}", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/evidence/{id}")).StatusCode);
        Assert.False(File.Exists(Path.Combine(root, $"{owner:N}", $"{play.Id:N}", $"{id:N}.png")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/evidence/{id}/content")).StatusCode);
    }

    [Fact]
    public async Task Rejects_foreign_plays_wrong_forms_and_non_images()
    {
        var foreign = EvidenceFixtures.PlayFor(Guid.NewGuid());
        store.Plays.Add(foreign);
        await using var factory = Factory();
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/plays/{foreign.Id}/evidence", Form(EvidenceFixtures.Png))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plays/{foreign.Id}/evidence")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsJsonAsync($"/api/plays/{play.Id}/evidence", new { note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync($"/api/plays/{play.Id}/evidence", Form("<svg/>"u8.ToArray(), type: "image/svg+xml"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/plays/{play.Id}/evidence", new MultipartFormDataContent { { new StringContent("x"), "note" } })).StatusCode);
        var two = Form(EvidenceFixtures.Png);
        two.Add(new ByteArrayContent(EvidenceFixtures.Png), "file", "second.png");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/plays/{play.Id}/evidence", two)).StatusCode);
        var problem = await (await client.PostAsync($"/api/plays/{play.Id}/evidence", Form(EvidenceFixtures.Png, source: "screen"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Source must be capture or upload.", problem.GetProperty("detail").GetString());
        Assert.Empty(objects.Objects);
    }

    [Fact]
    public async Task Applies_configured_size_limit()
    {
        await using var factory = Factory(new() { ["Vessel:Evidence:MaxUploadBytes"] = "16" });
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/api/plays/{play.Id}/evidence", Form(EvidenceFixtures.Png))).StatusCode);
        var response = await client.PostAsync($"/api/plays/{play.Id}/evidence", Form([.. EvidenceFixtures.Png, .. new byte[8]]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Single(objects.Objects);
    }
}

public sealed class EvidenceMarkupApiTests : EvidenceMarkupTestsValues
{

    [Fact]
    public async Task Api_puts_and_deletes_marks()
    {
        var owner = Guid.NewGuid();
        var store = new MemoryEvidenceStore(owner);
        var play = EvidenceFixtures.PlayFor(owner);
        store.Plays.Add(play);
        var objects = new MemoryObjectStore();
        await using var factory = new CoreApiFactory(owner, configure: services =>
        {
            services.AddSingleton<IEvidenceMetadataStore>(store);
            services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            services.AddSingleton<IEvidenceObjectStore>(objects);
        });
        using var client = factory.AuthorizedClient();
        var form = new MultipartFormDataContent { { new ByteArrayContent(EvidenceFixtures.Png), "file", "a.png" },
            { new StringContent("""{"width":10,"height":10,"shapes":[{"id":"t","kind":"text","color":"#ffffff","at":{"x":1,"y":1},"size":4,"text":"Hi"}]}"""), "markup" } };
        var created = await (await client.PostAsync($"/api/plays/{play.Id}/evidence", form)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Hi", created.GetProperty("markup").GetProperty("shapes")[0].GetProperty("text").GetString());
        var id = created.GetProperty("id").GetGuid();

        var put = await client.PutAsJsonAsync($"/api/evidence/{id}/markup", new { width = 10, height = 10, shapes = new[] { new { id = "p", kind = "pen", color = "#4ade80", width = 1, points = new[] { new { x = 2, y = 3 } } } } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("pen", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("markup").GetProperty("shapes")[0].GetProperty("kind").GetString());
        var bad = await client.PutAsJsonAsync($"/api/evidence/{id}/markup", new { width = 10, height = 10, shapes = new[] { new { id = "p", kind = "pen", color = "#4ade80", width = 1, points = new[] { new { x = 20, y = 3 } } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("Mark points must lie inside the image.", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var cleared = await client.DeleteAsync($"/api/evidence/{id}/markup");
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("markup").ValueKind);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/evidence/{id}/markup")).StatusCode);
        Assert.Equal(EvidenceFixtures.Png, await client.GetByteArrayAsync($"/api/evidence/{id}/content"));
    }
}
