using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Credentials;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;

namespace Vessel.Tests.Isolation;

/// <summary>Portfolios, accounts, bulk membership, credentials and the account sub-resources across two owners.</summary>
public abstract class WorkspaceIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/portfolios")]
    [Covers("GET", "/api/portfolios/{id:guid}")]
    [Covers("POST", "/api/portfolios")]
    [Covers("PATCH", "/api/portfolios/{id:guid}")]
    [Covers("DELETE", "/api/portfolios/{id:guid}")]
    public async Task Portfolios_of_another_owner_answer_like_unknown_ones_and_stay_unchanged()
    {
        var (a, b) = await api.Owners();
        var portfolio = await a.Portfolio(Isolated.Marker("portfolio-a-initial"));
        var account = (await a.ManualAccount(Isolated.Marker("account-a"), portfolio)).GetProperty("id").GetGuid();
        // Positive control: A renames and re-icons its own portfolio through the route B attempts below, so a handler
        // that refused every request would fail here rather than pass as isolation.
        var name = Isolated.Marker("portfolio-a");
        var patched = await a.Send(HttpMethod.Patch, $"/api/portfolios/{portfolio}", new UpdatePortfolioRequest(name, "rocket"));
        Assert.Equal((name, "rocket"), (patched.GetProperty("name").GetString(), patched.GetProperty("icon").GetString()));
        var read = await a.GetJson($"/api/portfolios/{portfolio}");
        Assert.Equal((name, "rocket"), (read.GetProperty("name").GetString(), read.GetProperty("icon").GetString()));
        var state = await Snapshot.Of(a, "/api/portfolios", $"/api/portfolios/{portfolio}", $"/api/accounts/{account}");
        await b.Portfolio(Isolated.Marker("portfolio-b"));

        await state.After(() => Isolated.NotFound(b, portfolio, Isolated.Request(HttpMethod.Get, id => $"/api/portfolios/{id}")));
        await state.After(() => Isolated.NotFound(b, portfolio, Isolated.Request(HttpMethod.Patch, id => $"/api/portfolios/{id}",
            _ => new UpdatePortfolioRequest("renamed-by-b", "folder"))));
        await state.After(() => Isolated.NotFound(b, portfolio, Isolated.Request(HttpMethod.Delete, id => $"/api/portfolios/{id}")));

        var list = await b.GetText("/api/portfolios");
        Isolated.NoLeak(list, portfolio, name, account);
        Assert.Single((await b.GetJson("/api/portfolios")).EnumerateArray());
        // A's own delete still works afterwards: B's attempts did not lock or alter it.
        await a.Send(HttpMethod.Delete, $"/api/portfolios/{portfolio}", expected: HttpStatusCode.NoContent);
        Assert.Equal(JsonValueKind.Null, (await a.GetJson($"/api/accounts/{account}")).GetProperty("portfolioId").ValueKind);
    }

    [PostgresFact]
    [Covers("GET", "/api/accounts")]
    [Covers("GET", "/api/accounts/{id:guid}")]
    [Covers("POST", "/api/accounts")]
    [Covers("PUT", "/api/accounts/{id:guid}")]
    [Covers("DELETE", "/api/accounts/{id:guid}")]
    public async Task Accounts_of_another_owner_answer_like_unknown_ones_and_stay_unchanged()
    {
        var (a, b) = await api.Owners();
        var portfolio = await a.Portfolio(Isolated.Marker("portfolio-a"));
        var created = await a.ManualAccount(Isolated.Marker("account-a-initial"), portfolio);
        var account = created.GetProperty("id").GetGuid();
        // Positive controls: A renames, disables and unlinks its own account through the route B attempts below, then
        // re-enables and relinks it, so a handler that refused every request would fail here rather than pass.
        var name = Isolated.Marker("account-a");
        var disabled = await a.Send(HttpMethod.Put, $"/api/accounts/{account}",
            new UpdateAccountRequest(name, null, false, created.GetProperty("settingsRevision").GetInt64()));
        Assert.Equal((name, JsonValueKind.Null, false), (disabled.GetProperty("name").GetString(), disabled.GetProperty("portfolioId").ValueKind,
            disabled.GetProperty("isEnabled").GetBoolean()));
        var enabled = await a.Send(HttpMethod.Put, $"/api/accounts/{account}",
            new UpdateAccountRequest(name, portfolio, true, disabled.GetProperty("settingsRevision").GetInt64()));
        var revision = enabled.GetProperty("settingsRevision").GetInt64();
        Assert.True(revision > disabled.GetProperty("settingsRevision").GetInt64());
        var read = await a.GetJson($"/api/accounts/{account}");
        Assert.Equal((name, portfolio, true, revision), (read.GetProperty("name").GetString(), read.GetProperty("portfolioId").GetGuid(),
            read.GetProperty("isEnabled").GetBoolean(), read.GetProperty("settingsRevision").GetInt64()));

        // Both owners retain imported facts of the same native source, with identical source fill, order and funding IDs.
        var venue = (await a.SyncedVenueAccount(Isolated.Marker("venue-a"), portfolio)).GetProperty("id").GetGuid();
        var ownVenue = (await b.SyncedVenueAccount(Isolated.Marker("venue-b"))).GetProperty("id").GetGuid();
        string[] facts = ["imported_fills", "imported_orders", "funding_payments", "account_funding_states"];
        foreach (var owner in new[] { a, b })
        {
            var rows = await Persisted.Rows(owner.Journal!, owner.Id);
            Assert.All(facts, table => Assert.True(rows[table].Rows > 0, $"{table} is empty for {owner.Id}."));
        }
        var paths = new[] { "/api/accounts", $"/api/accounts/{account}", $"/api/accounts/{venue}", $"/api/accounts/{venue}/fills",
            "/api/portfolios", "/api/overview" };
        var state = await Snapshot.Of(a, paths);
        // Positive control for delete: B deletes its own synced account and its retained facts go with it; A's account
        // on the same source, its fills, orders and funding stay byte-identical.
        await state.After(() => b.Send(HttpMethod.Delete, $"/api/accounts/{ownVenue}", expected: HttpStatusCode.NoContent));
        await b.Send(HttpMethod.Get, $"/api/accounts/{ownVenue}", expected: HttpStatusCode.NotFound);
        var deleted = await Persisted.Rows(b.Journal!, b.Id);
        Assert.All(facts.Append("accounts"), table => Assert.Equal(0, deleted[table].Rows));

        // Refusal baselines follow the positive controls: A's state above, with an account B could validly update.
        var own = await b.ManualAccount(Isolated.Marker("account-b"));
        var ownId = own.GetProperty("id").GetGuid();
        var ownRevision = own.GetProperty("settingsRevision").GetInt64();
        await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Get, id => $"/api/accounts/{id}")));
        // Disable, rename and move through the route: the request is otherwise valid for an owned account.
        await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Put, id => $"/api/accounts/{id}",
            _ => new UpdateAccountRequest("taken-by-b", null, false, revision))));
        foreach (var target in new[] { account, venue })
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Delete, id => $"/api/accounts/{id}")));

        // A foreign portfolio in a body answers like an unknown portfolio, and creates or moves nothing.
        var both = await Snapshot.Of((a, paths), (b, ["/api/accounts", "/api/portfolios"]));
        await both.After(() => Isolated.SameAsUnknown(b, portfolio, Isolated.Request(HttpMethod.Post, _ => "/api/accounts",
            id => new CreateAccountRequest(id, "into-foreign-portfolio", "manual", ManualAccountValueUsd: "1"))));
        await both.After(() => Isolated.SameAsUnknown(b, portfolio, Isolated.Request(HttpMethod.Put, _ => $"/api/accounts/{ownId}",
            id => new UpdateAccountRequest("moved-into-foreign", id, true, ownRevision))));

        var list = await b.GetText("/api/accounts");
        Isolated.NoLeak(list, account, name, portfolio, venue);
        Assert.Single((await b.GetJson("/api/accounts")).EnumerateArray());
        Isolated.NoLeak(await b.GetText("/api/overview"), account, name, portfolio, venue);
    }

    [PostgresFact]
    [Covers("POST", "/api/accounts/portfolio")]
    public async Task Bulk_membership_with_foreign_ids_changes_nothing_for_either_owner()
    {
        var (a, b) = await api.Owners();
        var aPortfolio = await a.Portfolio(Isolated.Marker("portfolio-a"));
        var aAccount = await a.ManualAccount(Isolated.Marker("account-a"), aPortfolio);
        var aId = aAccount.GetProperty("id").GetGuid();
        var aRevision = aAccount.GetProperty("settingsRevision").GetInt64();
        var bPortfolio = await b.Portfolio(Isolated.Marker("portfolio-b"));
        var bAccount = await b.ManualAccount(Isolated.Marker("account-b"));
        var bId = bAccount.GetProperty("id").GetGuid();
        var bRevision = bAccount.GetProperty("settingsRevision").GetInt64();
        // Positive control: each owner moves its own account with a matching expected revision.
        var aOther = (await a.ManualAccount(Isolated.Marker("account-a2"))).GetProperty("id").GetGuid();
        var moved = await a.Post("/api/accounts/portfolio", new MoveAccountsRequest([aOther], aPortfolio));
        Assert.Equal(aPortfolio, Assert.Single(moved.EnumerateArray()).GetProperty("portfolioId").GetGuid());
        var aPaths = new[] { "/api/accounts", "/api/portfolios", $"/api/portfolios/{aPortfolio}" };
        var bPaths = new[] { "/api/accounts", "/api/portfolios", $"/api/portfolios/{bPortfolio}" };
        var state = await Snapshot.Of((a, aPaths), (b, bPaths));
        var aState = await Snapshot.Of(a, aPaths);
        var move = (Func<Guid, MoveAccountsRequest> body) => Isolated.Request(HttpMethod.Post, _ => "/api/accounts/portfolio", body);
        var create = (Func<Guid, CreatePortfolioRequest> body) => Isolated.Request(HttpMethod.Post, _ => "/api/portfolios", body);
        var withForeign = Isolated.Marker("with-foreign");

        // Well-formed requests that select a foreign account beside an owned one, with or without matching expected
        // revisions for both: all or nothing, refused like an unknown ID by the account lookup.
        await state.After(() => Isolated.NotFound(b, aId, move(id => new MoveAccountsRequest([bId, id], bPortfolio)), AccountNotFound));
        await state.After(() => Isolated.NotFound(b, aId, move(id => new MoveAccountsRequest([bId, id], bPortfolio,
            [new(bId, bRevision), new(id, aRevision)])), AccountNotFound));
        // Unlinking (null portfolio) a foreign account.
        await state.After(() => Isolated.NotFound(b, aId, move(id => new MoveAccountsRequest([id], null)), AccountNotFound));
        await state.After(() => Isolated.NotFound(b, aId, move(id => new MoveAccountsRequest([id], null, [new(id, aRevision)])), AccountNotFound));
        // An owned account moved into a foreign portfolio.
        await state.After(() => Isolated.NotFound(b, aPortfolio, move(id => new MoveAccountsRequest([bId], id, [new(bId, bRevision)])),
            "Portfolio not found."));
        // Create-with-accounts selecting a foreign account, with or without matching expected revisions, creates no
        // portfolio (B's portfolio list is part of the snapshot) and moves nothing.
        await state.After(() => Isolated.NotFound(b, aId, create(id => new CreatePortfolioRequest(withForeign, null, [bId, id])), AccountNotFound));
        await state.After(() => Isolated.NotFound(b, aId, create(id => new CreatePortfolioRequest(withForeign, null, [bId, id],
            [new(bId, bRevision), new(id, aRevision)])), AccountNotFound));

        // Validation, not ownership: expected revisions that name an account outside the selection are malformed and
        // refused before any account is read, identically for a foreign and an unknown ID.
        await state.After(async () => Assert.Equal(HttpStatusCode.BadRequest, await Isolated.SameAsUnknown(b, aId,
            move(id => new MoveAccountsRequest([bId], bPortfolio, [new(bId, bRevision), new(id, aRevision)])))));
        await state.After(async () => Assert.Equal(HttpStatusCode.BadRequest, await Isolated.SameAsUnknown(b, aId,
            create(id => new CreatePortfolioRequest(withForeign, null, [bId], [new(bId, bRevision), new(id, aRevision)])))));

        // The owned account still moves with its unchanged revision: no attempt consumed it or partly applied.
        var final = Assert.Single((await b.Post("/api/accounts/portfolio", new MoveAccountsRequest([bId], bPortfolio, [new(bId, bRevision)])))
            .EnumerateArray());
        Assert.Equal(bPortfolio, final.GetProperty("portfolioId").GetGuid());
        await aState.Unchanged();
        // Positive control for create-with-accounts and expected revisions: B's own account moves into the new portfolio.
        var created = await b.Post("/api/portfolios", new CreatePortfolioRequest(Isolated.Marker("with-own"), null, [bId],
            [new(bId, final.GetProperty("settingsRevision").GetInt64())]), HttpStatusCode.Created);
        Assert.Equal(created.GetProperty("id").GetGuid(), (await b.GetJson($"/api/accounts/{bId}")).GetProperty("portfolioId").GetGuid());
        await aState.Unchanged();
        Isolated.NoLeak(await b.GetText("/api/portfolios"), withForeign, aPortfolio, aId);
    }

    private const string AccountNotFound = "Account not found.";

    [PostgresFact]
    [Covers("GET", "/api/accounts/{id:guid}/credential")]
    [Covers("PUT", "/api/accounts/{id:guid}/credential")]
    [Covers("DELETE", "/api/accounts/{id:guid}/credential")]
    public async Task Credential_metadata_of_another_owner_answers_like_an_unknown_account()
    {
        var (a, b) = await api.Owners();
        var account = (await a.IndexAccount(Isolated.Marker("index-a"))).GetProperty("id").GetGuid();
        var saved = await a.Send(HttpMethod.Put, $"/api/accounts/{account}/credential", new { token = SecurityFixture.Token });
        Assert.Equal("valid", saved.GetProperty("credential").GetProperty("status").GetString());
        var path = $"/api/accounts/{account}/credential";
        var state = await Snapshot.Of(a, path, $"/api/accounts/{account}");

        await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Get, id => $"/api/accounts/{id}/credential")));
        await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Put, id => $"/api/accounts/{id}/credential",
            _ => new { token = SecurityFixture.Token })));
        await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Delete, id => $"/api/accounts/{id}/credential")));

        // B's own account on the same index stores its own credential; A's stays.
        var own = (await b.IndexAccount(Isolated.Marker("index-b"))).GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, (await b.GetJson($"/api/accounts/{own}/credential")).GetProperty("credential").ValueKind);
        await b.Send(HttpMethod.Put, $"/api/accounts/{own}/credential", new { token = SecurityFixture.Token });
        await b.Send(HttpMethod.Delete, $"/api/accounts/{own}/credential", expected: HttpStatusCode.NoContent);
        await state.Unchanged();
    }

    [PostgresFact]
    [Covers("GET", "/api/venues/{venueId}/accounts")]
    [Covers("POST", "/api/venues/{venueId}/accounts/credential")]
    public async Task Discovery_marks_only_the_callers_imports_of_a_shared_source()
    {
        var (a, b) = await api.Owners();
        var account = (await a.IndexAccount(Isolated.Marker("index-a"))).GetProperty("id").GetGuid();
        var venue = SecurityFixture.Venue.Id;
        var query = $"/api/venues/{venue}/accounts?address={IsolationVenues.Address}";
        var aFound = await a.GetJson(query);
        Assert.Equal(account, Candidate(aFound, IsolationVenues.Index).GetProperty("existingAccountId").GetGuid());
        foreach (var found in new[] { await b.GetJson(query),
            await b.Post($"/api/venues/{venue}/accounts/credential", new { address = IsolationVenues.Address, token = SecurityFixture.Token }) })
        {
            Assert.Equal(JsonValueKind.Null, Candidate(found, IsolationVenues.Index).GetProperty("existingAccountId").ValueKind);
            Isolated.NoLeak(found.GetRawText(), account);
        }
        // The same native index imports for B as a separate account.
        var own = (await b.IndexAccount(Isolated.Marker("index-b"))).GetProperty("id").GetGuid();
        Assert.NotEqual(account, own);
        Assert.Equal(own, Candidate(await b.GetJson(query), IsolationVenues.Index).GetProperty("existingAccountId").GetGuid());
        Assert.Equal(account, Candidate(await a.GetJson(query), IsolationVenues.Index).GetProperty("existingAccountId").GetGuid());
    }

    private static JsonElement Candidate(JsonElement discovery, string source) =>
        discovery.GetProperty("accounts").EnumerateArray().Single(c => c.GetProperty("sourceId").GetString() == source);

    [PostgresFact]
    [Covers("GET", "/api/accounts/{id:guid}/snapshot")]
    [Covers("GET", "/api/accounts/{id:guid}/fills")]
    [Covers("GET", "/api/accounts/{id:guid}/instruments")]
    [Covers("GET", "/api/accounts/{id:guid}/candles")]
    [Covers("GET", "/api/accounts/{id:guid}/market-context")]
    [Covers("GET", "/api/accounts/{id:guid}/market-stream")]
    [Covers("POST", "/api/accounts/{id:guid}/sync")]
    public async Task Account_reads_and_refresh_of_another_owner_answer_like_unknown_ones()
    {
        var (a, b) = await api.Owners();
        var synced = await a.SyncedVenueAccount(Isolated.Marker("venue-a"));
        var account = synced.GetProperty("id").GetGuid();
        Assert.Equal("synced", synced.GetProperty("syncStatus").GetString());
        // Positive controls for every read.
        Assert.NotEqual(JsonValueKind.Null, (await a.GetJson($"/api/accounts/{account}/snapshot")).ValueKind);
        Assert.NotEmpty((await a.GetJson($"/api/accounts/{account}/fills")).EnumerateArray());
        await a.GetJson($"/api/accounts/{account}/instruments");
        await a.GetJson($"/api/accounts/{account}/candles?instrument=BTC&interval=1h");
        await a.GetJson($"/api/accounts/{account}/market-context?instrument=BTC");
        var subscriptions = api.Venues.Stream.Subscriptions.Count;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{account}/market-stream?instrument=BTC&interval=1h"))
        using (var stream = await a.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            Assert.Equal(subscriptions + 1, api.Venues.Stream.Subscriptions.Count);
        }
        // Persisted records beside the imported facts, so the snapshot's fingerprints cover nonempty monitor, event and
        // notification rows: an active Play makes the account monitored and appends its events; a test notification.
        await Planned(a, account);
        await using (var hub = await IsolationNotifications.CreateAsync(api, a)) await hub.Send(a);
        var rows = await Persisted.Rows(a.Journal!, a.Id);
        Assert.All(Seeded, table => Assert.True(rows[table].Rows > 0, $"{table} is empty."));
        var reads = api.Venues.Reader.Fixture.Reads;
        var orderReads = api.Venues.Orders.Reads;
        var state = await Snapshot.Of(a, $"/api/accounts/{account}", $"/api/accounts/{account}/snapshot", $"/api/accounts/{account}/fills");

        foreach (var path in new Func<Guid, string>[]
        {
            id => $"/api/accounts/{id}/snapshot",
            id => $"/api/accounts/{id}/fills",
            id => $"/api/accounts/{id}/instruments",
            id => $"/api/accounts/{id}/candles?instrument=BTC&interval=1h",
            id => $"/api/accounts/{id}/market-context?instrument=BTC",
            id => $"/api/accounts/{id}/market-stream?instrument=BTC&interval=1h"
        })
            await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Get, path)));
        // No foreign stream subscribed to the venue.
        Assert.Equal(subscriptions + 1, api.Venues.Stream.Subscriptions.Count);
        // A refresh of a foreign account answers like an unknown one and never reads the venue for it.
        await state.After(() => Isolated.SameAsUnknown(b, account, Isolated.Request(HttpMethod.Post, id => $"/api/accounts/{id}/sync")));
        Assert.Equal((reads, orderReads), (api.Venues.Reader.Fixture.Reads, api.Venues.Orders.Reads));
    }

    /// <summary>Owner-scoped tables a synced account with an active Play and a notification fills.</summary>
    private static readonly string[] Seeded =
    [
        "imported_fills", "imported_orders", "funding_payments", "account_funding_states", "account_snapshots", "plays",
        "monitor_accounts", "events", "event_cursors", "notifications", "notification_deliveries", "notification_sources"
    ];

    private static async Task Planned(Owner owner, Guid account)
    {
        var draft = await owner.Post("/api/plays", new CreatePlayRequest(account, "BTC", "venue", Isolated.Marker("monitored"), TestPlays.Plan()),
            HttpStatusCode.Created);
        await owner.Post($"/api/plays/{draft.GetProperty("summary").GetProperty("id").GetGuid()}/status",
            new ChangePlayStatusRequest(draft.GetProperty("summary").GetProperty("version").GetInt64(), "planned"));
    }

    /// <summary>
    /// Self-test of <see cref="Snapshot"/>: a change to persisted funding that none of the snapshot's responses show
    /// (the account, its snapshot and its fills) must still fail <see cref="Snapshot.Unchanged"/>.
    /// </summary>
    [PostgresFact]
    public async Task Snapshot_fails_on_a_persisted_funding_change_no_snapshot_path_shows()
    {
        var a = await api.Owner();
        var account = (await a.SyncedVenueAccount(Isolated.Marker("venue-a"))).GetProperty("id").GetGuid();
        string[] paths = [$"/api/accounts/{account}", $"/api/accounts/{account}/snapshot", $"/api/accounts/{account}/fills"];
        var responses = await Isolated.State(a, paths);
        var state = await Snapshot.Of(a, paths);
        var originalRows = await Persisted.Of(a.Journal!, a.Id);
        await state.Unchanged();
        foreach (var (table, change, undo) in new[]
        {
            ("funding_payments", "\"Amount\" = \"Amount\" + 123", "\"Amount\" = \"Amount\" - 123"),
            ("account_funding_states", "\"LastAttemptToUtc\" = \"LastAttemptToUtc\" + interval '1 second'",
                "\"LastAttemptToUtc\" = \"LastAttemptToUtc\" - interval '1 second'")
        })
        {
            Assert.Equal(1, await Update(a, table, change));
            // The responses do not show it; only the persisted fingerprint does.
            Assert.Equal(responses, await Isolated.State(a, paths));
            await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(state.Unchanged);
            Assert.Equal(1, await Update(a, table, undo));
            // Undo restores facts but deliberately advances the monotonic cache revision. The isolation
            // harness still fingerprints that row; only this self-test rebases after checking restored facts.
            Assert.Equal(originalRows.Where(r => !r.StartsWith("activity_facts_revisions ", StringComparison.Ordinal)),
                (await Persisted.Of(a.Journal!, a.Id)).Where(r => !r.StartsWith("activity_facts_revisions ", StringComparison.Ordinal)));
            state = await Snapshot.Of(a, paths);
            await state.Unchanged();
        }
    }

    private async Task<int> Update(Owner owner, string table, string set)
    {
        await using var connection = new Npgsql.NpgsqlConnection(api.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand($"UPDATE {table} SET {set} WHERE \"OwnerId\" = @owner", connection);
        command.Parameters.AddWithValue("owner", owner.Id);
        return await command.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task Sealed_credential_copied_to_another_owners_account_cannot_be_opened()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.IndexAccount(Isolated.Marker("index-a"))).GetProperty("id").GetGuid();
        var bAccount = (await b.IndexAccount(Isolated.Marker("index-b"))).GetProperty("id").GetGuid();
        await a.Send(HttpMethod.Put, $"/api/accounts/{aAccount}/credential", new { token = SecurityFixture.Token });
        Domain.Credentials.AccountCredential original;
        await using (var aDb = api.Database.Context(a.Id))
            original = await aDb.AccountCredentials.SingleAsync();
        Assert.Equal(SecurityFixture.Token, api.Vault.Open(a.Id, aAccount, original.Purpose, original.Sealed()));

        // Copy the sealed bytes onto B's account, as a restore or a database edit could.
        await using (var bDb = api.Database.Context(b.Id))
        {
            bDb.AccountCredentials.Add(new()
            {
                OwnerId = b.Id, AccountId = bAccount, Purpose = original.Purpose, KeyId = original.KeyId,
                Nonce = original.Nonce, Ciphertext = original.Ciphertext, Tag = original.Tag, Scope = original.Scope,
                ExpiresAt = original.ExpiresAt, CreatedAt = original.CreatedAt, LastVerifiedAt = original.LastVerifiedAt
            });
            await bDb.SaveChangesAsync();
        }
        // The associated data binds owner, account and purpose: every rebinding fails to open.
        Assert.Throws<CredentialStorageException>(() => api.Vault.Open(b.Id, bAccount, original.Purpose, original.Sealed()));
        Assert.Throws<CredentialStorageException>(() => api.Vault.Open(b.Id, aAccount, original.Purpose, original.Sealed()));
        Assert.Throws<CredentialStorageException>(() => api.Vault.Open(a.Id, bAccount, original.Purpose, original.Sealed()));
        // Through the API, B's copied row is unusable and A's own is untouched.
        var copied = await b.GetJson($"/api/accounts/{bAccount}/credential");
        Assert.Equal("unavailable", copied.GetProperty("credential").GetProperty("status").GetString());
        Assert.Equal("valid", (await a.GetJson($"/api/accounts/{aAccount}/credential")).GetProperty("credential").GetProperty("status").GetString());
    }
}

/// <summary>The cases above in Token mode, with the suite's test scheme selecting the owner.</summary>
public sealed class WorkspaceIsolationTests(IsolationApi api) : WorkspaceIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode: two signed-in users, each request mapped to its owner by the session scheme.</summary>
public sealed class WalletWorkspaceIsolationTests(WalletIsolationApi api) : WorkspaceIsolationCases(api), IClassFixture<WalletIsolationApi>;
