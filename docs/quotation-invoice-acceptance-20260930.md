# Quotation68 producer invoice-intent candidate - root review only

## Current implementation and authorization boundary

The producer runtime gap is repaired locally on the exact graph/pins recorded
below. This is a code-only candidate, not a merged, deployed or full-chain
acceptance claim. Accounting23, Quotation21, consumer integration, live IAM and
the coordinated physical schema gate remain open. No commit, push, persistent
DDL, cloud operation, source-system write or external notification was performed.

Existing `PUT /quotations/{quotationId}/decision` accepts optional nullable
`InvoiceId`. Omitted/null retains existing behavior. Negative returns400.
Positive with decline returns400. Explicit zero requires an accepted,
EmployeeInitiated request passing the existing trusted employee caller boundary;
it is no invoice intent and never clears a stored invoice. Nonemployee zero
returns400. Untrusted EmployeeInitiated callers still return403. The trusted
profile remains exact Employee identity or exact service:legacy-intranet with
granular permissions; Accounting is not added to that allowlist.

First positive invoice attachment, first acceptance, immutable event time,
origin and provenance/outcome commit together through EF SaveChanges' PostgreSQL
transaction. Existing nonnull invoice mismatch (including historical stored0)
returns409 without mutation. Already accepted invoice-null late attachment is
employee-only; it changes invoice and ModifiedDate without rewriting first
AcceptedUtc, origin, provenance or outcome. Invoice-aware replay and optimistic
concurrency reconciliation require the same positive invoice intent. Newly
mutating requests honor X-Expected-Modified-Date; harmless replay retains the
existing stale-version retry behavior. Local-first linked-order partial503/409
saga behavior and deterministic retry operation identity are retained.

## Approved additive schema extension and schema-first gate

Investigation proved immutable accepted outcome metadata has no record of the
prior ModifiedDate used by linked-order saga keys. AcceptedUtc cannot substitute
for that historical version: a PostgreSQL HTTP regression with AcceptedUtc
different from ModifiedDate reproduced two changed keys after late attachment.
Root approved an additive extension rather than skipping the saga or changing
existing operation identities.

`Quotation.DecisionOrderVersion` is nullable timestamp without time zone, an
internal scalar not added to API response DTOs. At first late attachment it binds
the PRE-attachment ModifiedDate, else CreatedDate, else UnixEpoch, atomically with
invoice/ModifiedDate. Replay uses that bound version. Fresh mutating decisions
clear the binding and use the new decision ModifiedDate, preserving normal keys.
Historical rows are not bulk rewritten. Migration
`20260930070000_PreserveDecisionOrderVersion` only adds this nullable column;
its destructive downgrade is refused in favor of a reviewed compensating
migration. QuotationRequest schema is untouched. No indexes or foreign keys are
needed for this non-query retry identity. No xmin column is introduced.

Deployment requires separately authorized schema-first rollout through the
existing migration runner, then observed migration history AND physical
`public.Quotation.DecisionOrderVersion` nullable timestamp column, then API rollout
and real permission/consumer checks. Generic database connectivity health is not
physical schema readiness. There is no new runtime readiness health check here.
EF entity reads require this column even for invoice-omitted requests; default-off
does NOT make the binary safe against the old schema. There is deliberately no
missing-column fallback. PostgreSQL18 test evidence proves old-schema reads fail
UndefinedColumn, additive upgrade preserves existing acceptance/invoice/date,
and migration rerun is safe. No persistent target readiness was observed.

Accepted attachment bindings are never used for declined saga keys, including a
prior binding left on a row declined by legacy generic update; a workflow RED/GREEN
regression protects the old declined ModifiedDate-based identity. Historical
already-accepted rows lacking original stamps/outcomes are not silently adopted
by explicit-positive late attachment, same-invoice replay or concurrent late
reconciliation. Their null first metadata stays null and no outcome is inserted.
Omitted/null decisions retain the existing separate metadata-adoption behavior.
Sequential/concurrent missing-metadata cases were independently RED2/2 before
this preservation repair and are included in the latest focused run.

## Current fixture and validation evidence

The HTTP/PostgreSQL fixture now runs the normal exact-pin shared RS256 JWT bearer
validator with ephemeral test-owned keys. The static fixture permission policy
replaces IAM lookup only; it is not live IAM or delegated Accounting proof.
Real controller/workflow/repository, model binding, migrations, PostgreSQL18 and
OrderDecisionClient are exercised. External OrderService HTTP remains controlled.
Cache is no-storage. Separate Quotation/QuotationRequest test databases remain.

