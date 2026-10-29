

namespace Vessel.Tests;

public sealed class OwnerlessSqlClassifierTests
{
    [Theory]
    [InlineData("/* account lookup */ SELECT * FROM accounts")]
    [InlineData("-- account lookup\nSELECT * FROM accounts")]
    [InlineData("  \n (SELECT * FROM accounts) UNION ALL (SELECT * FROM accounts)")]
    [InlineData("select * from accounts")]
    [InlineData("SELECT @owner AS requested_owner, a.* FROM accounts a")]
    [InlineData("SELECT a.owner_id = @owner AS mine, a.* FROM accounts a")]
    [InlineData("SELECT * FROM accounts WHERE \"Id\" = @id OR owner_id = @owners")]
    [InlineData("SELECT * FROM accounts -- WHERE owner_id = @owner")]
    [InlineData("SELECT * FROM accounts /* AND owner_id = @owner */")]
    [InlineData("DELETE FROM events WHERE owner_id=@owner; DELETE FROM event_dedup")]
    [InlineData("INSERT INTO events(owner_id,event_id) VALUES(@other,@owner)")]
    [InlineData("INSERT INTO events(event_id,payload) VALUES(@id,@owner)")]
    [InlineData("TRUNCATE accounts")]
    [InlineData("LOCK TABLE accounts IN EXCLUSIVE MODE")]
    [InlineData("SET TRANSACTION READ ONLY")]
    [InlineData("MERGE INTO accounts a USING staged s ON a.\"Id\" = s.id WHEN MATCHED THEN DELETE")]
    [InlineData("truncate accounts")]
    [InlineData("lock table accounts in exclusive mode")]
    [InlineData("set search_path to public")]
    [InlineData("do $$ begin delete from accounts; end $$")]
    [InlineData("do 'begin delete from accounts; end'")]
    [InlineData("do E'begin delete from accounts; end'")]
    [InlineData("do language plpgsql $$ begin delete from accounts; end $$")]
    [InlineData("/* outer /* inner */ end */ SELECT * FROM accounts")]
    [InlineData("/* outer /* inner */ end */ truncate accounts")]
    // Fail-closed detection: any command keyword in any case, after ';' or behind a comment, is SQL.
    [InlineData("do/*comment*/'begin delete from accounts; end'")]
    [InlineData("do /*comment*/ E'begin delete from accounts; end'")]
    [InlineData("; SELECT * FROM accounts")]
    [InlineData("/* comment */ ; DELETE FROM accounts")]
    [InlineData("begin; DELETE FROM accounts; commit;")]
    [InlineData("note: -- then delete from accounts")]
    [InlineData("Show all; delete from accounts")]
    [InlineData("UPDATE accounts SET name='overwritten' WHERE CASE WHEN false AND owner_id=@owner AND false THEN true ELSE true END")]
    [InlineData("SELECT * FROM accounts WHERE ARRAY[false AND owner_id=@owner AND false,true] @> ARRAY[true]")]
    public void Ownerless_or_disguised_sql_needs_review(string sql) => Assert.True(OwnerlessSqlClassifier.RequiresReview(sql));

    [Theory]
    [InlineData("SELECT * FROM accounts WHERE owner_id = @owner")]
    [InlineData("SELECT * FROM accounts WHERE id = {id} AND \"OwnerId\" = {db.CurrentOwnerId} FOR UPDATE")]
    [InlineData("SELECT * FROM accounts a WHERE @ownerId = a.owner_id")]
    [InlineData("DELETE FROM events WHERE owner_id={account.OwnerId}; DELETE FROM event_dedup WHERE owner_id={account.OwnerId};")]
    [InlineData("INSERT INTO notification_cursors(owner_id) VALUES(@owner) ON CONFLICT DO NOTHING")]
    [InlineData("INSERT INTO n(owner_id,content,at) VALUES(@owner,CAST(@content AS jsonb),@now)")]
    [InlineData("INSERT INTO m (account_id, \"OwnerId\") VALUES ({accountId}, {db.CurrentOwnerId})")]
    [InlineData("INSERT INTO f(owner_id,account_id) SELECT @owner, t.account_id FROM unnest(@accounts) AS t(account_id)")]
    [InlineData("SELECT count(*) FILTER (WHERE state='unread') FROM d WHERE d.owner_id=@owner AND (d.channel='in-app' AND x) LIMIT 1")]
    [InlineData("UPDATE c SET n=n+1 WHERE owner_id=@owner RETURNING n")]
    [InlineData("INSERT INTO h(owner_id,at) VALUES(@owner,@now) ON CONFLICT (owner_id) DO UPDATE SET at=EXCLUDED.at WHERE h.at<EXCLUDED.at")]
    [InlineData("INSERT INTO f(owner_id,a) SELECT @owner, t.a FROM unnest(@a, @b) WITH ORDINALITY AS t(a, b, p) ORDER BY t.p ON CONFLICT DO NOTHING RETURNING a")]
    public void Owner_scoped_sql_passes(string sql) => Assert.False(OwnerlessSqlClassifier.RequiresReview(sql));

