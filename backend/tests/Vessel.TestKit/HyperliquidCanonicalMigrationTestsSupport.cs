
namespace Vessel.Tests;

public partial class HyperliquidCanonicalMigrationTestsSupport
{
    // Hand-off to the migration owner: run after the nullable account_positions.VenueContractId column exists.
    // Proposed migration name: HyperliquidCanonicalInstruments. No SourceId/Address dependency.
    // Plan and play_plan_revisions.Plan contain entries/sizing/notes, not instrument references.
    // Do not string-replace these documents: notes, evidence and manual symbols must remain unchanged.
    // Drawings keep their original venue namespace even after a Play switches account or to manual entry.
    internal const string UpSql = """
        DO $canonical$
        DECLARE
            native text;
            canonical text;
            old_key text;
            new_key text;
            facts_table text;
            conflict boolean;
        BEGIN
            FOR native, canonical IN
                SELECT * FROM (VALUES
                    ('kPEPE', '1000PEPE'), ('kSHIB', '1000SHIB'), ('kBONK', '1000BONK'),
                    ('kLUNC', '1000LUNC'), ('kFLOKI', '1000FLOKI'), ('kDOGS', '1000DOGS'),
                    ('kNEIRO', '1000NEIRO')
                ) AS aliases(native, canonical)
            LOOP
                old_key := 'hyperliquid:' || native;
                new_key := 'hyperliquid:' || canonical;
                IF EXISTS (
                    SELECT 1 FROM plays
                    WHERE "Drawings" ? old_key AND "Drawings" ? new_key
                ) THEN
                    RAISE EXCEPTION 'Hyperliquid canonical drawing key collision';
                END IF;

                FOREACH facts_table IN ARRAY ARRAY['imported_fills', 'imported_orders', 'account_positions']
                LOOP
                    EXECUTE format(
                        'SELECT EXISTS (SELECT 1 FROM %I f JOIN accounts a
                         ON a."OwnerId" = f."OwnerId" AND a."Id" = f."AccountId"
                         WHERE a."VenueId" = ''hyperliquid'' AND f."ContractId" = $1
                           AND f."VenueContractId" IS NOT NULL AND f."VenueContractId" <> $1)',
                        facts_table) INTO conflict USING native;
                    IF conflict THEN
                        RAISE EXCEPTION 'Hyperliquid native contract identity conflict';
                    END IF;
                    EXECUTE format(
                        'UPDATE %I f SET "ContractId" = $2, "VenueContractId" = $1
                         FROM accounts a WHERE a."OwnerId" = f."OwnerId" AND a."Id" = f."AccountId"
                           AND a."VenueId" = ''hyperliquid'' AND f."ContractId" = $1',
                        facts_table) USING native, canonical;
                END LOOP;

                UPDATE plays SET "ContractId" = canonical
                WHERE "VenueId" = 'hyperliquid' AND "InstrumentSource" = 'Venue' AND "ContractId" = native;

                UPDATE plays
                SET "Drawings" = ("Drawings" - old_key) || jsonb_build_object(new_key, "Drawings" -> old_key)
                WHERE "Drawings" ? old_key;
            END LOOP;
        END $canonical$;
        """;
}
