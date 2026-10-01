# Quotation82 bounded draft read/list parity

Tracking: [bounded child82](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/issues/82); parent71 remains open. Historical test/design chronology below is retained; approved implementation status supersedes its earlier no-runtime gate.

## Ownership and observed baseline

Clean canonical HEAD/origin/live main `6747d8f89e51d68e17530ce85b8493ef57be32b0` was independently observed; no open PR. Exclusive new branch `codex/quotation-draft-read-parity-20261001`, worktree `B:/maliev-legacy/.worktrees/quotation-draft-read-parity-20261001`. Prior worktrees remain untouched. Only this new doc, `Legacy.Maliev.QuotationService.Tests/Controllers/QuotationDraftReadParityHttpTests.cs` and the two approved read methods in `QuotationRepositories.cs` are modified; all 435 old tests remain unchanged.

Exact CI dependency clones are ignored/private/owned: Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`. No authority bridge activation or changes to ordinary production DI. Actual Production Program/RS256 validation, real PostgreSQL18 migrations in two disposable databases, real Redis, and the actual pinned IamServiceClient with a controlled named HTTP transport are exercised. This is not live external IAM or production-derived Aspire/data acceptance. Generated synthetic RSA keys/fixture-only rows and IAM credential never leave the fixture boundary.

Untouched baseline Release build: zero warnings/errors. Full original suite: 435 passed, zero failed/skipped; `TestResults/draft-read-baseline/natth_MALIEV-31USFIV_2026-10-01_14_38_28_net10.0.trx` (2m53s).

## Source/target/consumer contract

Local authoritative ledger read-only snapshot: MigrationTracking `7a1b95c2d6bfdd2bec8384152d58121a61886220`, `migration/source-commit-ledger.json`. Committed original mirror cutoff `bed10c7d15e0698e0b75f1329d0f312937f5d77f`; no original fetch/edit.

The source `Maliev.QuotationService.Api/Controllers/QuotationsController.cs` at `5fac706a7983a6d359b39acbd670e6800afe020e` and cutoff has unchanged list/detail behavior: lowercased Comment.Contains for nonnumeric input; numeric input exclusively Id/CustomerId equality; ordinary detail direct database FindAsync. Intervening controller diff does not replace those predicates/reads.

| Boundary | Approved current wire/security | Executed difference |
| --- | --- | --- |
| GET `/quotations`, `/quotations/customers/{customerId}` | customer-quotations.read; unscoped list additionally administrative quotations.read. Search/sort/index/size; PascalCase Items/PageIndex/TotalPages/TotalRecords/HasNextPage/HasPreviousPage, no PageSize. Existing trim and bounds retained. | Target unescaped ILIKE admits unrelated rows for `%`/`_`; numeric ID also matches numeric text in Comment, unlike exclusive source branch. |
| GET `/quotations/{quotationId}` | customer-quotations.read resource `/quotations/{quotationId}`, administrative quotations.read if no customerId. Nullable omitted fields, unchanged 19-field QuotationResponse. Customer mismatch404. | Target ordinary administrative detail trusts a late Redis value instead of current PostgreSQL financial state. Customer-specific rich detail is already directly projected. |
| PUT `/quotations/{quotationId}` | live/critical quotations.update, resource scope, optional X-Expected-Modified-Date;204/404/409 unchanged. | Control proves normal registered IAM HTTP admission and successful persisted214 total; then real stale Redis107 payload is reinserted by a simulated delayed read-cache writer and GET incorrectly returns107. No runtime mutation defect repair is proposed in this lane. |

Read-only consumers: Intranet `40275ec6c5b3547ab6d5363fcb8cc03b82d3f473`, `Legacy.Maliev.Intranet.Bff/Quotations/QuotationCreationGateway.cs` reads `/quotations/{id}` for the version before a PUT. Accounting `ae0826156b06c34476e95de8c53dfccfcf5a5972`, `Legacy.Maliev.AccountingService.Data/InvoiceCreationDownstreamClients.cs` `InvoiceCreationSourceClient.GetAsync` consumes that detail's CustomerId/EmployeeId/CurrencyId/Subtotal/Vat/Total/WithholdingTax/Comment/Fob/ShippedVia/Terms/InvoiceId plus separately retrieved items. Returning stale financial state can affect its invoice source snapshot. Their contracts need no field/route change for a fresh detail projection. Neither consumer was modified, built or runtime-invoked.

## Genuine RED and controls

New tests compiled after baseline: Release0W0E. First diagnostic focus:4 genuine RED/4 passing controls plus1 test-assumption failure (nonexistent PageSize), preserved `TestResults/draft-read-red/natth_MALIEV-31USFIV_2026-10-01_14_43_51_net10.0.trx`. PageSize assertion corrected to existing TotalPages and explicit PageSize absence, not a runtime defect.

Fresh Release0W0E then final nine-case focus: **4 failed /5 passed /0 skipped**, `TestResults/draft-read-final-red/natth_MALIEV-31USFIV_2026-10-01_14_44_54_net10.0.trx`:

- `%` and `_`: expected exactly literal-bearing row; actual includes an unrelated comment on a second same-customer row.
- Numeric ID: expected matched Id only; actual includes a Comment-only match.
- Delayed cache writer: PUT204 and one actual normal IAM admission, fresh PostgreSQL confirms Total214/Commentfresh before Redis insertion; exact stale DTO roundtrips real Redis; subsequent GET200 returns107 instead of214.
- Passing controls: backslash case as currently tested; case-insensitive trim/customer isolation/pagination/null omission; normal unauthenticated401; no-permission403; customer mismatch and empty-search404.

No intended failures were skipped/softened. At this historical gate the test/design tree was deliberately not mergeable; baseline full435 was distinct from the nine new executed cases.

## Approved minimal runtime scope

Only `QuotationRepository.GetQuotationsAsync`: escape literal percent, underscore and escape character with explicit ILIKE escape; use exclusive numeric branch matching Id/CustomerId. Preserve current trim, numeric parsing, customer filter, six sorts/tie breakers, page bounds and404 behavior.

Only `QuotationRepository.GetQuotationAsync`: PostgreSQL-authoritative existing projection, no cache read/write authority. Keep every mutation's existing cache invalidation and separate create idempotency caching untouched. This closes delayed cache writer correctness without claiming multi-read invoice/Quotation atomicity. PG/provider faults and caller cancellation remain their existing categories; no blanket exception suppression.

Root approved stronger missing/deleted/cache-poison and numeric CustomerId controls before the minimal two-method repair. Current implementation performs escaped explicit ILIKE and exclusive numeric branching; administrative detail is PostgreSQL-only. No controller/DTO/auth/schema/config/grant/receipt/deployment changes. #71 whole financial aggregate lifecycle remains open (create, line/link financial computations, rollback and other acceptance are not proved by this slice).

## Extended RED and normal authority scope characterization

Fresh pre-repair Release0W0E, extended16 cases:6 genuine parity failures/9 controls plus1 resource expectation diagnostic; preserved `TestResults/draft-read-extended-red/natth_MALIEV-31USFIV_2026-10-01_14_50_39_net10.0.trx`. Separate actual normal-client diagnostic `TestResults/draft-read-resource-diagnostic/natth_MALIEV-31USFIV_2026-10-01_14_51_36_net10.0.trx` proved resourcePath `global`, not the assumed `/quotations/1`.

Pinned Defaults8f handler uses route resource templates only if `Features:ResourceScopedAuthEnabled` is enabled; defaultfalse remains unchanged in production. Root approved exact global as a compatibility control, explicitly **live admission, not resource-specific proof**. A separate fixture-only enabled control verifies exact `/quotations/id` with the same actual normal client/DI and no permission fallback. Principal, POST path, synthetic live credential and `bypassCache=true` are asserted; resource is asserted outside the transport so policy exception handling cannot mask a diagnostic. No runtime auth/config repair is included; activation/acceptance remains a separate root-owned gap.

Corrected pre-repair Release0W0E/focus17:8 genuine RED/9 controls/0 skips, `TestResults/draft-read-approved-red/natth_MALIEV-31USFIV_2026-10-01_14_53_25_net10.0.trx`. Added failures: unscoped numeric CustomerId admits Comment-only row; poisoned missing/deleted rows return200 instead of404; stale updated detail fails in both global/scoped fixture modes. Combined percent/underscore/backslash, Thai and trailing-backslash cases and scoped numeric CustomerId control already passed before repair; they are regression controls, not invented REDs.

After minimal repair, fresh Release0W0E/focus17 passed/0 failed/0 skipped, `TestResults/draft-read-green/natth_MALIEV-31USFIV_2026-10-01_14_54_31_net10.0.trx`. Final full452 passed/0 failed/0 skipped (2m34s), `TestResults/draft-read-full/natth_MALIEV-31USFIV_2026-10-01_14_54_37_net10.0.trx`; all435 unchanged old cases passed. No obsolete old expectations were suppressed or edited.

## Final commands and coverage precision

Sequential owned commands used `UseLocalMalievDependencies=true`, absolute `MalievWorkspaceRoot=<owned-worktree>/.dependencies` and exact clean detached CI refs. `dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=$env:MalievWorkspaceRoot -nodeReuse:false` passed0W0E. Focus used `dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build --no-restore --filter 'FullyQualifiedName~QuotationDraftReadParityHttpTests' --logger trx`; full used the same solution/Release/no-build/no-restore plus `--collect 'XPlat Code Coverage'` and owned results directory. Full coverage collection did not exclude new code.

Exact report: `TestResults/draft-read-full/0804d9ae-d882-4d31-9b7e-b2961f9bdc36/coverage.cobertura.xml`. Executed unchanged `python scripts/check_owned_coverage.py <exact-report> --minimum 80`: PASS, owned handwritten5184/5477=94.65%; API425/469=90.62%, Application157/163, Data3944/4053, Domain95/99, MigrationRunner563/693. This is the checker's actual handwritten denominator, not raw XML. Raw entire XML (includes external dependencies/generated files)6419/10315=62.22%. Generated obj/external assemblies retain separate rows in actual checker output.

Whole `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore`, `python -m unittest discover -s scripts/tests -p 'test_*.py'` (5 passed; deliberate rejection-case stderr is expected), `actionlint`, `git diff --check`: PASS. `PYTHONDONTWRITEBYTECODE=1` prevented generated Python cache files. `dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive --no-restore`: no vulnerable packages across all six projects using current NuGet sources. `gitleaks git . --redact --no-banner`:74 commits/no findings. Final three-owned-file stdin scan and exact CI73dd signing-resource scanner: no findings.

The independently observed unchanged base main's CI run36820845264 succeeded. That is base acceptance, not this uncommitted candidate's protected CI. Root must independently validate/integrate the frozen candidate; no remote acceptance, full joined Auth rerun, provider/runtime deployment or whole71 closure is claimed.

## Independent root candidate acceptance

Root independently read all three final files, source/consumer boundaries and current
permission configuration. Direct test-project Release compilation including both exact
private dependencies passed with zero warnings/errors (`-warnaserror -nodeReuse:false`).
Sequential focused17 and unfiltered452 tests passed with zero failures/skips; original435
tests remain unchanged. Root TRX files are `TestResults/root-focus/root-focus.trx` and
`TestResults/root-full/root-full.trx`. Root raw coverage report is
`TestResults/root-full/1f2f756f-8003-4ae6-898d-112478e997aa/coverage.cobertura.xml`.
The unchanged checker passed owned5186/5477=94.69%, API427/469=91.04%; generated/external
denominators are separately reported. Whole solution formatting, five coverage-checker
tests (including deliberate rejection stderr), actionlint, six project transitive
vulnerability audits, diff whitespace and three-file redacted stdin scan passed. The
scan covered86,839 bytes with no findings. Ignored owned artifacts are retained for
review and excluded from staging. The observed publication variable is false.
Protected exact-head/main checks are still required; this local acceptance is not
deployment, persistent data parity, real external IAM or full parent71 completion.

## Individually retained exclusions

All controller-owning tracked SHA cohorts remain individually unresolved beyond this bounded `5fac706a7983a6d359b39acbd670e6800afe020e` read/list behavior:

- `e790933f67e7dd748e34ae805a8b85f268ce1aa1`: accepted analytics/outbox/provider delivery not exercised.
- `6c1a4921b3524dd575cab5b1f4aa5774ad9b8997`: employee acceptance boundary unchanged, no new acceptance claim.
- `c821605b7ecde5f79d01966888b116defb10650d`: invoice linking/saga dependency is not repaired here.
- `3b8bfe3658a1205b4811d063d44bec8cfa88d895`: accepted outcome reconciliation excluded.
- `7ebbc97b0b93c6f71b8b5079ebd404e42c259338`: privacy-safe outcome readback excluded.
- `c6ac93af03a0dafc506d9570aca96e4aed3b1643`: marketing measurement/search ownership excluded.
- `7ebe7e4bf83a435ecb18afc29cf263424ed2bb74`: persisted journey attribution unchanged, not accepted wholesale.

Requests73/80/create, qualification70/68, attachments72, worker74, schema/deployment22/55, provider delivery and CNC expansion are excluded. No actor-binding/global idempotency partition or signed-grant/live authority policy changes. No commit/push/GitHub, deployment, persistent data/DDL or provider traffic.