Expanded test-first invoice regressions were RED13/18 (5passed), then GREEN21/21.
The historical partial-order/late-attachment/replay regression was independently
RED1/22 (21passed), then GREEN22/22 after the additive bound-version repair.
Latest focused HTTP+workflow run is39/39 passed,0skipped:
`TestResults/quotation68-focused-final-v3.trx`. It includes null/zero/customer positive
initial intent, invalid input, exact service trust negatives, conflicting and same
invoice concurrent first/late intents, stale late expected version, historical0,
first-outcome transaction rollback, late binding/invoice/date rollback, unchanged
generic full-update rejection, order-key preservation and physical schema proof.
An intermediate full suite passed224/224,0skipped before the latest extra tests.
Fresh exact-pin Release build passed0warnings/0errors; final focused39/39 and
full237/237 with XPlat coverage passed,0skipped (2m37s):
`TestResults/quotation68-full-final-v3.trx`, coverage under
`TestResults/375e3b0f-32b9-4cc4-a452-08933b3174f0/coverage.cobertura.xml`.
Prior full runs234/234 (2m52s) and235/235 with coverage (3m8s) passed,0skipped
but predate the final historical missing-metadata repair.
Exact-pin `dotnet format ... --verify-no-changes --no-restore` exited0;
exact-pin package vulnerability audit exited0 with no vulnerable packages in all
six service projects. Redacted scoped Gitleaks directory scans covering API
controllers, Application, Data, Domain, test controllers and docs found no leaks.
`git diff --check` passed. Dependency heads were reconfirmed and remained clean.
Final237test coverage collection reported Application96.27%,
Data97.36%, Domain94.94%, MigrationRunner81.44% line coverage, but API46.19%.
Raw API coverage includes generated OpenAPI output; it is not the configured service-wide handwritten denominator. Independent root invocation of the unchanged `scripts/check_owned_coverage.py` on the final full-suite report passed 4839/5172 owned handwritten lines (93.56%, minimum80%). API handwritten coverage is261/351 (74.36%), still a visible lifecycle acceptance residual, not implicitly proven by the aggregate gate. No generated-code exclusion or coverage policy was changed. Shared dependency coverage is outside
this producer slice. Live IAM, hosted consumer chain, source SQL Server and
persistent schema readiness were not executed; there is no claim for those gates.

An overlapping build while that full testhost held its DLL failed MSB3027/MSB3021
(10warnings/2errors); this is a harness output-file lock, not a passing build.
It was followed by a serialized Release build with0warnings/0errors. A package
audit without exact-pin environment restored default local dependencies; it is
not authoritative graph evidence and is followed by exact-pin restore/build.
No source or fixture was changed to mask these execution errors.

Independent root verification used the recorded exact pins in `TestResults/.dependencies`: Release0warnings/0errors; focused39/39 and full237/237 passed, zero skips (full2m23s), with durable reports under `TestResults/root-quotation68-focused/` and `TestResults/root-quotation68-full/`. A first root build used an incorrect `.dependencies` path and failed1warning/83errors; correcting only the dependency-root argument restored the exact graph. That failed harness invocation is not counted as a successful build.

## Historical RED checkpoint (preserved traceability, superseded status)

The following original checkpoint records the pre-implementation state. Its
test-only/no-production-change statements describe that earlier checkpoint,
not the current candidate. Six source scopes remain independent and are not
marked fully implemented or resolved by this slice.

# Quotation68 invoice-intent RED handoff

This is a test-only, deliberately RED checkpoint, not an implemented fix or a
release-ready slice. Parent issues Quotation21 and Accounting23 remain open.
No production code, DTO, repository, schema migration, consumer, route, ledger,
provider delivery or external state was changed.

## Exact graph and isolation

