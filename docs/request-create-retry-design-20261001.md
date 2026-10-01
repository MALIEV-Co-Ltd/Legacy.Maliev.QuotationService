# Request-create retry boundary — bounded runtime candidate, Quotation #73

## Scope and frozen identity

Owned branch `codex/quotation-request-create-retry-20261001`, base
`c02f0f5e9feb81e6e814fcbb4046908e653bdb19`. The initial gate changed tests/design
only; root subsequently approved the bounded runtime repair below. Existing
tests, configuration, migrations, consumers and CI remain unchanged. No commits, pushes,
deployments, persistent SQL, source edits or external state writes. Disposable
PostgreSQL 18 and Redis are test-owned only. This is create-only, not completion
of the broader request lifecycle/source-parity issue #73.

Private ignored dependencies under `TestResults/.request-create-dependencies`:

- Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`.
- Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.

No sibling dependency binaries are used. The source bare mirror is read-only,
with cutoff `bed10c7d15e0698e0b75f1329d0f312937f5d77f`.

## Source lineage and consumers

| Full source SHA | Relevant behavior | This slice |
| --- | --- | --- |
| `5fac706a7983a6d359b39acbd670e6800afe020e` | Original request create/lifecycle | Create boundary only |
| `7ebe7e4bf83a435ecb18afc29cf263424ed2bb74` | Journey attribution on request creation | Retain optional JourneyId |
| `3e95e397f7180356f0b5a8acfeed0119d1db62ad` | Server-owned transaction attribution test | Retain `request-{Id}` |
| `d852d3ef0ea45bba51bb29de557784b5e5fffae6` | Qualification/outcome readback | Excluded; separate acceptance |

Target `QuotationRequestsController.CreateQuotationRequestAsync` calls either
`QuotationRepository.CreateRequestAsync` or `CreateRequestIdempotentlyAsync`.
The Web infrastructure `QuotationClient` sends the eleven PascalCase input
fields, optional JourneyId, and an opaque Idempotency-Key. It requires 201,
JSON PascalCase response, `/quotationrequests/{Id}` Location, matching request
fields/JourneyId and server-owned TransactionId `request-{Id}`. The Intranet
`QuotationRequestsProxy` consumes request details/page/files and qualification;
it is not changed. File upload/idempotency and triage/readback are excluded.

## Executed evidence

Build command, run before each focused execution:

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-request-create-retry-20261001\TestResults\.request-create-dependencies
```

Every build so far: **0 warnings, 0 errors**. Original full suite was executed
before adding new tests: **332 passed, 0 failed, 0 skipped**.
TRX: `TestResults/request-create-baseline-full/natth_MALIEV-31USFIV_2026-10-01_09_58_10_net10.0.trx`.

Focused command (same exact dependency environment, no build/restore):

```powershell
$env:UseLocalMalievDependencies='true'
$env:MalievWorkspaceRoot='B:\maliev-legacy\.worktrees\quotation-request-create-retry-20261001\TestResults\.request-create-dependencies'
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~QuotationRequestCreateRetryHttpTests --logger trx --results-directory TestResults/request-create-review-red
```

Initial eight-case gate after test-expectation correction: **2 failed, 6 passed,
0 skipped**, TRX `TestResults/request-create-entry-red-final/natth_MALIEV-31USFIV_2026-10-01_10_06_52_net10.0.trx`.
Both keyed and unkeyed legitimate HTTP POSTs return **400**, not the anticipated
500; each reaches the real IAM live-check client once. The registered request
context reports `RetriesOnFailure=true`. A fixture logger retains only the
boolean category: `InvalidOperationException` containing the provider's
`user-initiated transactions` category. No log text, token, options or request
contents are retained. Fresh contexts find **zero JourneyId rows**.

Six passing controls: anonymous401, expired401, live IAM denied403 despite
signed create permission, absent live IAM client403, overlong key400, malformed
JSON400. Each stops before repository mutation. Actual pinned IamServiceClient
and normal authorization handler are used; only remote named HTTP transport is
controlled. Synthetic generated RSA keys/claims and IAM credential are fixture
inputs, not live IAM acceptance.

