# Phase 2 test-preservation source audit, November 2, 2026

## Verdict

No blocker found in test preservation, test-project placement, service gates or the shared frontend timeout change. This is source verification, not a test-run pass.

Current prepared tip: `f3fdfd8ca1c59e0617c92465683018e33da3130a`.
Backend inventory initially frozen at `c91d1a11da944e4b465f0cc5910ee6648d99ba90`. Every updated branch changes only `frontend/src/test/setup.ts`; all 25 backend test trees are byte-identical to the audited trees.
Original Phase 2 tip: `07a18823005b2eb26fcc2e69d6f21a01311b18b0`.
Main baseline: `d3d3d1f72b5d01385ca31214b4223bd26eaed6ef`.

## Method and data preservation

The original Phase 2 tip contains 2,212 attributed test methods. All 2,212 map one-to-one to the prepared tip, including duplicate method names in different classes. All original method-level attributes, including 2,230 InlineData attributes and 11 MemberData uses, survive unchanged. Test methods are not discovered-case counts.

At the tip, 2,196 original test bodies are token-identical, preserving literal token contents. Fourteen already match current main's intentional changes. The other two differ only by references to the extracted StopConfirmationTestSupport and ConditionKindsTestSupport. No assertion was removed as a conflict/compile fix.

All 25 layers retain their own original test methods and attributes, with exactly 40 additional methods from integrating main. There are no duplicate class/method keys, tests in TestKit, tests in Vessel.Tests, missing linked sources, project compile exclusions or cross-test-project static references to classes containing tests. All 25 solution files retain main's five-suite plus TestKit wiring.

One main-baseline method is intentionally replaced by the existing Phase 2 work rather than lost during reconciliation: RisexAdapterTests.Missing_execution_money_is_not_zeroed_even_on_an_opening_trade. Its six cases become the three-case A_missing_fee_is_not_zeroed_even_on_an_opening_trade at backend/tests/Vessel.Venues.Tests/RisexAdapterTests.cs:162 and the three-case A_trade_without_reported_PnL_is_set_aside_with_its_order_and_the_read_still_applies at line 182. Fee rejection remains, and missing-PnL expectations now assert omission markers under the Phase 2 contract.

## Placement and fixtures

Tip method counts: Unit 381; UseCases 407; Venues 352; Api 126; Integration 986. Total 2,252.

Unit references only Domain and Application at backend/tests/Vessel.Unit.Tests/Vessel.Unit.Tests.csproj:10-11. Its new CsvTestRows source link at line 35 does not reference TestKit's hosting assembly or introduce discoverable tests. Pure rules and calculators stay in Unit; fake-backed services in UseCases; venue adapters in Venues; persisted/API-with-database work in Integration.

Shared setup remains non-test support. CsvTestRows, StopConfirmationTestSupport and ConditionKindsTestSupport retain original helper bodies, apart from accessibility needed across assemblies. TestKit grants access to UseCases, Venues, Api and Integration at backend/tests/Vessel.TestKit/Vessel.TestKit.csproj:13-16. It contains no attributed test methods.

The four-case Kinds property stays public on the actual test class at backend/tests/Vessel.UseCases.Tests/MonitorDiscrepancyConditionKindsTests.cs:20; MemberData still resolves there rather than to an imported helper. All source data providers, fixture fields/properties and helper methods were compared. Initial member-audit candidates were manually resolved as existing main changes or support-constructor renames: OverviewSummaryTests, EvidenceTests, EvidenceApiTests, ExactNumberCharacterizationTests.PlanText, StreamRevalidationTests.Both_event_caps_preserve_RetryAfter_and_code_at_the_client, and IdentityApiTests.Config_does_not_reveal_origins_and_only_reports_the_query_hint_and_gate. They are not remaining findings.

Every original custom Fact/Theory gate and collection definition has a token-identical counterpart after accessibility changes. Duplicated collection definitions are intentional main-era copies into separate assemblies, not duplicated tests. The nested S3 copy-command gate still requires both PostgreSQL and S3.

All five recorded/evidence fixture files have identical content hashes before and after relocation, and the sixth linked graphite.jpg has the same Git blob in all three baselines. Fixture-copy rules remain in TestKit at lines 19-20.

## Unicode

JournalSearchTests' 14 original test methods split into five Unit methods and nine PostgreSQL Integration methods. All 14 bodies and attributes survive. Extraction used UTF-16 code-unit spans, not Python codepoint indexing. Literal tests retain full-width BTC, zero-width characters, the literal unpaired-surrogate escape, replacement character, accented Greek/Latin, astral Deseret characters and ellipses. See backend/tests/Vessel.Unit.Tests/JournalSearchTests.cs:15-22, 29 and 47-58. The nine persisted methods remain PostgresFact-gated in JournalSearchPostgresTests.cs.

## Frontend shared harness delta