    // An owner predicate somewhere in the text is not enough: these read or write other owners' rows.
    [Theory]
    [InlineData("SELECT * FROM accounts WHERE owner_id=@owner OR TRUE")]
    [InlineData("SELECT * FROM accounts WHERE owner_id=@owner UNION ALL SELECT * FROM accounts")]
    [InlineData("WITH scoped AS (SELECT * FROM accounts WHERE owner_id=@owner) DELETE FROM accounts")]
    [InlineData("SELECT 'WHERE owner_id=@owner' AS note, a.* FROM accounts a")]
    [InlineData("INSERT INTO accounts(owner_id,name) VALUES(@owner,'mine'),(@other,'foreign')")]
    [InlineData("UPDATE accounts SET name = 'WHERE owner_id = @owner'")]
    [InlineData("SELECT 'WHERE owner_id = @owner' AS label FROM accounts")]
    [InlineData("SELECT * FROM accounts $q$ WHERE owner_id=@owner $q$")]
    [InlineData("SELECT * FROM accounts E'\\' WHERE owner_id=@owner '")]
    [InlineData("SELECT * FROM accounts /* /* */ WHERE owner_id=@owner */")]
    [InlineData("SELECT * FROM accounts AS \"x WHERE owner_id=@owner\"")]
    [InlineData("SELECT * FROM accounts WHERE NOT owner_id=@owner")]
    [InlineData("SELECT * FROM accounts WHERE owner_id=@owner IS NOT TRUE")]
    [InlineData("SELECT * FROM accounts WHERE x AND (owner_id=@owner AND y) IS NULL")]
    [InlineData("SELECT * FROM accounts a LEFT JOIN plays p ON p.owner_id=@owner")]
    [InlineData("SELECT e.payload FROM events e JOIN event_cursors c ON c.owner_id=@owner AND c.owner_id=e.owner_id")]
    [InlineData("SELECT * FROM accounts a, plays p WHERE a.owner_id=@owner")]
    [InlineData("SELECT (SELECT max(name) FROM accounts) AS leak FROM plays WHERE owner_id=@owner")]
    [InlineData("SELECT * FROM (SELECT * FROM accounts) a WHERE a.owner_id=@owner")]
    [InlineData("SELECT * FROM accounts WHERE owner_id=@owner AND {filter}")]
    [InlineData("UPDATE accounts SET name=p.name FROM plays p WHERE accounts.owner_id=@owner")]
    [InlineData("DELETE FROM accounts USING plays p WHERE accounts.owner_id=@owner")]
    [InlineData("INSERT INTO monitor_accounts(\"AccountId\",\"OwnerId\") VALUES({id},{db.CurrentOwnerId}) ON CONFLICT (\"AccountId\") DO UPDATE SET \"State\"='idle'")]
    [InlineData("INSERT INTO copies(owner_id,name) SELECT @owner, name FROM accounts")]
    [InlineData("INSERT INTO copies(owner_id,name) VALUES(@owner,(SELECT max(name) FROM accounts))")]
    // Ownership transfer: the rows are the owner's, but the assignment hands them to someone else.
    [InlineData("UPDATE accounts SET owner_id=@other WHERE owner_id=@owner")]
    [InlineData("UPDATE accounts a SET name=@n, a.owner_id=@other WHERE a.owner_id=@owner")]
    [InlineData("UPDATE accounts SET \"OwnerId\"={other} WHERE \"OwnerId\"={db.CurrentOwnerId}")]
    [InlineData("UPDATE accounts SET \"owner_id\"=@other WHERE owner_id=@owner")]
    [InlineData("UPDATE accounts SET (name, owner_id)=(@n, @other) WHERE owner_id=@owner")]
    [InlineData("INSERT INTO n(owner_id,id) VALUES(@owner,@id) ON CONFLICT(owner_id,id) DO UPDATE SET owner_id=@other")]
    [InlineData("INSERT INTO n(owner_id,id) VALUES(@owner,@id) ON CONFLICT(owner_id,id) DO UPDATE SET n.\"OwnerId\"=@other RETURNING id")]
    [InlineData("UPDATE accounts SET U&\"own\\0065r_id\"=@other WHERE owner_id=@owner")]
    [InlineData("INSERT INTO n(owner_id,id) VALUES(@owner,@id) ON CONFLICT(owner_id,id) DO UPDATE SET U&\"own\\0065r_id\"=@other")]
    [InlineData("UPDATE accounts SET u&\"own\\0065r_id\" = @other WHERE owner_id=@owner")]
    // Nothing the normaliser does not fully model is exempt: comments, quoted names, $ and prefixed or escaped strings.
    [InlineData("/* lookup */ SELECT * FROM accounts a WHERE @ownerId = a.owner_id")]
    [InlineData("SELECT * FROM accounts WHERE owner_id=@owner -- note")]
    [InlineData("SELECT * FROM accounts WHERE \"Id\" = {id} AND \"OwnerId\" = {db.CurrentOwnerId} FOR UPDATE")]
    [InlineData("INSERT INTO m (\"AccountId\", \"OwnerId\") VALUES ({accountId}, {db.CurrentOwnerId})")]
    [InlineData("SELECT * FROM \"accounts\" WHERE owner_id=@owner")]
    [InlineData("UPDATE accounts SET name=E'x\\'' WHERE owner_id=@owner")]
    [InlineData("UPDATE accounts SET name='x\\' WHERE owner_id=@owner")]
    [InlineData("SELECT $$x$$ FROM accounts WHERE owner_id=@owner")]
    [InlineData("SELECT a$b FROM accounts WHERE owner_id=@owner")]
    public void Owner_predicates_outside_the_safe_subset_need_review(string sql) =>
        Assert.True(OwnerlessSqlClassifier.RequiresReview(sql));

