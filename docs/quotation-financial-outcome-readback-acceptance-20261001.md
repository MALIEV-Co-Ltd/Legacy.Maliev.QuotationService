# Financial outcome readback — producer HTTP acceptance

Bounded acceptance-only follow-up under open Quotation #21/#71. No runtime, old-test, DTO, schema, consumer, auth configuration or provider changes. Source behavior is already implemented; new tests exercise its actual signed HTTP boundary. No product RED was observed in the initial focused run.

## Ownership and revisions

- Owned worktree/branch: `B:/maliev-legacy/.worktrees/quotation-financial-readback-20261001`, `codex/quotation-financial-readback-20261001`.
- Initial clean protected base `29b257070db1c6125c017af8034d4e75fcf31025` passed its 452-test baseline.
- Before final gates, root merged protected PR85 at `5c3217dd1d3be5d98709115cb2ab0a51115ac336`. After verifying no tracked edits, `git merge --ff-only 5c3217dd1d3be5d98709115cb2ab0a51115ac336` safely fast-forwarded this branch. Owned untracked tests/private ignored dependencies/evidence were preserved; no reset/discard. That adds the accepted 13 aggregate cases; final expected suite is **479 = 465 existing + 14 new**. No redundant final 466-test run was started.
- Independently cloned local dependencies and private rebuilt outputs under ignored `.dependencies`: Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`. No shared output directories.
- Original source read-only committed mirror cutoff `bed10c7d15e0698e0b75f1329d0f312937f5d77f`. No original configuration/data/execution or source writes.
- Only owned writes: `Legacy.Maliev.QuotationService.Tests/Controllers/QuotationFinancialOutcomeReadbackHttpTests.cs` and this document.

## Source traceability — individual dispositions retained

| Full source SHA | Behavior and disposition |
| --- | --- |
| `3b8bfe3658a1205b4811d063d44bec8cfa88d895` | Source reconciliation keys on draft creation and immutable first-acceptance outcome; represented by the new signed HTTP chain. No blanket completion of other owners. |
| `7ebbc97b0b93c6f71b8b5079ebd404e42c259338` | Employee-only bounded UTC aggregate outcome readback; primary accepted boundary. |
| `c6ac93af03a0dafc506d9570aca96e4aed3b1643` | Attributed/unattributed counts, including partial source keys; covered. GA/provider and other Web behavior remain outside scope. |
| `e790933f67e7dd748e34ae805a8b85f268ce1aa1` | Earlier atomic first acceptance/analytics source intent; existing provider-neutral outcome replaces provider delivery under approved privacy architecture. No analytics restoration or owner closure. |
| `6c1a4921b3524dd575cab5b1f4aa5774ad9b8997` | Employee acceptance belongs to existing decision/identity producer and consumer gates; no new role or workload compatibility policy. |
| `c821605b7ecde5f79d01966888b116defb10650d` | Invoice linking remains Quotation #68/Accounting #23 consumer integration; not repaired by readback tests. |
| `7ebe7e4bf83a435ecb18afc29cf263424ed2bb74` | Lead-request attribution remains #73 and independently accepted request slices; not duplicated or closed. |

Primary original paths:
`Maliev.QuotationService.Api/Controllers/QuotationsController.cs`,
`Maliev.QuotationService.Api/Models/QualifiedOutcomeReadback.cs`,
`Maliev.QuotationService.Data/Database/QuotationContext/Quotation.cs`,
`Maliev.QuotationService.Data/Database/QuotationContext/QuotationOutcomeOutbox.cs`.

Source readback joins immutable first-outcome rows to **current** quotation rows, sums current nullable quoted amounts by current currency, uses `CreatedDate` and acceptance time independently, and excludes the upper UTC boundary. Both attribution keys are required for attributed counts. A deleted quotation disappears from the joined aggregate even if its immutable outcome remains. Current edits changing reported amount/currency and deletion omission are source-equivalent observations, not invented defects or financial snapshot promises.

## Producer and consumer contracts

Producer: `QuotationsController.GetOutcomeReadbackAsync`, `GET /quotations/outcomes/readback?fromUtc=...&toUtc=...`; ordinary normal JWT + Employee role + live `legacy.quotations.read`. Source-equivalent non-UTC, reversed/equal or greater-than-31-day windows are 400; exactly 31 days is admitted. Response is current PascalCase aggregate-only `QuotationOutcomeReadback` with unavailable technical-conversion/qualified-customer/revenue markers. Accepted quote amounts are **not settled revenue**.

`QuotationRepositories.GetOutcomeReadbackAsync` supplies PostgreSQL projections; ordinary `UpdateQuotationAsync` deliberately does not remap source IDs. `ApplyDecisionAsync` freezes first acceptance provenance/event/time. Normal `POST /quotations` and `PUT /quotations/{id}` / `PUT /quotations/{id}/decision` are used in the tests, without fake repositories or direct fabricated acceptance outcomes. There are no order links in these records, so the actual decision workflow has zero downstream order transitions; no Order provider is replaced.

Intranet committed `40275ec6c5b3547ab6d5363fcb8cc03b82d3f473`:
`Legacy.Maliev.Intranet.Bff/Operations/AggregateOutcomeProxy.cs` forwards the current employee access token to the exact producer route.
`OutcomeReadbackEndpointMapper.cs` requires the existing employee navigation grant, reloads the session token, bounds the response, validates aggregate payload and returns its own no-store receipt. No raw PII is forwarded. `Quotations/QuotationCreationGateway.cs` carries source IDs, and `QuotationCreateEndpointMapper.cs` resolves the source journey server-side.

Accounting committed `ae0826156b06c34476e95de8c53dfccfcf5a5972`:
`Legacy.Maliev.AccountingService.Data/InvoiceCreationDownstreamClients.cs`, `InvoiceCreationSourceClient.GetAsync`, consumes quotation detail/line snapshots, not this readback. Existing invoice completion full-PUT/decision mismatch remains its separately tracked #68/#23 chain; these tests do not prove that consumer repaired or activated.

## Actual fixture boundary and independent oracles

Actual Production `Program`, generated synthetic RSA-2048/RS256 bearer tokens through the ordinary validator, disposable PostgreSQL 18 with both DbContexts migrated, Redis 7 and registered pinned actual `IamServiceClient` with controlled remote HTTP transport. No `IIamServiceClient` substitute, auth-handler replacement, signed-permission fallback, provider traffic or production credentials. JWT uses ordinary real-clock not-before/expiry; only application business `TimeProvider` is fixture-controlled for deterministic persisted/accepted UTC windows. Hosted behavior is not disabled; expiration is safely in 2035.

Every actual IAM call asserts permission endpoint/header/principal, expected granular permission, `bypassCache=true`, and current default `resourcePath="global"`. The positive aggregate chain and employee-denied case explicitly assert read-check count. This is live-client admission through controlled transport, **not resource-specific production authorization, live IAM or full Aspire acceptance**. Resource flag/qualification bridge remain unchanged/default-off.

The actual IAM-client registration is fixture-only. Current production `Program` does not register `IIamServiceClient`; its forced-live aggregate admission does not become operational merely because this controlled-client test passes. Production authoritative-permission integration/readiness remains a separate gate. The qualification-only bridge is not expanded, and no grants, credential provisioning or fallback are introduced.

Fourteen cases:

- HTTP draft creation → expected-version ordinary update cannot overwrite provenance → first employee decision/replay → readback; literal `107.54 - 3.02 = 104.52`, single durable event/key/acceptance time/source IDs.
- Half-open boundaries with independently separated created/accepted dates, source-attributed and partial-key rows, two days and sorted currencies; literal amounts `10`, `104.52`, `25`, `50` and exact counts asserted.
- Accepted financial edit changes readback to current currency 840/value `250 - 10 = 240`; subsequent ordinary deletion omits the joined root while retaining the original immutable event/provenance.
- Null computed quoted amount still counts acceptance, without inventing a total-value fallback.
- Four normal caller/admission denials: anonymous 401; customer/service with read grants still 403; employee with read grant but live transport denial 403. Graph counts unchanged.
- Six UTC window controls: equal/reversed/over31/unspecified/malformed 400; exactly31 admitted.

All successful readbacks independently assert PascalCase, unavailable markers and absence of identifiers, contact fields, source keys, event metadata and private comment content. No quote IDs/PII leak in aggregate response.

## Executed validation chronology

For .NET commands: `UseLocalMalievDependencies=true`, absolute `MalievWorkspaceRoot=<owned-worktree>/.dependencies`; outputs remain owned.

1. Untouched `dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=$env:MalievWorkspaceRoot -nodeReuse:false`: **0 warnings / 0 errors**.
2. Untouched full `dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build --no-restore --logger trx --results-directory TestResults/readback-baseline`: **452 passed / 0 failed / 0 skipped**, duration 3m33s; TRX `natth_MALIEV-31USFIV_2026-10-01_15_50_34_net10.0.trx`.
3. Initial new-test compilation had one ambiguous xUnit `Assert.Contains` collection-expression overload, zero warnings; no test executed. Corrected only the new test to an explicit array. This is not a product RED.
4. New Release **0 warnings / 0 errors**; focus `FullyQualifiedName~QuotationFinancialOutcomeReadbackHttpTests` **14 passed / 0 failed / 0 skipped**. TRX `TestResults/readback-focus/natth_MALIEV-31USFIV_2026-10-01_15_59_13_net10.0.trx`. No runtime repair was authorized or performed.
5. Safe fast-forward to root's protected PR85 merge before final gates. Fresh Release **0 warnings / 0 errors**, final focus **14 passed / 0 failed / 0 skipped**, TRX `TestResults/readback-final-focus/natth_MALIEV-31USFIV_2026-10-01_16_03_08_net10.0.trx`. Earlier 452 baseline/14 focus remain clearly historical, not substituted for final479 acceptance.
6. Final unexcluded suite command: `dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build --no-restore -p:GITHUB_ACTIONS=false --collect 'XPlat Code Coverage' --logger trx --results-directory TestResults/readback-final-coverage`: **479 passed / 0 failed / 0 skipped**, duration 2m47s. TRX `TestResults/readback-final-coverage/natth_MALIEV-31USFIV_2026-10-01_16_03_15_net10.0.trx`.
7. Exact report `TestResults/readback-final-coverage/b587516d-c837-465a-9574-f3538cbcc4ce/coverage.cobertura.xml`, SHA256 `8946617C78EBCD35F07B8BBD8C64B1A9B57733D0F5B6D25A1BA13FBCEF4F93BB`. Unchanged executed `python scripts/check_owned_coverage.py <that-report> --minimum 80`: **5193/5477 owned handwritten lines (94.81%)**, API **432/469 (92.11%)**, PASS. Raw Cobertura including generated/external lines: **6429/10315**, reported line rate **0.6232**. These tests add boundary evidence, not a claimed line-coverage increase. Reports/denominators remain distinct.
8. Sequential final gates terminal zero: whole `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore`; `python -m unittest discover -s scripts/tests -p 'test_*.py'` **5 passed** (deliberate rejection-case stderr/sample reports expected); `actionlint`; `git diff --check`; `dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive --no-restore` **no vulnerable packages across all six projects against current NuGet sources**; redacted history `gitleaks git . --redact --no-banner` **77 commits/no findings**; scoped two-owned-file stdin scan **31,814 bytes/no findings**. `PYTHONDONTWRITEBYTECODE=1` prevented Python cache artifacts.
9. Exact CI `73dd7304ffe85ec504389fd7664cc39070b9f148` committed `scripts/JwtSigningResourceScanner.ps1` function imported read-only through `git show`; `Test-JwtSigningResourceMaterial -RepositoryPath <owned-worktree> -TrackedFiles @(git ls-files)`: PASS. No scanner/source edits. Final document readback and scoped redacted scan after chronology update retained. All owned build/test/static handles terminal at freeze.

Root independently reviewed the complete test and producer/consumer boundaries, then ran a fresh Release build (0 warnings/errors), focused 14/14 and unfiltered 479/479 tests (zero skips). Root report `TestResults/root-financial-readback-full/2a9071e1-d64a-4c84-b331-b654b13aa83f/coverage.cobertura.xml` has SHA256 `E565B0525CA86ABB0F49AEC54EDDBE1122389363490CC391F8F65EEA3496198A`: unchanged checker reports 5195/5477 owned handwritten lines (94.85%), API 434/469 (92.54%). This independent report is distinct from the agent's report. Root whole-solution format, five checker tests, actionlint, six package audits, whitespace and scoped/staged/history secret checks passed. Ignored private dependencies and validation reports are retained, never staged. Bounded issue #86 owns this evidence; parents and whole-source owners remain open.

The two-file test/documentation candidate is ready for protected integration. No runtime repair was needed or performed. Current production authoritative-permission, real Intranet/Aspire and external-provider acceptance remain explicitly unclaimed.

## Remaining boundaries

No distributed transaction/current-identity-at-commit claim, financial snapshot invention, actor/key namespace changes, positive authorization cache, schema changes, source ledger edits, provider delivery, activation, deployment, persistent DDL/data, commits, pushes or GitHub writes. Intranet end-to-end receipt mapping and genuine current live IAM/Aspire acceptance remain separate integration gates. Root owns protected integration and individual issue/source-owner decisions; no blanket #21/#71 or seven-SHA closure applies.