Two earlier diagnostic TRXs (`request-create-entry-red` and
`request-create-entry-red-corrected`) each contain 3 failures/5 passes. Their
third failure is a NEW test expectation defect about whitespace header/problem
shape, not an additional production finding. They are preserved. The final
control uses unambiguous malformed JSON instead; no production/old expectation
was weakened and no whitespace policy conclusion is claimed.

Expanded preparation is **12 failed, 6 passed, 0 skipped**, TRX
`TestResults/request-create-review-red/natth_MALIEV-31USFIV_2026-10-01_10_10_11_net10.0.trx`.
Classification is **2 genuine entry RED + 10 blocked boundary cases + 6 controls**:
same-payload replay/changed-payload conflict, concurrent same-key create,
keyed/unkeyed second-save rollback, keyed/unkeyed lost commit acknowledgement,
and four pre/post-commit caller-cancellation cases. All ten stop at current
400 entry rejection. Fault tests explicitly assert `Fired`; none reached the
fault. Thus rollback, ambiguous acknowledgement, cancellation or concurrency
correctness has NOT been demonstrated. The intermediate fourteen-case TRX
`request-create-expanded-preparation` (8 failed/6 passed) is also preserved.

## Exact existing receipt and compatibility

`RequestCreateIdempotency`: global `KeyHash` primary key, `Fingerprint`,
`RequestID`; no actor/subject binding. Keep SHA256 lowercase key hash,
fingerprint canonicalization/null-Journey compatibility and advisory namespace
`quotation-request\n{keyHash}` unchanged. Keep existing 409 problem extension
`idempotency_key_conflict` and 503 `idempotency_store_unavailable`.

There is no durable subject proof in this schema. Do not call it actor-bound or
silently add a column/change key partition. Current live create permission is
required on every request, including replay. Actor binding is a separate
security/compatibility policy gate for #73/parent, not this bounded retry fix.

## Approved minimal runtime design

1. Limit runtime changes to the two create repository paths/private helper,
   plus a narrow typed unavailable result/exception and create-action mapping
   if needed for the unkeyed contract. No global exception mapping, schema or
   retry policy changes. Preserve public DTOs, JSON, Location and permission.
2. Reuse the scoped context's **registered DbContextOptions** (including data
   source, provider retry configuration and interceptors), creating a fresh
   QuotationRequestDbContext inside each configured execution-strategy attempt.
   Never reuse tracked entity/audit state across attempts. Do not change the
   production fixture registration to disable retry or replace repositories.
3. Keep both saves (root ID, then TransactionId + optional durable binding) in
   one transaction. Pre-commit transient failure may retry only after confirmed
   rollback/disposal; fresh attempt constructs a new entity. Timestamp/response
   must describe the finally persisted row, not an abandoned ID attempt.
4. Fence keyed attempts using the existing advisory namespace. Existing binding
   with changed fingerprint returns409 without writes. Matching tuple reads the
   exact root with fresh no-tracking context and retains201/Location. Missing
   root or invalid/unavailable verification remains503, never a fabricated
   successful response. Preserve the existing unique PK and atomic receipt.
5. Track COMMIT submission separately from acknowledgement and include async
   teardown in the guard. After ambiguous submitted commit, **never blindly
   replay the write**. For keyed create, a fresh bounded read proves the exact
   stored keyHash/fingerprint/root-ID tuple and server-owned TransactionId; only
   that durable proof can return existing success. Failure/absence of proof is
   generic503, not permission bypass, signed-claim fallback or guessed receipt.
6. Unkeyed create has no durable operation receipt. Any ambiguous COMMIT or
   post-commit teardown failure is typed generic503, with no automatic replay.
   The durable root may exist: callers must not be told failure proves absence.
7. Propagate caller cancellation. Before COMMIT, confirmed rollback preserves
   zero rows/bindings; after durable COMMIT, row/receipt remains. A canceled
   request does not invent201/503 or perform cancellation-ignoring mutation.
   Later keyed retry uses normal current authorization and exact receipt proof.
   For a still-connected ambiguous request, reconciliation needs an explicit
   bounded deadline (proposed linked10s) and no hidden positive cache.

