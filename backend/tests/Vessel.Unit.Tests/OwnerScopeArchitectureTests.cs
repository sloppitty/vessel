using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vessel.Tests;

public sealed class OwnerScopeArchitectureTests
{
    [Fact]
    public void Background_work_never_creates_an_unassigned_scope()
    {
        foreach (var path in Sources().Where(p => p.Contains("/Vessel.Worker/", StringComparison.Ordinal) ||
                     p.Contains("/Vessel.Messaging/", StringComparison.Ordinal) ||
                     Path.GetFileName(p).StartsWith("Monitor", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotMatch(@"\b(IServiceScopeFactory|CreateScope|CreateAsyncScope)\b", source);
        }
    }

    [Fact]
    public void Query_filters_are_never_bypassed()
    {
        foreach (var path in Sources()) Assert.DoesNotContain("IgnoreQueryFilters", File.ReadAllText(path));
    }

    [Fact]
    public void Ownerless_sql_is_an_explicit_reviewed_inventory()
    {
        // SQL text hashes make an added statement (even in an approved file) require an inventory review.
        // Schema migrations are separately reviewed DDL, not runtime owner-scope bypasses.
        // Advisory locks on owner-derived or previously owner-validated entity keys are inventoried too.
        // OwnerlessSqlClassifier decides which literals are SQL and which fall inside its small owner-safe subset.
        // To add an entry, review the statement's scope, then copy its path|hash from this assertion's message.
        string[] reviewed =
        [
            // Revision-only read: exact assigned owner parameter inside a repeatable-read snapshot; absent means revision zero.
            "Vessel.Persistence/CachedActivitySummaryReader.cs|404194565bdca94c05578fa57d66abe1700797d0437acb7c314b332ed84cd0be",
            "Vessel.Api/EvidenceCopyCommand.cs|c9b34ee7f02a2f083c971b5bcfd2b6dfb239af4970d91d8e56dc616d55a3d9c4",
            // Offline evidence copy: read-only transaction mode for its metadata read; touches no rows.
            "Vessel.Api/EvidenceCopyCommand.cs|6c20e5345ee8028f61cc0541aeb60699e5cffa99c8f79ff7f8c62b0a8dad1837",
            // Server-wide identity invitations: atomic use consumption by code hash, expiry and remaining uses.
            "Vessel.Identity/IdentityService.cs|c26ffac165c6420b5dab892b98bdc6fffed4d9b01eb5f98c3e15838abdc753aa",
            // Identity-schema advisory lock only; the raw literal keeps its quoted suffix inside the SQL hash.
            "Vessel.Identity/IdentityTransaction.cs|412856c9009c100267bcaed2ee98f7bd0fadcb134bbb68fd42b5070d64427a34",
            // Identity-schema user row lock by parameterized user ID; no journal tables are accessed.
            "Vessel.Identity/IdentityTransaction.cs|e4012de46b9a1236d2044f1cb200566ccf1334562c5e9f4aecdf08440632800b",
            "Vessel.Messaging/MessagingDeployments.cs|7cef9def011385ddc17e6a8b4adb587d48f430370a1b13ad73d8395985ec579b",
            "Vessel.Messaging/MessagingDeployments.cs|a16bdafb912a647befee6a34e34df9a2211d16df05d1454b8f9d412ff1e241ae",
            "Vessel.Persistence/DiaryStore.cs|c020f566091297d43f5450ac02e794b1ea045d677575f85160310daa91b8fe9b",
            "Vessel.Persistence/Events/EventRetention.cs|05f8fdd414fdbae35682179a42f93c4155253edbfa66a0cbc539f9699a93bc96",
            "Vessel.Persistence/Events/EventStreamEpoch.cs|6b5c6a08e6efcc41d3867d722197a1f8c7afed5900c702f3305cd90c32078937",
            "Vessel.Persistence/Events/Subscriptions/EventCatchUp.cs|7225f3f3ae3c860c6e54be3ec2ae2d6512c0728b4ac7d97355722ffff8a5fed6",
            "Vessel.Persistence/Events/Subscriptions/EventDispatcher.cs|7225f3f3ae3c860c6e54be3ec2ae2d6512c0728b4ac7d97355722ffff8a5fed6",
            "Vessel.Persistence/Events/Subscriptions/EventReceiptAdmin.cs|7225f3f3ae3c860c6e54be3ec2ae2d6512c0728b4ac7d97355722ffff8a5fed6",
            "Vessel.Persistence/Events/Subscriptions/SubscriptionProgress.cs|29f0701052dbb3611ea20ace64fc9d2907dbdaedc4d05debf78bdb149406d60c",
            "Vessel.Persistence/Events/Subscriptions/SystemEventSubscriptionStore.cs|29f0701052dbb3611ea20ace64fc9d2907dbdaedc4d05debf78bdb149406d60c",
            "Vessel.Persistence/Events/Subscriptions/SystemEventSubscriptionStore.cs|33eb5f4699ea96d9ad88233bdce435edf6aac8de7ad25117b6e0983e0b899441",
            "Vessel.Persistence/Events/Subscriptions/SystemEventSubscriptionStore.cs|3cee12e62179e1aadd9bfcb5c7637482f84fa2b9659157b483e1643147ede685",
            "Vessel.Persistence/Events/Subscriptions/SystemEventSubscriptionStore.cs|c208652efd5fa6ecf778cf7f9cad719648b4c8377d222db0aba9bb202854a90f",
            "Vessel.Persistence/Events/Subscriptions/SystemEventSubscriptionStore.cs|d861bf11b70ccc91d89b833f9d76ac9e59c6ce1b5ec881e11eaa6b15fe07ba1a",
            "Vessel.Persistence/EvidenceStore.cs|c020f566091297d43f5450ac02e794b1ea045d677575f85160310daa91b8fe9b",
            "Vessel.Persistence/ExecutionStore.cs|c020f566091297d43f5450ac02e794b1ea045d677575f85160310daa91b8fe9b",
            // Paper settings serialize by the assigned journal owner's advisory key; no owner rows are bypassed.
            "Vessel.Persistence/PaperStore.cs|ca35ce83e5046ca71b824a5e1463e632f7a8a1da5a1d567b9cbd4c62cf33a4d3",
            // Exact capital aggregates explicitly bind owner+account; suspended exposure correlates that owner's Play facts.
            "Vessel.Persistence/PaperStore.cs|03709fdf8f9f4ee1c8ac2bc0eff423494c13010ee2d3d8f01038e080312b4737",
            "Vessel.Persistence/PaperStore.cs|08d7fae2990f586cb97eda7d7b88003197c70bfdbcc6cea9c4e121d2ae85e800",
            // One snapshot of paper balances: outer account is assigned-owner filtered; every lateral aggregate correlates owner+account.
            "Vessel.Persistence/WorkspaceStore.Paper.cs|d536769e0d360a5190b0e2a654164db45c584b6035d845183f94a9538cccfb7e",
            // Paper schema probe returns no rows; the scheduler's sole global acquire/release protects shared public reads.
            "Vessel.Worker/PaperScheduler.cs|836ebdb2b79c67541fb7174785661d0bb001697ceeee2ce7e06a2a15ca0b2f5b",
            "Vessel.Worker/PaperScheduler.cs|5553fefce46ca5daf07aa5e90a20bae242e0cf92c34cbaec98217b8b48a581d1",
            "Vessel.Worker/PaperScheduler.cs|977d0760f4aac902b870946f7815ec72dd13e5590886281f3dbf0dc207acf752",
            // Shared venue admission: the same server-wide advisory key as the real monitor; only a global lease count is read.
            "Vessel.Worker/PaperScheduler.cs|2e6d3566b15d1febae42a7bb464483d062e02708eeb0f7b7113a41fe28ed2428",
            "Vessel.Worker/PaperScheduler.cs|0bd82837aabbbd4c1ff5587c1b31d0a7c2f139b7b41ec238ba794e40b6c872b9",
            "Vessel.Worker/PaperScheduler.cs|e83477b56188aca19b8fe5b167e717c77061df36b5e9555b425734927803888f",
            // System discovery returns owner IDs only, including every notification table and suspended owners.
            "Vessel.Persistence/JournalOwnerInventory.cs|5b331c2e2976398122197f9609eb633e348703851c0d1c70e635bb2a344a75cd",
            // Notification paging assembles both statements with the constant `visible` fragment, which
            // includes n.owner_id=@owner; parameters always bind the current scope's owner.
            "Vessel.Persistence/Notifications/NotificationStore.cs|d03c88d4e53ec73a5f089f774c05085e0223717c18d88d256fe7b673ca4ec6c7",
            "Vessel.Persistence/Notifications/NotificationStore.cs|d30491db273f29ecb15d7b278b9db3d849a3e75af4d2d5cbad9aef0e1a5807c8",
            // Advisory key is prefixed with the scope's owner GUID; it accesses no owner rows.
            "Vessel.Persistence/Notifications/NotificationStore.cs|24e428e35918edb014abb289e0a62af4270d36e3b2494a023e892e5068fb1ed2",
            // System-only notification health: configured deployment IDs, no owner rows or owner transaction.
            "Vessel.Persistence/Notifications/NotificationWorkerState.cs|5f9192e9e09f282f927f5e2cc48e654e01ee119cf0a935c10218dff4fc629238",
            "Vessel.Persistence/Notifications/NotificationWorkerState.cs|f75cf6a3ee0394344375a8242df94a8756058aa8f107353fef28663d45c69553",
            // Retention discovery returns only notification cursor/source owner IDs in bounded index-keyset pages.
            // No journal facts, owner directory or status filter: suspended owners' derived alerts still expire.
            "Vessel.Persistence/Notifications/SystemNotificationStore.cs|4cf27d1a1a94c5bc022ecda2a317013fd1e76b1f37682a7ed6550510acd42dc7",
            // Not SQL: server-authored notification title beginning with the word "Update".
            "Vessel.Application/Notifications/NotificationComposer.cs|fb59481d31b3a452f939961f54a0da2ba0ebfb1c04c24b7cd273dcf14f79c639",
            "Vessel.Persistence/PlayStore.cs|c020f566091297d43f5450ac02e794b1ea045d677575f85160310daa91b8fe9b",
            "Vessel.Persistence/WorkspaceStore.cs|c020f566091297d43f5450ac02e794b1ea045d677575f85160310daa91b8fe9b",
            // B2: reviewed system discovery/state, transactional venue count and budget-lock statements.
            "Vessel.Persistence/MonitorStore.cs|11fb326c24d434a2b59624a7e4a796561d98ef3e2291f2682033d47b113996e3",
            "Vessel.Persistence/MonitorStore.cs|5d0a1ed4c9cf1315a1cb064478718b843e28efcb64c8bb63578411302ca9e1be",
            "Vessel.Persistence/MonitorStore.cs|2b6dd27282cdf69a0bfa64cbc1a4de96621d46d16d63d39f96373947271daf1d",
            // System due-candidate discovery (§9.1): keys only, every owner in (due, owner, account) order with no per-owner
            // cut; each claim then rechecks status, deadlines and budgets inside that owner's scope.
            "Vessel.Persistence/SystemMonitorStore.cs|d006eb6260b4719db6fcb5d4b50581c7496ae0e5bd6dbbebf9df0fdb498e664d",
            "Vessel.Persistence/ServerStateStore.cs|40c74c937dd0eb512282090d98087bea4726000d71185d726dbbdd89e80b5b51",
            "Vessel.Persistence/ServerStateStore.cs|4de6036152e376cb02952ca7410a4db74f2a434f16091e168cf64995f90293f8",
            "Vessel.Persistence/ServerStateStore.cs|ee73f5478e13936ed833caed3737b495efb924b2d70cbf4c9d46ca0f8ff1593a",
            "Vessel.Persistence/ServerStateStore.cs|6973e58fc649dacc48f32b3733651715ab8955080a0b79a97ecf85be116dc12c",
            "Vessel.Persistence/ServerStateStore.cs|e6dc41ee45eddfeaad9785f33c42534223f79ae4627170351a604402d4b9f9fb",
            // Reviewed outside the classifier's owner-safe subset (joins, subqueries, CTEs, OR inside AND-ed groups):
            // Run grouping: e.owner_id=@owner; cursor, subscription and sources join on e.owner_id.
            "Vessel.Persistence/Notifications/NotificationStore.cs|2e4a474f20be70278c263306f2ab99290e08adba7c8fe44ab05fc4e3a86c17eb",
            // Subject sources: CTE and s are owner-filtered; deliveries join on s.owner_id; the OR is inside an AND.
            "Vessel.Persistence/Notifications/NotificationStore.cs|09e99dedd0ebc3ed0fd43879379b208412b3bb5c3cf6847ded4ae49ed30c57e6",
            // Feed head: every scalar subquery filters owner_id=@owner.
            "Vessel.Persistence/Notifications/NotificationStore.cs|2773ae6d4cdb7845facddffd5856a71263426fdc752a68e1bd4f44fa7f46d1c6",
            // Feed page: the owner-scoped head plus a lateral d.owner_id=@owner page; Delivery is a constant column list.
            "Vessel.Persistence/Notifications/NotificationStore.cs|0447aee1702f9d12a32335189601f64e98c4cba9ea5e12d3aad8e11cc451b74c",
            // Popup claim: d.owner_id=@owner, n joins on d.owner_id, the account check on n.owner_id; OR is inside an AND.
            "Vessel.Persistence/Notifications/NotificationStore.cs|701bb1eb0d25c2ba12c391c67efa2f996f33f1a9c440ef6dea56fdd9847c6d2f",
            // One notification: n.owner_id=@owner; deliveries join on n.owner_id.
            "Vessel.Persistence/Notifications/NotificationStore.cs|b3a64abaf89b3ff014c7a7db36171e5ce8a1adb2feda5106539f507b1f59dc20",
            // Source pruning: the outer DELETE and its IN subquery both filter owner_id=@owner.
            "Vessel.Persistence/Notifications/NotificationStore.cs|2f165406dc9dd09ccca2c08e50ea368150c79d3efea3bc9cf216b3a5ac64f5ed",
            // Hub status: s.owner_id=@owner; the failed-receipt count is tied to s.owner_id.
            "Vessel.Persistence/Notifications/NotificationStore.cs|4b9c27cf5cada1fb1df64cd2b3590b64506baea367f0555f1a45116767585964",
            // State transition: every CTE and UPDATE filters @owner; callers pass constant predicates without OR.
            "Vessel.Persistence/Notifications/NotificationStore.cs|812e4f088a8cc88b291dc898a7ec138bd582ce4568a2579665d21c5fa1820900",
            // Notification deletion: every CTE, the DELETE and the cursor UPDATE filter @owner; constant predicates.
            "Vessel.Persistence/Notifications/NotificationStore.cs|d9cf24a27ca571ff67c90187a6f9ac0512de8b83c3b162e873b8fc2df51d352e",
            // Retention window: the CTE filters owner_id=@owner; the outer SELECT reads only that CTE.
            "Vessel.Persistence/Events/EventRetention.cs|08fe953a5aedb2f335f11e49a1484ed16e06953c61bf3e16b299d98429aa429f",
            // Lagging mark: UPDATE filters owner_id=@owner; the OR is inside a CASE in SET.
            "Vessel.Persistence/Events/EventRetention.cs|baa2742c79784d8c8358bae309148a6a222739eedec5632d01cbb6ab86ae1b7a",
            // Retention delete: the DELETE and its IN subquery filter owner_id=@owner; the cursor joins on e.owner_id.
            "Vessel.Persistence/Events/EventRetention.cs|07afcada101f2d867340f052fda079475f56d49cc4e9a936ea79990cc6d464c8",
            // History page: cursor CTE and lateral events both filter owner_id=@owner; the OR is inside an AND.
            "Vessel.Persistence/Events/VesselEventHistory.cs|b5665061b04cde94495b8ab29722016ac1f25809c2f51a7f0d664d0a128d67b2",
            // Baseline: inserts @owner, then copies fills filtered f."OwnerId"=@owner with their own owner ID.
            "Vessel.Persistence/Events/VesselEventStore.cs|8d594c5d4c42d91b8b5b39e88510a6fbe83d2efb33e3c486d2673e6c298caa99",
            // Catch-up scan: e.owner_id=@owner; subscription, cursor and receipts are tied to e.owner_id.
            "Vessel.Persistence/Events/Subscriptions/EventCatchUp.cs|951e3b3f0227f410c60234b5f6e650f3fde160d4782b5ed176f7e2dac488c60e",
            // Retry scan: r.owner_id=@owner; events and cursor join on r.owner_id.
            "Vessel.Persistence/Events/Subscriptions/EventCatchUp.cs|84dc3ad7952f911dc1079b72f343a76c32a154378a8959a11f78676f5c066e66",
            // Progress lock: s.owner_id=@owner; the cursor left-joins on s.owner_id.
            "Vessel.Persistence/Events/Subscriptions/EventCatchUp.cs|9518a2aa6c8732ff7d3ca41b587e42fd5ae8d5f74b76c3202f5e0b9a340e95fe",
            // Next unsettled event: e.owner_id=@owner; the receipt check is tied to e.owner_id.
            "Vessel.Persistence/Events/Subscriptions/EventCatchUp.cs|8ddd536c65505a48351e71b2df4f0160a0cd00256bc87d5d242bc2978d17719b",
            // Event lookup: e.owner_id=@owner; the cursor joins on e.owner_id.
            "Vessel.Persistence/Events/Subscriptions/EventDispatcher.cs|4d51b73db92ca8222bb6e5c31c9068de49820feac9cf1b469b3b9a1176b585d2",
            // Receipt lock: owner_id=@owner; the hole is the constant " SKIP LOCKED" suffix or empty.
            "Vessel.Persistence/Events/Subscriptions/EventDispatcher.cs|d5040693d4d45e4d8a3f3f26a8773b607a8ac68738ff73c5de06e6c0ed14ad97",
            // Receipt retry: rows match (owner, subscription, event) tuples selected with f.owner_id=@owner.
            "Vessel.Persistence/Events/Subscriptions/EventReceiptAdmin.cs|4fbcf294925800f60955937ecc536ad840bed216eb1a48435abfaee39d2f3402",
            // Subscription list: s.owner_id=@owner; receipt counts are tied to s.owner_id.
            "Vessel.Persistence/Events/Subscriptions/EventSubscriptionRegistry.cs|f028bd9900a04eb2f5dba91ffb25a20286a61cb7cec2ae79ceb140cfbfb7c10e",
            // Progress row: inserts @owner from the server-wide registration and the owner's own retention floor.
            "Vessel.Persistence/Events/Subscriptions/SubscriptionProgress.cs|6a6e7519684ffb58f2e2cf0a0af6c1baf38c72a25cd1c8174919b0c641bd0b3c",
            // Fail-closed rule (quoted identifiers are never exempt); each is single-table with "OwnerId" = the scope owner:
            // Open-play lock: plays by "AccountId" and "OwnerId" = db.CurrentOwnerId, real mode and a constant status list (Unresolved when settling).
            "Vessel.Persistence/ExecutionStore.cs|95a972ad76be70f47e94ff36bb33b3663f8b466b0c0e8a0de0fa503ba9cd944e",
            // Account row lock: "Id" and "OwnerId" = db.CurrentOwnerId.
            "Vessel.Persistence/ExecutionStore.cs|f893e1f76d22fb36ed9f16200cec96625e462cf6930d1e287e08257755036a55",
            "Vessel.Persistence/MonitorDiscrepancyStore.cs|f893e1f76d22fb36ed9f16200cec96625e462cf6930d1e287e08257755036a55",
            "Vessel.Persistence/WorkspaceStore.cs|72ddfb9d33e8a6c0834c1302db460ca114f05c0e3b1259f90146155ed31066aa",
            "Vessel.Persistence/MonitorStore.cs|20cbcb1a165df294219c574b593603de1429eb3b2ab39759ffa060fe820bcae0",
            // Monitor row creation after the owner-scoped account lock: inserts db.CurrentOwnerId; DO NOTHING.
            "Vessel.Persistence/MonitorStore.cs|586395464b82f79dcac1bbfd6571b1768bf9197512283ea27f8c445c917a08dc",
            // Monitor row lock: "AccountId" and "OwnerId" = db.CurrentOwnerId.
            "Vessel.Persistence/MonitorStore.cs|72f44c2fc895a9aac9af3b69284c3867a3c709d0ec01af43510b9184f19d203c",
            // Background claim lock: "Id" and "OwnerId" = db.CurrentOwnerId, FOR UPDATE SKIP LOCKED.
            "Vessel.Persistence/MonitorStore.cs|9e75433b5e6d7481a5eefd45c2235b5eb7866254bf98b31b55fca9d4cfb384e5",
            // Account enabled recheck: "OwnerId"=@owner (db.CurrentOwnerId) and "Id"=@account.
            "Vessel.Persistence/Notifications/NotificationStore.cs|9c7e4f9996ff9f771794a22d8ef4a53be2aa1826be74fb2d5a4524b050a3832b",
            // Play row lock: "Id" and "OwnerId" = db.CurrentOwnerId.
            "Vessel.Persistence/PlayStore.cs|92f5878e0e7c1be5bfd533b89b1af2f1d7c245edb9c8fc3f6981d2b3541b3f7d",
            // First sizing row: initial.OwnerId is checked equal to db.CurrentOwnerId just before; DO NOTHING.
            "Vessel.Persistence/SizingStore.cs|a9ceddf772233bbe66f61445850ce916446ce6f3013950049716c2e75596eaf5",
            // Risk settings first write: the current owner's row only, inserted then locked FOR UPDATE, like sizing settings.
            "Vessel.Persistence/RiskStore.cs|24f46de69ab4d3902c2b6df44d48bd4fe30691666e6e9fe239bfd3b5bd21f7cb",
            // Reporting settings first write (Phase 2, item 2): initial.OwnerId is checked equal to db.CurrentOwnerId just before;
            // DO NOTHING, then the current owner's row is locked FOR UPDATE, like risk settings.
            "Vessel.Persistence/ReportingStore.cs|1bae790ca809f53b6019e15a5181d427f9f8b144aaf89687de2f686e8ae90e79",
            // Portfolio row lock and its member accounts: "Id"/"PortfolioId" and "OwnerId" = db.CurrentOwnerId.
            "Vessel.Persistence/WorkspaceStore.cs|0c09a808bbe318f68eea1bda7d6dae1ac6ae5f56b4e988575a2787a457df5d2a",
            "Vessel.Persistence/WorkspaceStore.cs|029e4ce40dd7db519f675a99000b2f0e8cde631a8dd888f38f14cfd2f9e09a80",
            // Fail-closed false positives (prose that starts with a command word or holds "--" and one):
            // Not SQL: evidence-copy usage text (its "--source" option and the word "copy").
            "Vessel.Api/EvidenceCopyCommand.cs|cc48784ef9d9c5cdefe99d0627f044cd8a55dde3de8798853acabe2e8d60b024",
            // Not SQL: evidence-copy console messages beginning with "Copy".
            "Vessel.Api/EvidenceCopyCommand.cs|1fa96c1ca5ae47fff04b7b5ed6fdd6877aeb6ffaa19a7a232f7fe75f1f80643b",
            "Vessel.Api/EvidenceCopyCommand.cs|8b086bac7175c730c31e07567d55a49b05eab5ca83438af19bf3dd9fa8fe784f",
            "Vessel.Api/EvidenceCopyCommand.cs|cbc5c851f0dc70798f98079151240ea7ac2441729b0d45a86a994572a735efd6",
            // Not SQL: messaging command usage text ("--owner", "--all-owners" and the word "retry").
            "Vessel.Messaging/MessagingCommands.cs|ebacba1d8076b46e61466ea17e85b43e964482d9807da0c86c6a8b19a113ee68",
            // Not SQL: "reset" stream status value and feed cursor parameter name.
            "Vessel.Api/VesselEventEndpoints.cs|01be30bb4a27765c37462e6bf2a0bf8b6c109f9be9d81e6fd56455db1a736a43",
            "Vessel.Persistence/Notifications/NotificationStore.cs|01be30bb4a27765c37462e6bf2a0bf8b6c109f9be9d81e6fd56455db1a736a43",
            // Not SQL: candle query validation message.
            "Vessel.Application/MarketData/CandleService.cs|08cce1536657c12e9fa66fbf50b9df30e9dffdfd13bb6c5c22d81f4dbc0701c0",
            // Not SQL: "refresh" monitor trigger value.
            "Vessel.Application/Monitoring/MonitorCoordinator.cs|d6cc0a088c07683c65cd266860cab8d94b3a1937b17420d9da30ca299c09fb77",
            "Vessel.Domain/Monitoring/MonitorAccount.cs|d6cc0a088c07683c65cd266860cab8d94b3a1937b17420d9da30ca299c09fb77",
            "Vessel.Persistence/MonitorStore.cs|d6cc0a088c07683c65cd266860cab8d94b3a1937b17420d9da30ca299c09fb77",
            // Not SQL: "close" position-effect constant and RISEx candle field name.
            "Vessel.Domain/Workspace/AccountFacts.cs|310ff200149b44a32f124023d7caba19a1a890763a980606813d3a3d4a085d36",
            "Vessel.Infrastructure/Venues/Risex/RisexReader.cs|310ff200149b44a32f124023d7caba19a1a890763a980606813d3a3d4a085d36",
            // Not SQL: venue fill direction strings "Close Long" and "Close Short".
            "Vessel.Infrastructure/Venues/Hyperliquid/HyperliquidPerpetualReader.cs|980f257a57d5846f41e59e1d79fafb13929ea348e0eae3a28daf7132f4fe286d",
            "Vessel.Infrastructure/Venues/Hyperliquid/HyperliquidPerpetualReader.cs|f074918f528ce6faa19ada092244639377c9eb3603e338a3cb8057b9a2ba2913",
            "Vessel.Infrastructure/Venues/Lighter/LighterReader.Account.cs|980f257a57d5846f41e59e1d79fafb13929ea348e0eae3a28daf7132f4fe286d",
            "Vessel.Infrastructure/Venues/Lighter/LighterReader.Account.cs|f074918f528ce6faa19ada092244639377c9eb3603e338a3cb8057b9a2ba2913",
            "Vessel.Infrastructure/Venues/Risex/RisexReader.cs|980f257a57d5846f41e59e1d79fafb13929ea348e0eae3a28daf7132f4fe286d",
            "Vessel.Infrastructure/Venues/Risex/RisexReader.cs|f074918f528ce6faa19ada092244639377c9eb3603e338a3cb8057b9a2ba2913",
            // Not SQL: design-time DbContext factory error messages.
            "Vessel.Identity/IdentityDbContext.cs|e1b7a668a5e3ab1cf3318d78321688ad059827b7d660f00bda0ac249e2afee69",
            "Vessel.Persistence/VesselDbContextFactory.cs|e1b7a668a5e3ab1cf3318d78321688ad059827b7d660f00bda0ac249e2afee69",
            // Not SQL: check-config usage text ("--Vessel:Key=value" and the word "do").
            "Vessel.Api/ConfigurationCheckCommand.cs|ed23367322632085581515ce59e3a4ab16f07ac9f7dc62ae7df4d6fc17700445",
            // Not SQL: paper report coverage prose and the account-move instruction.
            "Vessel.Application/Review/ReviewService.cs|e540123a645048886c64b77cd996b33b11ba772faa86cca3911968a0a364ffde",
            "Vessel.Application/Workspace/WorkspaceService.cs|a1c0bc9961d3e38f15fd9b7b59072f141e41a0ba20319ee201057cfa365c4a81",
            "Vessel.Persistence/WorkspaceStore.cs|a1c0bc9961d3e38f15fd9b7b59072f141e41a0ba20319ee201057cfa365c4a81",
            // Not SQL: identity command names "reset-after-restore" and "revoke-sessions".
            "Vessel.Identity/IdentityAdministration.cs|de972956ff71edac782269c6c600a1fd82dc90c14f4363d70e3287828933c5db",
            "Vessel.Identity/IdentityAdministration.cs|d0a010615ff67a2fdcaabb9a9ea71fda1e68f0cb866d887bd960e2daac2ce4e2",
            "Vessel.Identity/IdentityCommands.cs|de972956ff71edac782269c6c600a1fd82dc90c14f4363d70e3287828933c5db",
            "Vessel.Identity/IdentityCommands.cs|d0a010615ff67a2fdcaabb9a9ea71fda1e68f0cb866d887bd960e2daac2ce4e2",
            // Not SQL: reset-after-restore completion message.
            "Vessel.Identity/IdentityAdministration.cs|05bcbd54e5313ea9342913bae8c9ca458edd7ac36a0dfdcf0493ff7afc0aab60",
            // Not SQL: identity command usage text ("--user", "--address" … and words such as "revoke").
            "Vessel.Identity/IdentityCommands.cs|077a1423296023055128d9ed1c94d99ecfbd28849c50709809d3ca8d2d47489b",
            // Not SQL: the "import-conflicts" route segment of the actual-only boundary (Phase 2, item 14c).
            "Vessel.Api/TradingModeBoundary.cs|d9ea752b24a4ee2088e6578a7606d8a3cbb73206c981e9343f33b244ea4ea174",
            // Not SQL: the "refresh" source of a fill coverage record (Phase 2, item 6).
            "Vessel.Domain/Monitoring/HistoryCoverage.cs|d6cc0a088c07683c65cd266860cab8d94b3a1937b17420d9da30ca299c09fb77",
        ];
        // One entry per distinct path and statement text: repeating an already reviewed literal needs no new review.
        Assert.Equal(reviewed.Length, reviewed.Distinct().Count());
        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Sources().Where(p => !p.Contains("/Migrations/", StringComparison.Ordinal)))
        foreach (Match literal in OwnerlessSqlClassifier.SourceLiteral.Matches(File.ReadAllText(path)))
        {
            if (!OwnerlessSqlClassifier.LiteralRequiresReview(literal.Value)) continue;
            var sql = literal.Value.TrimStart('$', '@', '"').TrimEnd('"').Trim();
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(sql, @"\s+", " "))));
            actual.Add(Path.GetRelativePath(SourceRoot(), path).Replace('\\', '/') + "|" + hash);
        }
        Assert.True(actual.SetEquals(reviewed),
            "Unreviewed: " + string.Join(", ", actual.Except(reviewed)) + "; stale: " + string.Join(", ", reviewed.Except(actual)));
    }

    private static IEnumerable<string> Sources() => Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains("/obj/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal));

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vessel.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src");
    }
}
