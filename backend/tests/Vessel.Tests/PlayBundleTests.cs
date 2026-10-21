using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 5c (G-07, I11): one Play as a zip with its JSON record and its evidence originals unchanged. The
/// irreplaceable plan, review and evidence get a portable second copy; nothing secret goes with them.
/// </summary>
public sealed class PlayBundleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vessel-bundle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private CoreApiFactory Factory(Guid owner, CoreDatabase database) => new(owner, connection: database.ConnectionString,
        settings: new Dictionary<string, string?> { ["Vessel:Evidence:Storage:Local:RootPath"] = root });

    /// <summary>A planned manual Play with one owner-entered entry and one chart capture.</summary>
    private static async Task<(Guid Play, Guid Evidence)> Seed(HttpClient client)
    {
        var account = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(null, "Broker", "manual", ManualAccountValueUsd: "1000"));
        account.EnsureSuccessStatusCode();
        var accountId = (await account.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await client.PostAsJsonAsync("/api/plays", new CreatePlayRequest(accountId, "BTC", "manual", "Range retest", TestPlays.Plan()));
        created.EnsureSuccessStatusCode();
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        var planned = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status", new ChangePlayStatusRequest(play.Summary.Version, "planned"));
        planned.EnsureSuccessStatusCode();
        var version = (await planned.Content.ReadFromJsonAsync<PlayDto>())!.Summary.Version;
        (await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/manual-executions", new ManualExecutionRequest(Guid.NewGuid(), version,
            "entry", "entry-1", DateTimeOffset.Parse("2026-10-01T10:00:00Z"), "buy", "0.5", "100.25", "0.05", "USDC", "Broker statement")))
            .EnsureSuccessStatusCode();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(EvidenceFixtures.Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "chart.png");
        form.Add(new StringContent("Retest of the range high"), "note");
        form.Add(new StringContent("capture"), "source");
        var upload = await client.PostAsync($"/api/plays/{play.Summary.Id}/evidence", form);
        upload.EnsureSuccessStatusCode();
        return (play.Summary.Id, (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static async Task<ZipArchive> Bundle(HttpClient client, Guid play)
    {
        var response = await client.GetAsync($"/api/plays/{play}/bundle");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("vessel-play-range-retest-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
    }

    private static JsonElement Json(ZipArchive zip)
    {
        using var stream = zip.GetEntry("play.json")!.Open();
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    [PostgresFact]
    public async Task A_bundle_holds_the_record_and_the_original_unchanged()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var factory = Factory(owner, database);
        using var client = factory.AuthorizedClient();
        var (playId, evidenceId) = await Seed(client);
        using var zip = await Bundle(client, playId);
        var image = $"evidence/01-{evidenceId:N}.png";
        Assert.Equal(["evidence/01-" + evidenceId.ToString("N") + ".png", "play.json"], zip.Entries.Select(e => e.FullName).Order());
        await using (var original = zip.GetEntry(image)!.Open())
        {
            using var copy = new MemoryStream();
            await original.CopyToAsync(copy);
            Assert.Equal(EvidenceFixtures.Png, copy.ToArray());
        }
        var json = Json(zip);
        Assert.Contains("Not a tax report.", json.GetProperty("notice").GetString());
        Assert.Equal("Range retest", json.GetProperty("play").GetProperty("summary").GetProperty("title").GetString());
        Assert.Equal(1, json.GetProperty("history").GetProperty("revisions").GetArrayLength());
        Assert.Equal("manual", json.GetProperty("executionSource").GetString());
        var record = Assert.Single(json.GetProperty("executions").GetProperty("records").EnumerateArray());
        Assert.Equal(("100.25", "0.05", "Broker statement"), (record.GetProperty("price").GetString(), record.GetProperty("fee").GetString(),
            record.GetProperty("source").GetString()));
        Assert.Equal("Retest of the range high", json.GetProperty("evidence")[0].GetProperty("note").GetString());
        var listed = json.GetProperty("originals")[0];
        Assert.True(listed.GetProperty("included").GetBoolean());
        Assert.Equal(image, listed.GetProperty("file").GetString());
        // Nothing internal: no storage key or local path.
        var text = json.GetRawText();
        Assert.DoesNotContain(root, text);
        Assert.DoesNotContain("storageKey", text, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task An_original_that_cannot_be_read_is_listed_not_hidden_and_others_cannot_see_the_play()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var factory = Factory(owner, database);
        using var client = factory.AuthorizedClient();
        var (playId, _) = await Seed(client);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
        using (var zip = await Bundle(client, playId))
        {
            Assert.Equal(["play.json"], zip.Entries.Select(e => e.FullName));
            var listed = Json(zip).GetProperty("originals")[0];
            Assert.False(listed.GetProperty("included").GetBoolean());
            Assert.Equal("The original could not be read from storage.", listed.GetProperty("reason").GetString());
        }
        await using var foreign = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString,
            settings: new Dictionary<string, string?> { ["Vessel:Evidence:Storage:Local:RootPath"] = root });
        using var other = foreign.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/plays/{playId}/bundle")).StatusCode);
        // A real Play asked for in paper mode is not found either.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plays/{playId}/bundle?tradingMode=paper")).StatusCode);
    }
}