frontend/src/test/setup.ts:2 imports configure and line 7 sets asyncUtilTimeout to 5,000 ms. The existing cleanup, storage clearing, environment/global unstubbing and chart mock are unchanged. frontend/vite.config.ts:31 loads the shared setup, and line 35 retains the separate 15,000 ms per-test limit. No assertion, skip/gate, production path or service configuration changes in this delta. The source change is reasonable timing headroom, not evidence that the parent's failing tests now pass.

## Limits

No repository edits, pushes, merges, agents, live services, builds or tests were run by this reviewer. Only read-only Git/source inspection, the existing Roslyn syntax utility and temporary audit artifacts were used. Runtime discovery, compilation and service-enabled execution remain the parent's checks. Parent reports all five backend projects compile and is rerunning frontend failures with controlled concurrency.

## Per-layer inventory

| PR | Original methods | Prepared methods | Lost methods | Changed original test attributes | Current head |
|---|---:|---:|---:|---:|---|
| #35 | 1986 | 2026 | 0 | 0 | `67c0ffc8e898641665b66ec755062f4bc47e1f4c` |
| #37 | 1991 | 2031 | 0 | 0 | `aa7d8a0dc2d71c3b579b7307f732ca70ea2408b7` |
| #38 | 1996 | 2036 | 0 | 0 | `0601e7a8cf3b36e79a14f77debde334f78ff48a4` |
| #39 | 2005 | 2045 | 0 | 0 | `ede372713b88ee656b62c985952941749a7230f6` |
| #40 | 2015 | 2055 | 0 | 0 | `61e89f08541cfce666ba2b83d289357e8c470e91` |
| #41 | 2021 | 2061 | 0 | 0 | `275f2398a82a3ac20a26062533080ed4b4da0e34` |
| #42 | 2026 | 2066 | 0 | 0 | `6d2938b88d084865d5dcbc761665a06466ef3d26` |
| #44 | 2041 | 2081 | 0 | 0 | `aeb41c72fe422f3eb1db2ae933cc4eccaba85a89` |
| #45 | 2041 | 2081 | 0 | 0 | `462a1b39f0d6041a1431d194cd376b930950b19b` |
| #46 | 2042 | 2082 | 0 | 0 | `11aa16241df42cf64a4dd7dcc7d42d510c2a0643` |
| #47 | 2057 | 2097 | 0 | 0 | `2fb7e6f47fcb57d7b1bab0c1c9f560016378a80d` |
| #49 | 2068 | 2108 | 0 | 0 | `5085b5116a661ec5f784345b70e8a10c9434f2fa` |
| #50 | 2071 | 2111 | 0 | 0 | `b6991cc6d3914179d13d914e5c347528709a51e0` |
| #51 | 2076 | 2116 | 0 | 0 | `1e3fad1aaa3e648d619689ecf4baf2159c8be0d8` |
| #52 | 2085 | 2125 | 0 | 0 | `25885174081ca81e69b7201fd595fce0f8a7dd55` |
| #54 | 2103 | 2143 | 0 | 0 | `18718794c64713f05a030ba9579fbe7725fb76d8` |
| #55 | 2117 | 2157 | 0 | 0 | `c008fc30bdb0eeb92bb0e3e54246bbc4cd8d04e4` |
| #56 | 2126 | 2166 | 0 | 0 | `8136c4f8418191faaf880fe8252ea8471b441c11` |
| #57 | 2137 | 2177 | 0 | 0 | `ef9ab97b7b2399535a5b28b776dc288058aac666` |
| #58 | 2147 | 2187 | 0 | 0 | `30ce077b0ab3b342157ac5162843fa54cbbaadbd` |
| #59 | 2158 | 2198 | 0 | 0 | `de792d5a4fb4e407b61fd11c3b50272a8f391348` |
| #60 | 2165 | 2205 | 0 | 0 | `989c23a5d7f24e60b8815b9613c96e8755d736fa` |
| #63 | 2180 | 2220 | 0 | 0 | `da7881048d4bec31e814d6c08dd8b1d72f6e34af` |
| #64 | 2205 | 2245 | 0 | 0 | `05be514943b6d4b160a9bf56310edfa6eabffa07` |
| #66 | 2212 | 2252 | 0 | 0 | `f3fdfd8ca1c59e0617c92465683018e33da3130a` |

## Inventory artifacts

- summary.txt: baseline counts and initial body candidates.
- strict-summary.txt and strict-final-mapping.json: one-to-one method preservation, with same-class preference and no reuse of target methods.
- layers-results.json: exact initial prepared heads, per-layer original method counts, body candidates and attributes.
- structural-results.json: per-layer references/links/placement checks and cross-project reference audit.
- updated-heads.json: updated heads, delta paths and backend tree identity.
- fixtures-summary.txt and fixtures-results.json: raw member audit candidates; the six recurring candidates are resolved above, not blockers.