    // False positives cost one reviewed "Not SQL: …" entry; prose starting with a command word is SQL.
    [Theory]
    [InlineData("Set ConnectionStrings__Vessel for migration tooling.")]
    [InlineData("Do not switch providers.")]
    [InlineData("refresh")]
    [InlineData("reset-after-restore")]
    [InlineData("refresh must be true or false.")]
    [InlineData("Reset complete. Keep all hosts stopped.")]
    [InlineData("Copy failed. Check local files.")]
    [InlineData("Lock callback failed.")]
    [InlineData("Values")]
    [InlineData("Do it again.")]
    [InlineData("/* note /* nested */ still a note */ refresh")]
    [InlineData("Use --apply to copy the files.")]
    public void Prose_starting_with_a_command_word_is_sql(string text) => Assert.True(OwnerlessSqlClassifier.IsSql(text));

    [Theory]
    [InlineData("/v1/markets/id/{id}/trading-view-data")]
    [InlineData("Selected account is disabled.")]
    [InlineData("Updated 3 rows")]
    [InlineData("Settings")]
    [InlineData("Unsupported venue.")]
    [InlineData("😀")]
    public void Prose_is_not_sql(string text) => Assert.False(OwnerlessSqlClassifier.IsSql(text));

    [Fact]
    public void Literal_escapes_decode_as_utf16_without_throwing()
    {
        Assert.Equal("😀", OwnerlessSqlClassifier.Text("\"😀\""));
        Assert.Equal("😀", OwnerlessSqlClassifier.Text("\"\\xD83D\\xDE00\""));
        Assert.Equal("😀", OwnerlessSqlClassifier.Text("\"\\uD83D\\uDE00\""));
        Assert.Equal("\uD800x", OwnerlessSqlClassifier.Text("$\"\\uD800x\""));
        Assert.Equal("SELECT '😀' FROM accounts", OwnerlessSqlClassifier.Text("\"SELECT '\\uD83D\\uDE00' FROM accounts\""));
        Assert.False(OwnerlessSqlClassifier.LiteralRequiresReview("\"\\xD83D\\xDE00\""));
        // Raw literals may use more than three delimiter quotes; the whole token is found and its body read exactly.
        Assert.Equal("\"\"\"\"DELETE FROM accounts\"\"\"\"",
            OwnerlessSqlClassifier.SourceLiteral.Match("const string Unsafe = \"\"\"\"DELETE FROM accounts\"\"\"\";").Value);
        Assert.Equal("DELETE FROM accounts", OwnerlessSqlClassifier.Text("\"\"\"\"DELETE FROM accounts\"\"\"\""));
        Assert.True(OwnerlessSqlClassifier.LiteralRequiresReview("\"\"\"\"DELETE FROM accounts\"\"\"\""));
        Assert.True(OwnerlessSqlClassifier.LiteralRequiresReview("$$\"\"\"\"\"DELETE FROM accounts\"\"\"\"\""));
        Assert.True(OwnerlessSqlClassifier.LiteralRequiresReview("\"SELECT '\\uD800' FROM accounts\""));
    }

