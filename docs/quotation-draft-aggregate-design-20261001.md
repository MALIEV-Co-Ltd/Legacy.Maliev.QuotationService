# Draft financial aggregate HTTP proof — bounded zero-ID repair

Tracking: [Quotation #84](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/issues/84), child of open #71. Root reviewed the frozen genuine RED and approved only the source zero-ID guard in the order-link create action. This document retains the test/design chronology; final acceptance is recorded below, not inferred from the earlier RED gate. No whole-source acceptance claim applies.

## Frozen revisions and ownership

- Owned branch/worktree: `codex/quotation-draft-aggregate-20261001`, `B:/maliev-legacy/.worktrees/quotation-draft-aggregate-20261001`.
- Baseline: `c5a6a0721fd737b2e48565c48dc58bf4a2c35118`; root independently verified its tree equals merged main `29b257070db1c6125c017af8034d4e75fcf31025`. Retained base without resetting work.
- Independent ignored local dependency clones/output: Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7` under `.dependencies`.
- Source mirror cutoff: `bed10c7d15e0698e0b75f1329d0f312937f5d77f`; source reads only, never original runtime/database/configuration.
- Owned writes: new test file, this document, and only `OrdersController.CreateQuotationOrderLinkAsync`. Existing tests, other runtime methods, DTOs, consumers, migrations and permission configuration are untouched.

## Exact source behavior and narrow discrepancy

Source `5fac706a7983a6d359b39acbd670e6800afe020e`, `Maliev.QuotationService.Api/Controllers/OrdersController.cs`, `CreateQuotationOrderLinkAsync` explicitly returns `BadRequest("Quotation id and order id are required")` when **either identifier equals zero**, before parent lookup or any insert. This guard remains in the committed cutoff. It does not establish a negative-ID policy. Source root, line and link controllers each save separately; they do not provide cross-step aggregate atomicity.

Before repair, `Legacy.Maliev.QuotationService.Api/Controllers/OrdersController.cs` called the nullable Redis-idempotent create helper immediately. `QuotationRepositories.CreateOrderLinkAsync` checks parent existence but accepts order zero. Historical actual Production HTTP RED proved:

| Inputs | Required source result | Observed current result / fresh graph |
| --- | --- | --- |
| Existing quotation / order zero | 400, no link | 201, one durable link with order zero |
| Quotation zero / positive order | 400, no link | 404, no link |
| Both zero | 400, no link | 404, no link |

The first RED initially asserted persisted count before status; final assertion includes both count and status in a tuple, so the diagnostic retains the durable defect even when status differs.

## Wire, permissions and consumers

| Step | Producer wire | Permission / admission | Consumer |
| --- | --- | --- | --- |
| Root | `POST /quotations`, `UpsertQuotationRequest`; 201 `QuotationResponse`, named `GetQuotation` Location | `legacy.quotations.create`, live critical | Intranet root creation step |
| Line | `POST /quotations/orderitems`, five-field `UpsertQuotationOrderItemRequest`; 201 `QuotationOrderItemResponse`, named `GetOrderItem` Location | `legacy.quotation-lines.write`, live | Intranet priced line step; Accounting line snapshot |
| Link | Bodyless `POST /quotations/{quotationId}/orders/{orderId}`; 201 `QuotationOrderLinkResponse`, named `GetQuotationOrder` Location | `legacy.quotation-orders.write`, live | Intranet external order link step |

Intranet committed `40275ec6c5b3547ab6d5363fcb8cc03b82d3f473`:
`Legacy.Maliev.Intranet.Server/Quotations/QuotationCreationWorkflow.cs` saves resumable state after each successful step and emits only distinct positive order IDs. `Legacy.Maliev.Intranet.Bff/Quotations/QuotationCreationGateway.cs` sends individual idempotency keys and reads the returned `Id`; it calls `EnsureSuccessStatusCode`, not an exact-201 check. No cross-service aggregate transaction or compensation is claimed. Its line payload also carries the legacy subtotal, ignored by the producer's current five-field request; PostgreSQL computes authoritative subtotal.

Accounting committed `ae0826156b06c34476e95de8c53dfccfcf5a5972`:
`Legacy.Maliev.AccountingService.Data/InvoiceCreationDownstreamClients.cs`, `InvoiceCreationSourceClient.GetAsync`, reads quotation detail plus its line list for invoice snapshots. There are no consumer edits in this slice.

## Real test boundary and passing controls

`Legacy.Maliev.QuotationService.Tests/Controllers/QuotationDraftAggregateHttpTests.cs` runs actual Production `Program`, ordinary RSA-2048/RS256 validation, disposable PostgreSQL 18 (both databases migrated), real Redis 7, and the pinned actual `IamServiceClient` with a controlled remote HTTP transport. It does not replace IAM with a fake authority or replace authentication. Synthetic issuer, keys and credential stay fixture-only.

The transport asserts the exact POST permission endpoint, credential header, principal, allowed permission IDs, `bypassCache=true`, and actual default `resourcePath="global"`. This proves live permission admission with unchanged feature flags, **not** production resource-specific authorization or external IAM acceptance. No bridge activation or grants are introduced.

Passing controls: keyed/unkeyed root→line→link; follow all three Locations through authenticated HTTP; Pascal-case response/null omission; server-owned Accepted null despite supplied true; server timestamps; literal financial oracle `100.50` line subtotal and `104.52` quoted amount; fresh durable one-root/one-line/one-link graph; same-key sequential exact responses/Locations with no extra rows; six unauthenticated 401/live-denied 403 no-write cases; missing positive parent 404; actual PostgreSQL BEFORE INSERT trigger failure leaves the previously committed root intact and no line, with current 500 response. The trigger is installed and removed only in the disposable fixture database.

## Approved minimal runtime repair

Restored the exact zero-only guard in `OrdersController.CreateQuotationOrderLinkAsync` **before** the Redis helper, after normal middleware permission admission. Returns the exact source message with 400. No repository, key namespace, fingerprints, concurrency receipts, cache protocol, schema or DTO changes occurred. The rule is not `<=0`; no aggregate transaction or inferred negative policy was added. Valid 201/Locations, missing-parent 404, existing live checks and sequential replay behavior remain unchanged.

Approved validation sequence: Release build → all 13 new HTTP cases green → full existing 452 plus new cases/coverage → whole format/coverage-checker/actionlint/audits/security checks → root independent review. No existing tests were modified or weakened.

## Executed validation chronology

Environment for all commands: `UseLocalMalievDependencies=true`; absolute `MalievWorkspaceRoot` set to this worktree's `.dependencies`.

1. Untouched `dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=$env:MalievWorkspaceRoot -nodeReuse:false`: **0 warnings, 0 errors**.
2. Untouched full `dotnet test ... -c Release --no-build --no-restore --logger trx --results-directory TestResults/aggregate-baseline`: **452 passed, 0 failed, 0 skipped**. TRX `natth_MALIEV-31USFIV_2026-10-01_15_14_13_net10.0.trx`.
3. Initial new test compilation used an incorrect `Orders` DbSet name: five compilation errors, zero warnings. Corrected only the new test to actual `OrderLinks`; no runtime change. This was not a product RED or a valid test run.
4. New Release build: **0 warnings, 0 errors**. Focus `FullyQualifiedName~QuotationDraftAggregateHttpTests`, `TestResults/aggregate-red`: **10 passed, 3 genuine RED, 0 skipped**, TRX `natth_MALIEV-31USFIV_2026-10-01_15_20_53_net10.0.trx`.
5. Scoped `dotnet format Legacy.Maliev.QuotationService.slnx --no-restore --include Legacy.Maliev.QuotationService.Tests/Controllers/QuotationDraftAggregateHttpTests.cs`, then final Release build: **0 warnings, 0 errors**. Final focus **10 passed / 3 failed / 0 skipped**, TRX `TestResults/aggregate-red-final/natth_MALIEV-31USFIV_2026-10-01_15_22_18_net10.0.trx`.
6. Final full `dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build --no-restore --logger trx --results-directory TestResults/aggregate-full-red`: **462 passed / 3 failed / 0 skipped / 465 total**; duration 3m12s. TRX `TestResults/aggregate-full-red/natth_MALIEV-31USFIV_2026-10-01_15_22_21_net10.0.trx`. Parsed failure names are only the three new zero-link cases. All 452 old cases plus the ten new controls passed. This is deliberately RED, not mergeable runtime acceptance.
7. Final scoped `dotnet format ... --verify-no-changes --no-restore --include Legacy.Maliev.QuotationService.Tests/Controllers/QuotationDraftAggregateHttpTests.cs`, `git diff --check`, and redacted `gitleaks dir --no-banner --redact` on each owned file all exited zero; no leaks. No production/package/workflow change occurred, so no new deployment, actionlint or vulnerability claim is made. No processes remained active at handoff.
8. After root's runtime approval, the one-line zero-only guard was added. Release build **0 warnings, 0 errors**; focused HTTP suite **13 passed / 0 failed / 0 skipped**, TRX `TestResults/aggregate-green/natth_MALIEV-31USFIV_2026-10-01_15_30_44_net10.0.trx`.
9. Final full, unexcluded coverage command: `dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build --no-restore -p:GITHUB_ACTIONS=false --collect 'XPlat Code Coverage' --logger trx --results-directory TestResults/aggregate-final-coverage`. **465 passed / 0 failed / 0 skipped**, duration 4m54s; TRX `TestResults/aggregate-final-coverage/natth_MALIEV-31USFIV_2026-10-01_15_31_02_net10.0.trx`.
10. Exact report: `TestResults/aggregate-final-coverage/12e6e55a-b940-4591-b986-4362a9b5462f/coverage.cobertura.xml`, SHA256 `9D7675A2CDD1C870107F3723E2D0AB6E539F1697C05294745B374C341F39E9B7`. Unchanged checker `python scripts/check_owned_coverage.py <that-report> --minimum 80`: **5193/5477 owned handwritten lines (94.81%)**, API **432/469 (92.11%)**, PASS. Raw Cobertura including generated/external dependency lines: **6429/10315**, reported line rate **0.6232**. Do not substitute raw/handwritten denominators or different reports.
11. Sequential final static gates all terminal zero: whole `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore`; `python -m unittest discover -s scripts/tests -p 'test_*.py'` **5 passed** (intentional rejection-case stderr/sample coverage outputs are expected); `actionlint`; `git diff --check`; `dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive --no-restore` **no vulnerable packages in all six projects against current NuGet sources**; redacted `gitleaks git . --redact --no-banner` **76 commits / no findings**; three-owned-file stdin gitleaks **28,663 bytes / no findings**. `PYTHONDONTWRITEBYTECODE=1` prevented generated Python cache artifacts.
12. Exact CI `73dd7304ffe85ec504389fd7664cc39070b9f148` signing-resource function imported from committed `scripts/JwtSigningResourceScanner.ps1` using read-only `git show`, then `Test-JwtSigningResourceMaterial -RepositoryPath <owned-worktree> -TrackedFiles @(git ls-files)`: PASS, no signing resource findings. No source checkout or scanner edits. Final document-only readback/secret scan after this chronology update is retained; no owned handles remain active at release.

The bounded local candidate is green and frozen for root independent acceptance/integration. Protected PR/main CI and the unchanged separate joined Auth/Quotation job are root-owned integration gates, not claimed newly executed here. No commits/pushes or activation occurred.

## Explicit exclusions and retained traceability

This is source-cohort proof for `5fac706a7983a6d359b39acbd670e6800afe020e`, not closure of that entire commit or #71. Separate cohorts remain outside scope: analytics/outbox `e790933f67e7dd748e34ae805a8b85f268ce1aa1`; employee acceptance `6c1a4921b3524dd575cab5b1f4aa5774ad9b8997`; invoice linking `c821605b7ecde5f79d01966888b116defb10650d`; outcome reconciliation `3b8bfe3658a1205b4811d063d44bec8cfa88d895`; readback `7ebbc97b0b93c6f71b8b5079ebd404e42c259338`; marketing measurement `c6ac93af03a0dafc506d9570aca96e4aed3b1643`; journey attribution `7ebe7e4bf83a435ecb18afc29cf263424ed2bb74`. The authoritative local MigrationTracking ledger was inspected read-only at `7a1b95c2d6bfdd2bec8384152d58121a61886220`.

Concurrent same-key creation, changed-payload/actor key binding, cache loss and ambiguous commits are not proven by sequential replay. Existing root/line/link Redis replay is unbound and non-atomic; do not invent a durable receipt claim. Root-to-child failure is intentionally separate commits, not distributed rollback. Ordinary auth/bridge/default flags remain unchanged. No provider traffic, production data, persistent DDL, deployment, commits, pushes or GitHub writes occurred.