## Test-first sequence after approval

- First minimal entry repair: run all18 current cases. Only then can the
  prepared fault cases yield genuine boundary evidence. Record each reached
  injection and persisted graphs; do not equate the current400 with fault RED.
- Inject recognized transient40001 before second SaveChanges, prove transaction
  rollback and fresh-context successful retry; rootID/TransactionId/binding
  remain one coherent persisted graph. Add unretryable-save rollback control.
- Inject Npgsql/IO lost acknowledgement after actual COMMIT: keyed exact receipt
  resolves one root; unkeyed503 and exactly one actual commit. Add missing/damaged
  receipt503 and changed-payload409 contrasts without manufacturing actor proof.
- Cancellation at second save vs after actual commit must independently prove
  precommit zero graphs vs postcommit durable graphs and keyed later replay.
- Concurrent same key must return same root/location/body and one receipt;
  different fingerprint409; mismatched root/receipt remains unavailable.
- Add disposal-after-commit fault and cancellation during bounded readback;
  never auto-replay unkeyed or treat arbitrary InvalidOperationException as
  proof of an ambiguous successful commit.
- Final fresh Release, new focus, affected full suite, unchanged contract
  snapshots/coverage checker, format and applicable static/security gates.

The test/design gate above intentionally retained failing tests before root
runtime approval. It was not a passing runtime candidate. Root owns any issue
split/integration and independent acceptance. The later chronology follows.

## Runtime implementation and later executed chronology

Changed runtime paths only:

- `Data/QuotationRepositories.cs`: two create methods/private helpers. Registered
  options are reused with fresh context per configured strategy attempt. The
  scoped requests context is not relied on for final tracked state/readback.
- `Application/Models/QuotationRequestCreateUnavailableException.cs`: narrowly
  typed unkeyed unavailable outcome; no public wire/schema changes.
- `Api/Controllers/QuotationRequestsController.cs`: only the unkeyed create
  branch catches that type and returns503 problem code `request_create_unavailable`.
  Keyed503 remains `idempotency_store_unavailable`; existing409 unchanged.

Entry-only fresh-context repair: Release0W0E, **17 passed/1 failed** of18,
TRX `request-create-reached-fault-red/natth_MALIEV-31USFIV_2026-10-01_10_38_51_net10.0.trx`.
All prepared fault injections were reached. The remaining unkeyed lost-ACK
failure was verified with a fresh persisted read BEFORE HTTP assertion:
**HTTP201, roots2, receipts0, CommitAcknowledgements2**. Targeted proof was
1 pass/1 fail, TRX `request-create-duplication-red/natth_MALIEV-31USFIV_2026-10-01_10_40_33_net10.0.trx`.

Submitted-COMMIT/teardown guard repair: Release0W0E, **18 passed/0 skipped**,
TRX `request-create-guard-green/natth_MALIEV-31USFIV_2026-10-01_10_42_18_net10.0.trx`.
Unkeyed lost ACK now503, one actual commit/root, no receipt; keyed response only
after fresh durable proof. Precommit40001 retries only after confirmed explicit
rollback, with a fresh normally configured attempt context.

Expanded32-case diagnostic run:31 pass/1 setup failure from an accidentally
reused damaged TransactionId literal in the NEW tests, violating the real unique
index. TRX `request-create-receipt-controls-red/natth_MALIEV-31USFIV_2026-10-01_10_45_12_net10.0.trx`
is retained and is NOT genuine runtime RED. Unique synthetic per-Journey damaged
values corrected the fixture. Isolated normal replay then genuinely returned201
with damaged server attribution where503 was required: TRX
`request-create-damaged-attribution-red/natth_MALIEV-31USFIV_2026-10-01_10_45_55_net10.0.trx`.
Narrow ordinary receipt replay validation fixed this; **32 pass/0 skipped**, TRX
`request-create-32-green/natth_MALIEV-31USFIV_2026-10-01_10_46_46_net10.0.trx`.

