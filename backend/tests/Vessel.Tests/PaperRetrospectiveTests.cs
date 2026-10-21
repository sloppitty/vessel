using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class PaperRetrospectiveTests
{
    [PostgresFact]
    public async Task Paper_mode_cannot_preview_or_create_real_retrospectives_through_any_path_casing()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Actual");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Fills.AddRange(DiaryPostgresTests.Imported(account, "open", "o1", "buy", "open", 1, 100, 0),
                DiaryPostgresTests.Imported(account, "close", "o2", "sell", "close", 1, 110, 1, 10));
            seed.Orders.AddRange(DiaryPostgresTests.Order(account, "o1", "buy", 1), DiaryPostgresTests.Order(account, "o2", "sell", 1));
            await seed.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var operation = Assert.Single((await client.GetFromJsonAsync<ActivityPageDto>("/api/activity"))!.Operations);
        var query = $"?accountId={account.Id}&operationId={operation.Id}";
        var actualPreview = (await client.GetFromJsonAsync<RetrospectivePreviewDto>("/api/activity/retrospective" + query))!;
        Assert.True(actualPreview.Eligible);
        foreach (var (preview, create) in new[]
        {
            ("/api/activity/retrospective", "/api/plays/retrospective"),
            ("/API/AcTiViTy/ReTrOsPeCtIvE", "/API/PlAyS/ReTrOsPeCtIvE")
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(preview + query + "&tradingMode=paper")).StatusCode);
            var attempt = await client.PostAsJsonAsync(create + "?tradingMode=paper",
                new CreateRetrospectiveRequest(Guid.NewGuid(), account.Id, operation.Id, actualPreview.Fingerprint, "Paper must not create actual history"));
            Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
            await using var check = database.Context(owner);
            Assert.Empty(await check.Plays.ToListAsync());
            Assert.Empty(await check.OrderLinks.ToListAsync());
            Assert.Empty(await check.DiaryRequests.ToListAsync());
            Assert.Equal(2, await check.Fills.CountAsync());
        }
        // The actual-mode workflow and its atomic links are unchanged.
        var real = await client.PostAsJsonAsync("/api/plays/retrospective",
            new CreateRetrospectiveRequest(Guid.NewGuid(), account.Id, operation.Id, actualPreview.Fingerprint, "Actual history"));
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        await using var verify = database.Context(owner);
        Assert.Equal("real", (await verify.Plays.SingleAsync()).TradingMode);
        Assert.Equal(2, await verify.OrderLinks.CountAsync());
    }
}