- Quotation main/origin: `9788378d7628917c8fef9bc7df70bfbf41afd7fa`.
- Exact-main CI `36441811934`: `validate / validate` succeeded.
- Branch: `codex/quotation-invoice-acceptance-20260930`.
- Worktree: `B:\maliev-legacy\.worktrees\quotation-invoice-acceptance-20260930`.
- CI ServiceDefaults pin: `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`.
- CI CompatibilityContracts pin: `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.
- Dependencies were cloned locally without fetching into ignored
  `TestResults/.dependencies`; detached heads and clean status were verified.
- Existing source objects were inspected read-only through checkpoint
  `4198baa6b0e7903f2b9b6e3d5d68f9d2c2b5b0db`. New remote source
  `bed10c7d15e0698e0b75f1329d0f312937f5d77f` is not locally available and is not
  silently folded into this slice.

## Concrete producer/consumer conflict

Accounting main `ae0826156b06c34476e95de8c53dfccfcf5a5972` has
`InvoiceQuotationCompletionClient` in
`Legacy.Maliev.AccountingService.Data/InvoiceCreationDownstreamClients.cs`.
It GETs the quotation, then full-PUTs `InvoiceId` and `Accepted=true` before
PUTting `/decision` whenever the quotation is not already accepted/linked.
QuotationRepository.UpdateQuotationAsync correctly rejects promotion to accepted
through that ordinary update. Accounting's invoice was committed before this
downstream rejection. Its mocked client test returns success for every non-GET,
so it cannot prove this producer/consumer chain.

The new test sends that original full-update shape over the real producer HTTP
boundary and verifies conflict with no invoice/decision/outcome mutation. The
Accounting class itself is not loaded or changed in this Quotation-only lane.

The source `/accept` alias is not proposed. Migrated Accounting and Intranet
already use `/decision`; preserve its order transitions and existing response
shape. No controller method or route is added here. Historical AGENTS guidance
states 34 actions/35 templates; the current executable contract census already
asserts 38 methods/39 templates including later outcome/qualification additions.
Neither inventory is edited by this slice.

## Six independent source scopes

| Full SHA | Relevant scope retained |
| --- | --- |
| `e790933f67e7dd748e34ae805a8b85f268ce1aa1` | Acceptance/invoice/replay boundary; ordinary update cannot bypass atomic acceptance; consented analytics enqueue. |
| `2bb747212f8c722115e53691e10a522d8a793cf7` | Leased durable provider delivery, bounded retries, terminal delivery states and hosted worker. |
| `6d74fb5d1370e65d6e5322adf12f36e4cd2d8458` | Delivery processor DI construction and separate startup warning repair. |
| `6c1a4921b3524dd575cab5b1f4aa5774ad9b8997` | Employee override/no-invoice acceptance and customer invoice/decline rules. |
| `c821605b7ecde5f79d01966888b116defb10650d` | Late positive invoice attachment to an already accepted invoice-less quotation. |
| `c6ac93af03a0dafc506d9570aca96e4aed3b1643` | Provenance in analytics intent and attributed/unattributed readback. |

Provider-neutral outcome/readback behavior already exists. These tests do not
port provider delivery, persist browser identifiers, approve retirement, resolve
any entire mixed source SHA, or establish Aspire acceptance.

## Fixture boundary and expected results

`QuotationInvoiceDecisionHttpPostgresTests` uses the real HTTP model binder,
QuotationsController, QuotationDecisionWorkflow, QuotationRepository,
OrderDecisionClient and PostgreSQL18. Quotation and request contexts use distinct
databases in one test-owned disposable container. A synthetic Employee identity
and explicit fixture permission policy are used, not signed JWTs or live IAM.
The cache is a no-storage double. Only external OrderService HTTP is controlled
by a test handler; no live service, storage, provider or financial operation runs.

| Case | Baseline result |
| --- | --- |
| Positive invoice on first decision | RED: HTTP200, invoice remains null. |
| Different invoice after accepted/link | RED: HTTP200 instead of409. |
| Late attachment | RED: HTTP200, invoice remains null. |
| Two synchronized different invoice intents | RED: both return200 instead of one200/one409. |
| Same-invoice replay | GREEN: first timestamp/link/outcome unchanged. |
| PostgreSQL rejects outcome insertion | GREEN: accepted/invoice/stamps/outcome unchanged. |
| Accounting old full-update promotion | GREEN: HTTP409 and no mutation. |
| Omitted invoice intent | GREEN: existing decision succeeds and linked-order transition still occurs. |

The concurrent case holds both optimistic decision saves at a bounded barrier;
no timing sleeps are used. The SQL fault adds a CHECK constraint rejecting only
one generated test quotation ID in the disposable database; it verifies actual
database transaction rollback, not an in-memory substitute. Fixture-only
DbUpdateException translation produces a non-success HTTP result and makes no
claim about production exception status. Container disposal removes its data.

## Minimum producer change for root review

Propose an optional invoice intent on existing QuotationDecisionRequest. Omitted
intent preserves current Intranet/client semantics. Positive explicit intent
must participate in the same local SaveChanges transaction as first acceptance
and immutable outcome, with a reviewed late-attachment branch. Both early replay
and concurrency reconciliation must compare the invoice intent, not merely
`Accepted=true` plus an existing outcome. Do not rewrite first AcceptedUtc,
origin, provenance or outcome on invoice attachment/replay.

Possible producer files: Application/Models/QuotationModels.cs,
Application/Interfaces/IQuotationBoundaries.cs,
Application/Services/QuotationDecisionWorkflow.cs, Data/QuotationRepositories.cs,
Api/Controllers/QuotationsController.cs and corresponding tests. Paths are a
proposal, not implementation authority. Existing columns suffice; no migration
is proposed for invoice intent.

Root must approve omitted/null/zero/negative semantics, customer versus employee
authority and trusted Accounting workload/delegation profile before those
expectations are added. Current trusted employee service branch accepts only
`service:legacy-intranet`; an ordinary Accounting machine token is not silently
equivalent to employee authority. Negative ownership/permission checks and real
service-to-service JWT proof remain required later gates.

Analytics fields are not sent in these tests. Rejecting unsupported nonempty
analytics context versus assigning a separate delivery owner is a reviewed
policy gate, not permission to silently accept and discard it.

## Serialized consumer and recovery handoff

After reviewed producer protection/merge, Accounting23 can replace the full
quotation PUT with invoice-aware `/decision`, retaining expected-version and
stable operation identity where the approved contract requires them. Consumer
files: InvoiceCreationDownstreamClients.cs, InvoiceCreationDownstreamClientTests.cs,
InvoiceCreationWorkflowTests.cs and real cross-service HTTP/PostgreSQL proof.
Intranet QuotationDecisionProxy's existing invoice-omitted JSON must remain
compatible; no Intranet change is authorized here.

Quotation locally commits before linked-order HTTP transitions. Existing
partial503/409 and retry keys remain part of the saga. Invoice creation, document,
file and notification effects are not made distributed-atomic by this producer
change. Accounting's Pending/NeedsReconciliation admission must remain fail-closed
after unknown downstream results. Do not delete the committed invoice, create a
fresh operation, auto-resume uncertain effects, or mark Accounting23 complete.

## Executed validation

Authoritative commands use standard bin/obj inside this worktree and ignored
exact-pin dependency clones:

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release `
  -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-invoice-acceptance-20260930\TestResults\.dependencies `
  -p:UseLocalMalievDependencies=true
dotnet test Legacy.Maliev.QuotationService.Tests\Legacy.Maliev.QuotationService.Tests.csproj `
  -c Release --no-build --no-restore `
  -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-invoice-acceptance-20260930\TestResults\.dependencies `
  -p:UseLocalMalievDependencies=true --logger 'trx;LogFileName=quotation68-standard-baseline.trx' `
  --results-directory TestResults
```