Root further clarified the submitted-COMMIT rule: ANY exception there is
uncertainty, not validation400. An initial new contrast incorrectly accepted
postcommit InvalidOperationException400. That expectation was superseded, not
claimed as safe. Actual TransactionCommittedAsync throws after durable COMMIT;
fresh root count1/commit count1 were independently observed before HTTP assertions.
Updated two postcommit cases genuinely failed400, while two precommit400/zero-row
controls passed: TRX `request-create-arbitrary-postcommit-red/natth_MALIEV-31USFIV_2026-10-01_10_49_13_net10.0.trx`.

The minimal guard now catches any failure ONLY when this attempt has submitted
COMMIT or rollback is unconfirmed; caller cancellation still propagates. Keyed
success requires a fresh **single SQL statement** matching keyHash, fingerprint,
expected original rootID and `request-{rootID}` attribution, bounded by linked10s.
Any missing/damaged/unavailable proof is503. Unkeyed never automatically replays
after COMMIT submission. Shared middleware is unchanged: unrelated/precommit
InvalidOperationException remains400, and unknown P0001 save failures remain500
after rollback. There is no generic global exception/claim fallback.

Final focused36 cases: Release0W0E, **36 passed/0 failed/0 skipped**, TRX
`request-create-final-focus/natth_MALIEV-31USFIV_2026-10-01_10_52_07_net10.0.trx`.
Includes missing/changed receipt/root/server TransactionId after actual commit,
ordinary damaged-attribution replay, non-retryable save rollback, postcommit
context disposal, pre/postcommit cancellation, linked10s readback expiry/caller
abort, explicit rollback count and distinct attempt ContextIds. Unconfirmed
rollback acknowledgement returns503 without replay; graph remains absent.

The complete public success wire assertions were tightened without changing
production: PascalCase fields, null omission, literal synthetic values, Done,
persisted CreatedDate/ModifiedDate, server attribution and JSON/Location. A fresh
36-case wire-focused run passed (`request-create-wire-final-focus/...10_58_36_net10.0.trx`).

Root review found that the internal uncertainty wrapper discarded its cause,
and a failed rollback could mask the original failure. Actual HTTP bounded
FirstChanceException probes (only exact fixture exception references, no raw
logging; unsubscribed in finally) proved missing original/rollback references:
**2 genuine failures**, while both503/zero-graph/no-retry checks still passed.
TRX `request-create-cause-retention-red/natth_MALIEV-31USFIV_2026-10-01_11_01_23_net10.0.trx`.
The internal wrapper now preserves its actual cause; failed rollback aggregates
original+rollback, and subsequent teardown can add its cause without hiding the
earlier chain. Public503 remains cause-free and unchanged. Fresh build0W0E and
36-case focus passed (`request-create-cause-final-focus/...11_03_35_net10.0.trx`).

Independent read-only review then identified a confirmed-rollback edge: context
teardown InvalidOperationException could replace original40001/P0001 after
successful rollback. Four normal HTTP RED cases (keyed/unkeyed x transient/
unretryable save failure) independently found rollback1, one attempt context,
roots0/receipts0, but HTTP400 and missing original+teardown references. TRX
`request-create-confirmed-rollback-teardown-red/natth_MALIEV-31USFIV_2026-10-01_11_06_05_net10.0.trx`.
The narrow guard now recognizes ONLY an escaping cleanup exception distinct
from a saved prior failure; it retains both internally and returns503 without
replay. The original exception escaping unchanged after confirmed rollback
keeps its prior400/500/transient retry classification. No global catch/mapping,
new production logging, actor/header/policy change or provider retry disable.

Latest Release0W0E and **40 focus passed/0 failed/0 skipped**, TRX
`request-create-teardown-final-focus/natth_MALIEV-31USFIV_2026-10-01_11_08_10_net10.0.trx`.

Static evidence so far: scoped format completed; package vulnerability audit
of all six solution projects reports no vulnerable packages; exact workflow-pinned
signing-resource scanner reports PASS; coverage checker unit suite5 PASS.
An earlier full368 passed before the cause-retention review (`request-create-final-full/...10_52_42_net10.0.trx`),
with unchanged checker report owned5064/5365=94.39%, API403/458=87.99%. That report
is historical evidence, not the final revised-runtime report.

