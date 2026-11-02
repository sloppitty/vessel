using System.Net;
using System.Net.Http.Json;
using Vessel.Application.Plays;

namespace Vessel.Tests;

/// <summary>Phase 1, 1.3b (decision 5): several manual corrections with one reason, all or nothing.</summary>
public sealed partial class DiaryPostgresTests
{
    private static ManualCorrectionRow Corrected(ManualExecutionDto record, string quantity) => new(record.Id, record.Role, record.EntryId,
        record.OccurredAtUtc, record.Side, quantity, record.Price, record.Fee, record.FeeCurrency, record.Source);

    [PostgresFact]
    public async Task A_closed_one_by_one_ledger_becomes_two_by_two_in_one_batch_with_one_shared_history()
    {
        // Phase 1.3b acceptance (Verify.F5).
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var planned = await Planned(client, account.Id);
        var opened = await Add(client, planned, Fact(planned.Summary.Version, "entry", "1", "100", 0));
        var closed = await Add(client, opened.Play, Fact(opened.Play.Summary.Version, "exit", "1", "110", 1));
        Assert.Equal("closed", closed.Play.Summary.Status);
        var id = closed.Play.Summary.Id;
        var version = closed.Play.Summary.Version;
        var entry = closed.Records.Single(r => r.Role == "entry");
        var exit = closed.Records.Single(r => r.Role == "exit");
        var path = $"/api/plays/{id}/manual-executions/corrections";
        async Task<ManualExecutionsDto> Ledger() => (await client.GetFromJsonAsync<ManualExecutionsDto>($"/api/plays/{id}/manual-executions"))!;

        // One row at a time cannot get there: 2 in and 1 out would reopen the closed ledger.
        var single = await client.PutAsJsonAsync($"/api/plays/{id}/manual-executions/{entry.Id}",
            Fact(version, "entry", "2", "100", 0) with { Reason = "Two contracts, not one." });
        Assert.Equal(HttpStatusCode.Conflict, single.StatusCode);

        // An invalid final ledger changes nothing, however valid each row is.
        var overExit = await client.PostAsJsonAsync(path, new ManualCorrectionBatchRequest(Guid.NewGuid(), version, "Statement says two.",
            [Corrected(entry, "2"), Corrected(exit, "3")]));
        Assert.False(overExit.IsSuccessStatusCode);
        var unchanged = await Ledger();
        Assert.Equal((version, 2, "1", "1"), (unchanged.Play.Summary.Version, unchanged.History.Count, unchanged.Totals.EnteredQuantity,
            unchanged.Totals.ExitedQuantity));

        // Malformed batches are refused before anything is read as a correction.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new ManualCorrectionBatchRequest(Guid.NewGuid(), version,
            "Twice.", [Corrected(entry, "2"), Corrected(entry, "3")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new ManualCorrectionBatchRequest(Guid.NewGuid(), version,
            "Nothing.", []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new ManualCorrectionBatchRequest(Guid.NewGuid(), version,
            " ", [Corrected(entry, "2"), Corrected(exit, "2")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new ManualCorrectionBatchRequest(Guid.NewGuid(), version - 1,
            "Stale.", [Corrected(entry, "2"), Corrected(exit, "2")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new ManualCorrectionBatchRequest(Guid.NewGuid(), version,
            "Side.", [Corrected(entry, "2") with { Side = "sell" }, Corrected(exit, "2")]))).StatusCode);

        var request = new ManualCorrectionBatchRequest(Guid.NewGuid(), version, " The statement shows two contracts. ",
            [Corrected(entry, "2"), Corrected(exit, "2")]);
        var response = await client.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        var corrected = (await response.Content.ReadFromJsonAsync<ManualExecutionsDto>())!;
        Assert.Equal(("closed", "2", "2", "0"), (corrected.Play.Summary.Status, corrected.Totals.EnteredQuantity, corrected.Totals.ExitedQuantity,
            corrected.Totals.OpenQuantity));
        // Each row's before and after, under one batch ID and the one reason.
        var batch = corrected.History.Where(h => h.BatchId is not null).ToList();
        Assert.Equal(2, batch.Count);
        Assert.Single(batch.Select(h => h.BatchId).Distinct());
        Assert.All(batch, h => Assert.Equal(("The statement shows two contracts.", "1", "2"), (h.Reason, h.Before!.Quantity, h.After.Quantity)));
        Assert.Equal(new[] { entry.Id, exit.Id }.Order(), batch.Select(h => h.ExecutionId).Order());
        Assert.All(corrected.History.Where(h => h.BatchId is null), h => Assert.Null(h.Reason));

        // A retry returns the first result; the same request ID with another payload is refused.
        var retry = (await (await client.PostAsJsonAsync(path, request)).Content.ReadFromJsonAsync<ManualExecutionsDto>())!;
        Assert.Equal((corrected.Play.Summary.Version, corrected.History.Count), (retry.Play.Summary.Version, retry.History.Count));
        Assert.Equal(4, (await Ledger()).History.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, request with { Reason = "Another reason." })).StatusCode);

        // Single-row corrections keep working.
        var after = await Ledger();
        var price = await client.PutAsJsonAsync($"/api/plays/{id}/manual-executions/{exit.Id}",
            Fact(after.Play.Summary.Version, "exit", "2", "111", 1) with { Reason = "Exit price." });
        price.EnsureSuccessStatusCode();
        Assert.Null((await price.Content.ReadFromJsonAsync<ManualExecutionsDto>())!.History.Last().BatchId);
    }
}