    [Fact]
    public void Undecodable_escapes_never_throw_and_sql_looking_literals_are_reviewed()
    {
        Assert.Equal("\uFFFD", OwnerlessSqlClassifier.Decode("\"\\U0000D800\"", out var complete));
        Assert.False(complete);
        Assert.False(OwnerlessSqlClassifier.LiteralRequiresReview("\"\\U00110000\""));
        Assert.True(OwnerlessSqlClassifier.LiteralRequiresReview("\"\\U00110000 WHERE owner_id=@owner; delete from accounts\""));
        Assert.True(OwnerlessSqlClassifier.LiteralRequiresReview("\"SELECT * FROM accounts WHERE owner_id=@owner\\U0000DC00\""));
    }

    [Fact]
    public void Source_literal_escapes_are_decoded_before_classification()
    {
        Assert.Equal("-- lookup\nSELECT \"Id\"", OwnerlessSqlClassifier.Text("\"-- lookup\\nSELECT \\\"Id\\\"\""));
        Assert.Equal("SELECT \"OwnerId\"", OwnerlessSqlClassifier.Text("$@\"SELECT \"\"OwnerId\"\"\""));
        Assert.True(OwnerlessSqlClassifier.RequiresReview(OwnerlessSqlClassifier.Text("\"-- lookup\\nSELECT * FROM accounts\"")));
        Assert.Equal("SELECT * FROM accounts", OwnerlessSqlClassifier.Text("\"S\\u0045LECT * FROM accounts\""));
        Assert.Equal("SELECT * FROM accounts", OwnerlessSqlClassifier.Text("$\"S\\U00000045LECT * FROM accounts\""));
        Assert.Equal("SELECT * FROM accounts", OwnerlessSqlClassifier.Text("\"S\\x45LECT * FROM accounts\""));
        Assert.Equal("SELECT * FROM accounts", OwnerlessSqlClassifier.Text("\"S\\x0045LECT * FROM accounts\""));
        Assert.Equal("\a\b\f\v\u001b\0x", OwnerlessSqlClassifier.Text("\"\\a\\b\\f\\v\\e\\0x\""));
        Assert.Equal("\U0001F600", OwnerlessSqlClassifier.Text("\"\\U0001F600\""));
        Assert.True(OwnerlessSqlClassifier.RequiresReview(OwnerlessSqlClassifier.Text("\"S\\u0045LECT * FROM accounts\"")));
        Assert.True(OwnerlessSqlClassifier.RequiresReview(OwnerlessSqlClassifier.Text("$\"\\x44\\x45LETE FROM accounts\"")));
    }
}