## Final local frozen-candidate evidence

Latest full suite with all new tests compiled/unexcluded: **372 passed,
0 failed, 0 skipped**, 3m23s. TRX:
`TestResults/request-create-frozen-full/natth_MALIEV-31USFIV_2026-10-01_11_08_52_net10.0.trx`.
Command:

```powershell
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore -p:GITHUB_ACTIONS=false --collect 'XPlat Code Coverage' --logger trx --results-directory TestResults/request-create-frozen-full
```

Unchanged checker executed against exactly:
`TestResults/request-create-frozen-full/8c54b265-1d38-413c-b145-82fee13f43a2/coverage.cobertura.xml`
with `--minimum 80`, PASS. SHA256:
`337E66D773511FF401D201C0B480766E395F8571C4CB160D7BFBEC82EF3B73CF`.

| Checker denominator | Covered/total | Percent |
| --- | --- | --- |
| API owned handwritten | 403/458 | 87.99% |
| API generated obj | 0/214 | Not included in handwritten gate |
| API raw | 403/672 | Not substituted for handwritten |
| Data owned handwritten | 3858/3965 | 97.30% |
| All owned handwritten | 5075/5377 | 94.38% |

Final Release0W0E precedes focus40 and full372. Whole-solution format verification,
six-project transitive vulnerability audit, pinned signing-resource scanner,
directory gitleaks, checker unit5 and diff whitespace checks are the final local
static gates. No generated evidence/dependency clone is staged; `TestResults`
is ignored. All failed diagnostic/genuine RED TRXs remain preserved.
No commits or pushes. Root still owns independent acceptance and integration.

## Residual boundaries

## Independent root acceptance

The primary agent reviewed the complete changed runtime paths, new fixture and
fault cases, source/consumer contracts, original-test preservation and internal
cause-retention guard. No runtime changes were needed after the frozen handoff.
Independent Release test-project build used the private exact Defaults/Contracts
graph, with both dependency outputs in Release: **0 warnings, 0 errors**.
Independent focus: **40 passed, zero skipped**, TRX
`TestResults/root-create-focus/natth_MALIEV-31USFIV_2026-10-01_11_15_57_net10.0.trx`.
Independent complete suite: **372 passed, zero skipped**, TRX
`TestResults/root-create-full/natth_MALIEV-31USFIV_2026-10-01_11_16_16_net10.0.trx`.
Unchanged coverage checker on that run reports owned handwritten
**5075/5377 = 94.38%**, API **403/458 = 87.99%**, minimum80 PASS; its five
unit controls also pass. A deliberately failing fixture printed by those checker
unit tests is expected failure-path coverage, not the complete-suite percentage.

Whole-solution format verification, all six project transitive vulnerability
audits, signing-resource scan, scoped directory gitleaks (tests and docs), and
diff whitespace checks pass. The general current-tree scanner reports three
pre-existing synthetic fixture password literals in migration-runner tests;
the clean canonical baseline reports the exact same paths/lines. Root inspected
them: never-connect redaction inputs and a synthetic localhost runner literal,
not live credentials. No scanner suppression, allowlist or old-test modification
was introduced. Generated TestResults/private dependencies/bin/obj remain ignored
and are not staged. The integration closes bounded **#78 only**; broader **#73**
and actor/lifecycle/provider/Aspire acceptance remain open. No deployment or
persistent schema/data writes occurred.

## Remaining acceptance boundaries

No live external IAM, production provisioning/config/schema activation, joined
Auth process or consumer deployment is proved by this fixture. Actual normal
Production HTTP/RS256/registered IAM client and disposable PostgreSQL18/Redis
are proved; the IAM remote HTTP response is controlled fixture evidence.
Receipt/root proof is a point-in-time database snapshot, not immutable actor
binding or immunity to subsequent delete/import/manual corruption. Existing
global keys can replay across currently authorized subjects; separate policy
review remains open. Unkeyed503 after ambiguity can coexist with one durable root,
and an explicit new caller request is not idempotent. Files and broader request
triage/source behavior remain outside this create-only repair.
