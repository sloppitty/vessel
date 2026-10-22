using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Export;

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

    [Fact]
    public void A_temporary_archive_is_readable_by_its_owner_only()
    {
        // Sol round 1 on #40: with a 022 umask the archive was created 0644, readable by other local users.
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        using var buffer = PlayBundleService.CreateBuffer(root);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(buffer.Name));
    }

    [Fact]
    public async Task An_original_larger_than_its_recorded_size_fails_the_copy()
    {
        using var target = new MemoryStream();
        await PlayBundleService.CopyBoundedAsync(new MemoryStream(new byte[10]), target, 10, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PlayBundleService.CopyBoundedAsync(new MemoryStream(new byte[11]), new MemoryStream(), 10, default));
    }

    [PostgresFact]
    public async Task A_disabled_manual_accounts_play_still_exports_its_ledger()
    {
        // Sol round 1 on #40: the ledger reader refused disabled accounts, so the bundle failed while the Play stayed readable.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var factory = Factory(owner, database);
        using var client = factory.AuthorizedClient();
        var (playId, _) = await Seed(client);
        await using (var db = database.Context(owner))
        {
            var account = await db.Accounts.SingleAsync();
            account.UpdateSettings(account.Name, account.PortfolioId, false);
            await db.SaveChangesAsync();
        }
        using var zip = await Bundle(client, playId);
        Assert.Single(Json(zip).GetProperty("executions").GetProperty("records").EnumerateArray());
    }

    [PostgresFact]
    public async Task A_removed_link_stays_in_the_record_with_its_times()
    {
        // Sol round 1 on #40: the execution read hides dismissed and rejected links, so the bundle lost them.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        Guid playId;
        await using (var db = database.Context(owner))
        {
            var account = new Vessel.Domain.Accounts.Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
            account.Configure(null, "0x" + new string('b', 40), null);
            var play = new Vessel.Domain.Plays.Play(Guid.NewGuid(), account, new Vessel.Domain.Markets.PerpetualInstrument("hyperliquid", "BTC"),
                Vessel.Domain.Plays.InstrumentSource.Venue, PlayDocuments.Serialize(TestPlays.Plan()), at);
            var (change, revision) = play.MarkPlanned(at);
            var order = new Vessel.Domain.Workspace.ImportedOrder
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ExternalOrderId = "order-1", ContractId = "BTC", Side = "buy",
                OrderType = "Limit", LimitPrice = 100, OriginalSize = 1, RemainingSize = 1, PlacedAtUtc = at, Status = "open",
                VenueStatus = "open", StatusAtUtc = at, ObservedAtUtc = at,
            };
            db.AddRange(account, play, change, revision, order, new Vessel.Domain.Plays.PlayOrderLink(play, order, Vessel.Domain.Plays.OrderLinkRole.Entry,
                "entry-1", null, Vessel.Domain.Plays.OrderLinkState.Dismissed, Vessel.Domain.Plays.OrderLinkSource.Owner, at));
            await db.SaveChangesAsync();
            playId = play.Id;
        }
        await using var factory = Factory(owner, database);
        using var client = factory.AuthorizedClient();
        using var zip = await BundleOf(client, playId);
        var link = Assert.Single(Json(zip).GetProperty("executions").GetProperty("links").EnumerateArray());
        Assert.Equal(("dismissed", "order-1", "entry"), (link.GetProperty("state").GetString(), link.GetProperty("orderId").GetString(),
            link.GetProperty("role").GetString()));
        Assert.Equal(at, link.GetProperty("createdAtUtc").GetDateTimeOffset());
    }

    [PostgresFact]
    public async Task A_paper_bundle_states_each_executions_model_and_plan_version_and_the_activations()
    {
        // Sol round 1 on #40: the paper state left out which model and plan activation produced an execution.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        Guid playId;
        await using (var db = database.Context(owner))
        {
            var portfolio = new Vessel.Domain.Workspace.Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
            var account = new Vessel.Domain.Accounts.Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", "paper", 4000);
            account.Configure(portfolio.Id, null, null);
            var play = new Vessel.Domain.Plays.Play(Guid.NewGuid(), account, new Vessel.Domain.Markets.PerpetualInstrument("hyperliquid", "BTC"),
                Vessel.Domain.Plays.InstrumentSource.Venue, PlayDocuments.Serialize(TestPlays.Plan()), at);
            var (change, revision) = play.MarkPlanned(at);
            db.AddRange(portfolio, account, play, change, revision, Vessel.Domain.Paper.PaperLedgerEntry.Seed(owner, account.Id, 4000, at),
                new Vessel.Domain.Paper.PaperPlanActivation { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, PlayId = play.Id,
                    PlanVersion = 1, PlanJson = play.Plan, EffectiveAtUtc = at },
                new Vessel.Domain.Paper.PaperExecution { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, PlayId = play.Id,
                    EntryId = "entry-1", Role = "entry", Price = 100, Quantity = 1, MarginUsd = 100, PlanVersion = 1, CandleOpenAtUtc = at,
                    CandleCloseAtUtc = at.AddMinutes(1), OccurredAtUtc = at.AddMinutes(1) });
            await db.SaveChangesAsync();
            playId = play.Id;
        }
        await using var factory = Factory(owner, database);
        using var client = factory.AuthorizedClient();
        using var zip = await BundleOf(client, playId, "?tradingMode=paper");
        var json = Json(zip);
        Assert.Equal("paper", json.GetProperty("executionSource").GetString());
        var facts = json.GetProperty("executions");
        var execution = Assert.Single(facts.GetProperty("executions").EnumerateArray());
        Assert.Equal((1, 1, "100"), (execution.GetProperty("modelVersion").GetInt32(), execution.GetProperty("planVersion").GetInt32(),
            execution.GetProperty("marginUsd").GetString()));
        var activation = Assert.Single(facts.GetProperty("activations").EnumerateArray());
        Assert.Equal(1, activation.GetProperty("planVersion").GetInt32());
        Assert.Contains("Never actual fills", facts.GetProperty("notice").GetString());
    }

    private static async Task<ZipArchive> BundleOf(HttpClient client, Guid play, string query = "")
    {
        var response = await client.GetAsync($"/api/plays/{play}/bundle{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
    }
}