Baseline: Release0W/0E; 202/202 passed, zero skipped. Post-test build:0W/0E.
Focused RED: 8 total,4 expected failures,4 passed,0 skipped,
`TestResults/quotation68-red.trx`.

Non-authoritative attempts are retained honestly: an initial default-dependency
build used newer canonical shared heads (0W/0E;202 tests passed), then an exact-pin
artifacts-path baseline failed one existing fixed-directory-depth Dockerfile test
(201 passed/1 failed). Standard CI layout corrected this harness-path issue;
no fixture/runtime repair was made. Initial outputs are preserved.

Post-format Release build: 0 warnings/0 errors. Full suite: 210 total,
206 passed, the same four expected invoice-intent failures, zero skipped;
`TestResults/quotation68-full-red.trx` (2m17s). All 202 existing tests passed.
`dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore`
with the same exact-pin properties supplied as process-local environment
variables exited0. The initial formatter invocation using unsupported CLI
`-p:` arguments did not run; only the new test file was mechanically formatted
before this successful verification. Package vulnerability inspection
(`dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive`)
exited0 and reported no vulnerable packages in all six projects against NuGet's
current source. Scoped redacted Gitleaks scans of both new files found no leaks.
Git whitespace verification passed. Final readback confirms only the two new
allowed files are untracked, with no tracked diff; the canonical Quotation,
ServiceDefaults and CompatibilityContracts checkouts remain clean.

Same-invoice replay and rollback are preservation baselines, not proof that a
new invoice intent is currently honored: the existing binder ignores that
unknown property. Invoice-aware replay/rollback must remain covered when the
producer repair is authorized. This checkpoint has no GREEN repair, hosted
producer/consumer JWT proof, deployment or release claim. No commit/push/PR.
