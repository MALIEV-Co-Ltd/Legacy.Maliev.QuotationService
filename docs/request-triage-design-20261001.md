# Request triage #73: test/design boundary

Status: root-approved bounded runtime implementation for child issue #80, with
acceptance still pending. The tests/design-only RED chronology below is retained;
the original four-failure candidate was not merge-ready. This is not whole-#73
acceptance. No commits, pushes, deployment, persistent
database operations, source writes or consumer edits are authorized here.

## Exact revisions and ownership

- Protected Quotation base: `18f71e8e4501f4f18e3f939b4f255baf1fee4717` (PR79).
- New exclusive workspace: `B:/maliev-legacy/.worktrees/quotation-request-triage-20261001`.
- Branch: `codex/quotation-request-triage-20261001`.
- Prior create workspace remains frozen/root-owned.
- Private ignored dependency root: `TestResults/.triage-dependencies`.
- Defaults: `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`.
- Contracts: `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.
- Source authority: committed objects in the isolated bare mirror through
  `bed10c7d15e0698e0b75f1329d0f312937f5d77f`, never original working files.
- Read-only consumer snapshots: Intranet `40275ec6c5b3547ab6d5363fcb8cc03b82d3f473`,
  Web `6d0dc9f7095b4128becbf25af5f4de2b22ed7731`.
- Historical read-only ledger snapshot: Workflows `eb4441d94c7f4d6a13619f7431fb3ceaee3d841d`.
  Current tracking is the separate MigrationTracking extraction; no tracking edits
  or source dispositions belong to this candidate.

Owned files after root's runtime gate: new `QuotationRequestTriageHttpTests.cs`,
this document, triage-only methods/private helpers in `QuotationRepositories.cs`,
new `QuotationRequestMutationUnavailableException.cs`, and narrow PUT/DELETE
catches in `QuotationRequestsController.cs`, plus root-approved explicit real-cache
seeding in the existing qualification replay fixture. All other existing tests, create/qualification/
file implementations, configuration, CI, schemas/migrations and consumers stay
unchanged. Source-owner ledger dispositions are not closed by these tests.

## Full source cohort

| Commit | Relevant behavior |
| --- | --- |
| `5fac706a7983a6d359b39acbd670e6800afe020e` | CRUD controller, nullable Done, six sorts, source pagination and substring search |
| `72eb9f1949176392141951d35e6e06f7c30af4c2` | Request documentation only; no lifecycle implementation delta |
| `7ebe7e4bf83a435ecb18afc29cf263424ed2bb74` | Journey capture and ordinary-update preservation regression |
| `3e95e397f7180356f0b5a8acfeed0119d1db62ad` | Server-owned `request-{Id}` transaction regression |
| `362308b605ff94878f684258ade46c67ae0b08ee` | Qualification projection and immutable transition contract; #70 boundary |
| `99462c12c33fc62da281fc90fc61a8ee458d0d7a` | Relational qualification persistence and receipt privacy; #70 boundary |
| `d852d3ef0ea45bba51bb29de557784b5e5fffae6` | Employee-only bounded UTC outcome readback |
| `da58002047bcea0d00d0769deb99173ef21bdf81` | Suppress both attribution fields if either is incomplete |

Source paths: `Maliev.QuotationRequestService.Api/Controllers/QuotationRequestsController.cs`,
`Maliev.QuotationRequestService.Data/Database/QuotationRequestContext/{Request,QuotationRequestContext}.cs`,
`Maliev.QuotationRequestService.Tests/QuotationRequests/{UpdateQuotationRequestAsync_UnitTest,DeleteQuotationRequestAsync_UnitTest}.cs`
and `Maliev.Entities/ViewModels/PaginatedListWebApi.cs`.

## Action, permission, wire and persisted-state matrix

| Action | Current boundary | Source/current persistence and response |
| --- | --- | --- |
| GET list | Live `legacy.quotation-requests.read`; PascalCase Items/PageIndex/TotalPages/TotalRecords/HasNextPage/HasPreviousPage | Six sorts and eight text fields retained; numeric ID exact; source empty page404 vs target nonempty-total out-of-range200; target size default50/cap250 is bounded target behavior |
| GET detail | Same live read; 15-field PascalCase DTO; 200/404 | Includes Journey/transaction; PostgreSQL-authoritative direct projection; qualification uses separate receipt rather than source entity fields. Historical two-minute Redis cache behavior is retained only in RED chronology below |
| PUT ordinary update | Live `legacy.quotation-requests.update`; optional `X-Expected-Modified-Date`; 204/404/409 | Ten editable fields; server ModifiedDate; immutable Journey/transaction/CreatedDate and qualification projection/audit preserved |
| Done | No separate route | Nullable ordinary PUT field in source and target; Intranet validates nonnull Done before sending |
| DELETE | Critical live `legacy.quotation-requests.delete`; normal204/missing404 | Root-only delete; qualification FK prevents audited-root deletion; no request-file or create-receipt root FK |
| Qualification history | Existing #70 qualification receipt/transition routes | Ordinary triage must preserve projection/version/history; no new public history route invented |
| Outcome readback | Employee role plus live read; camelCase; no-store; inclusive/exclusive UTC window, maximum31days | PII-free current state; both Journey/transaction omitted when attribution is incomplete |

Source CRUD uses normal bearer authorization without target granular live policies.
Target live/critical admission is an intentional architecture boundary, not a
missing-source permission bug. These tests retain normal middleware/authorization.

Web only creates requests via `Legacy.Maliev.Web.Infrastructure/QuotationClient.cs`:
strict PascalCase201 plus Location and server-owned transaction/Journey validation.
No Web triage consumer was found. Intranet's
`Legacy.Maliev.Intranet.Bff/Quotations/QuotationRequestsProxy.cs` forwards sort,
search, index, size and exactly ten update fields with the expected timestamp.
`QuotationRequestsEndpointMapper.cs` maps downstream list404 to browser200 empty
page and preserves update409; the browser uses bounded pages and ordinary Done.
No request DELETE proxy/UI was found. These are inspection results, not live BFF
or browser execution evidence.

## Actual fixture and evidence

New tests use actual Production `Program`, normal generated RS256 JWTs, the pinned
registered `IamServiceClient` and normal permission handler, PostgreSQL18 and Redis7
Testcontainers. Only named remote IAM HTTP is controlled. It validates principal,
permission, live credential and bypassCache request fields. No IAM interface,
repository, authentication handler or quotation cache is replaced. Synthetic
fixture credentials/identities are not live IAM/session/provisioning acceptance.

Historical request/audit/file/receipt graphs are seeded via owned EF contexts in
disposable databases. A synthetic historical audit is not a newly authorized
qualification event. Existing joined Auth/#70 acceptance remains separate.

Baseline commands (before any new tests):

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-request-triage-20261001/TestResults/.triage-dependencies -nodeReuse:false
# Private dependency projects additionally built in Release, both0W0E.
$env:UseLocalMalievDependencies='true'
$env:MalievWorkspaceRoot='B:/maliev-legacy/.worktrees/quotation-request-triage-20261001/TestResults/.triage-dependencies'
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore -p:GITHUB_ACTIONS=false --logger trx --results-directory TestResults/triage-baseline-full
```

Baseline Release: zero warnings/errors. Baseline full: **372 passed, zero failed/skipped**.
TRX: `TestResults/triage-baseline-full/natth_MALIEV-31USFIV_2026-10-01_11_37_35_net10.0.trx`.

New test build/focus commands:

```powershell
dotnet build Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-request-triage-20261001/TestResults/.triage-dependencies -nodeReuse:false
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~QuotationRequestTriageHttpTests --logger trx --results-directory TestResults/triage-final-red
```

Chronology retained without rewriting earlier evidence:

- Initial compile failed on two xUnit2031 analyzer violations; only new-test
  Assert.Single predicate overloads were corrected. This is not runtime RED.
- First genuine focus:29 executed,27 passed,2 literal-search RED;
  `triage-first-red/...11_44_57_net10.0.trx`.
- Expanded focus:37 executed,33 passed,4 genuine RED;
  `triage-expanded-red/...11_46_45_net10.0.trx`.
- Redis fixture initially denied DEL only, but StackExchange.Redis used UNLINK.
  `triage-review-red/...11_48_26_net10.0.trx` has four genuine RED plus two
  fault-admission setup failures. No product cache conclusion derives from those
  two failures. Fixture now denies both deletion commands and restores both in finally.
- Latest fresh build:0warnings/0errors; focus **39 executed,35 passed,4 genuine
  RED,zero skipped**. TRX:
  `TestResults/triage-final-red/natth_MALIEV-31USFIV_2026-10-01_11_49_22_net10.0.trx`.

Passing controls: all six sorts, all eight text search fields, numeric ID/bounds,
out-of-range characterization/empty404/detail404, Done true/false/null with audit
and attribution preservation, stale/malformed expected version, concurrent CAS,
anonymous/live-denied admission, ordinary deletion/repeat/cache eviction and
file/receipt retention, audited deletion FK rejection with intact graphs,
readback privacy/no-store and six admission/window negatives.

## Genuine RED1: literal substring parity

The source predicate is `s.FirstName.ToLower().Contains(search)` (and the same
Contains shape for LastName, Message, TelephoneNumber, CompanyName, Email,
InternalComment, Country). It is literal substring search, not caller-controlled
SQL LIKE syntax. Target `QuotationRepositories.GetRequestsAsync` sets
`var pattern = $"%{value}%"` and passes it to `EF.Functions.ILike` unescaped.

Actual normalHTTP fixtures search a unique marker plus `%` or `_`, with one literal
row and one marker+X distractor. Both returned both rows; assertion rejects the
distractor. No expected ID is computed through target query code. This is not SQL
injection, a permission bypass claim or a request to change trimming/collation.

Smallest proposed repair, pending root approval: escape the chosen LIKE escape
character first, then literal `%`/`_`, and use explicit-escape ILIKE consistently
for the existing eight fields. Preserve numeric branch, trimming, sort, bounds,
permissions, DTOs and out-of-range status. Add literal escape-character/backslash
controls before repair. Do not quietly change pagination in this slice.

## Genuine RED2: receiptless mutation lost acknowledgement

`RequestTriageAckFault` throws once after actual executed UPDATE or DELETE via a
DbCommandInterceptor attached to normally configured options. It records
`command.Transaction is null`, fault reach and total executed mutation commands.
Fresh persisted request/receipt counts are read BEFORE response assertions.

- DELETE: autocommit true, fault reached, **two executions**, root0/receipt0,
  HTTP404 after durable first deletion.
- PUT with expected version: autocommit true, fault reached, **two executions**,
  root1/receipt0 with Message Reviewed, HTTP409 after durable first update.

These are genuine reached normal execution-strategy replay failures, not source
status parity. The proposed no-replay/503 assertions deliberately remain RED for
root design review. No create receipt proves either triage mutation, and global
create-key namespace is untouched.

Separate proposed runtime design, NOT approved/implemented: normally configured
outer execution strategy with fresh contexts and explicit transaction prevents
inner single-command replay; retry only after proven precommit rollback. Record
commit submission including asynchronous teardown, retain causes internally,
and use a narrow typed unavailable mapping for unproven receiptless outcomes.
Do not infer success from current row existence/absence or mutable ModifiedDate.
No automatic postcommit replay, retry disabling, new receipt/schema or global
exception classification. Before implementation, add known rollback, unretryable
failure, pre/postcommit cancellation, commit/disposal ambiguity and unchanged
semantic409/404 controls. Root must select the repair/policy boundary first.

## Actual cache-fault characterization (not a503 claim)

`DistributedQuotationCache` absorbs non-cancellation Remove failures. The earlier
repository-only concern of ordinary cache failure causing500 was incomplete.
Tests warm the real Redis detail cache, deny DEL+UNLINK on only the disposable
Redis default user, prove a real KeyDelete server denial, then run normal HTTP
PUT/DELETE. ACL is restored in finally; no persistent cache/config is touched.

Both cases passed: mutation204, fresh PostgreSQL update/deletion durable, but
next authorized detailGET returns200 old cached Message (including the deleted
root). This demonstrates the actual stale-cache window, not fake IQuotationCache
exception propagation. A freshness/eviction repair needs separate root-approved
design; these tests do not authorize changing existing availability policy.

## Explicit exclusions and gates

- No cascade/audit deletion, imported-data repair, root-ID lifetime policy or
  automatic orphan cleanup. Audited root500 is characterized, not declared intended.
- Source delete unit tests used EF InMemory and cannot establish FK outcomes.
- Out-of-range page200 vs source404 is characterized; Intranet handles both.
- No actor-bound/global key partition claim; its policy gate remains separate.
- No source ledger retirement, producer/consumer activation, IAM provisioning,
  schema application, AppHost, live browser or whole-#73 completion claim.
- Default qualification bridge configuration and ordinary Auth/refresh remain unchanged.
- Static/final affected-full results must be appended from executed terminal output;
  a baseline pass is not a pass for the deliberately failing new suite.

## Runtime gate and additional evidence (chronological)

Root created child #80 and approved three bounded repairs after independently
reading the four-RED focus, source/current predicates, consumer evidence and real
fault/cache observations. Parent #73 and retention/actor partition policy stay open.

- Unexcluded tests/design full: **411 executed,407 passed,4 failed,zero skipped**;
  `triage-unexcluded-full-red/natth_MALIEV-31USFIV_2026-10-01_11_52_01_net10.0.trx`.
  Original 39-test source SHA256:
  `56D97DEE018FFF5DF8B7FB628D2E81FF9D8D2A7665EBC837FED29884E2721F8B`.
- Before runtime: added literal backslash, desired authoritative detail under real
  Redis eviction denial, late cache-writer poison, explicit precommit rollback/
  fresh-context retries and separate real commit-ACK/disposal cases. Focus48:
  34pass/14fail, `triage-runtime-design-red/...11_57_35_net10.0.trx`.
- Added rollback-ACK/cleanup cause retention and caller cancellation boundaries.
  Focus56:34pass/22fail, `triage-safety-prerequisite-red/...11_59_27_net10.0.trx`.
  Many transaction callback failures here are explicit missing-transaction
  boundary evidence, not claims that the intended injected commit/rollback fault
  had reached. Mandatory Fired/count checks remain in the candidate.
- Four unretryable precommit cases:4fail, `triage-unretryable-red/...12_01_34_net10.0.trx`.
- First runtime Release0W0E, focus60:55pass/5fail;
  `triage-first-runtime-focus/...12_04_08_net10.0.trx`.
- Owned ReaderExecuted fault initially abandoned the returned reader before EF
  received it. Exact first-chance category diagnostic confirmed
  `NpgsqlOperationInProgressException`; diagnostic4:2pass/2fail,
  `triage-reader-lifetime-diagnostic/...12_06_56_net10.0.trx`.
  Only the fixture was repaired: it disposes its reader before synthetic throw.
  No assertion/retry option/production guard was weakened.
- Fresh Release0W0E, focus60:59pass/1fail;
  `triage-owned-reader-focus/...12_08_21_net10.0.trx`.
  Remaining failure is a new-test category error: InvalidOperation injected after
  executed UPDATE is SaveChanges-wrapped DbUpdateException, retaining middleware500,
  not the direct business400 assumed by that test.
- Exact synthetic-reference observer proved the wrapping: diagnostic4:3pass/1fail,
  `triage-wrapped-category-diagnostic/...12_11_30_net10.0.trx`.
  No raw causes/request values were logged; observer unsubscribed in finally.
- Added independent direct SavingChanges InvalidOperation400/no-write/no-retry
  control and missing-root outcome context-teardown503 controls:3pass/0fail,
  `triage-direct-business-controls/...12_13_01_net10.0.trx`.
  No global middleware unwrapping/classification change is proposed.

Implemented runtime contract, pending final review:

1. Eight existing text fields use explicit-backslash ILIKE escape after escaping
   backslash, percent and underscore. Numeric search, trimming, paging statuses,
   sorts and DTOs are unchanged.
2. Ordinary detail directly projects PostgreSQL without accepting cached request
   objects. Mutation eviction calls remain. Real cache-denial and late-writer
   tests now require fresh updated detail or404, not stale success.
3. PUT/DELETE execute in fresh contexts using the registered scoped options and
   configured outer execution strategy, with an explicit transaction. No-write
   semantic404/409 rolls back; transient replay is permitted only after positively
   confirmed precommit rollback. Commit submission is recorded before await and
   both transaction/context teardown are inside the guard, including no-write
   outcome teardown. Any unproven outcome is wrapped before the strategy can
   replay it and mapped only by these two actions to generic503 with code
   `request_mutation_unavailable`. No row-existence/ModifiedDate inference, new
   receipt/schema, disabled retry strategy, actor/key partition, blanket exception
   catch or create-path modification. Caller cancellation propagates. Original,
   rollback and replacement cleanup causes are retained internally via inner/
   aggregate exceptions, never returned to HTTP.

Unretryable original categories and audited-root FK behavior must remain intact.
Final focus/full/coverage/static/audit/security results are not yet claimed.

Root approved the precise new-test category correction after the diagnostic:
post-command PUT wrapped DbUpdateException remains500; separate direct
SavingChanges InvalidOperation retains400. No runtime/global unwrapping or old
test change was made.

The first candidate full after runtime completed **435 executed,434 passed,1 failed,
zero skipped** (`triage-candidate-full/...12_20_09_net10.0.trx`). The only failure
was the existing qualification replay fixture's implicit GET-to-cache warm-up
precondition, not its transition/replay behavior. Root approved explicit real-cache
seeding of the freshly read nonnull DTO, retaining every failure/replay/audit-ID/
timestamp/single-audit/final-eviction assertion. Qualification runtime is unchanged.
The new Redis eviction-denial test also now explicitly seeds and reads back the
real cached DTO before ACL denial, so stale-entry presence is proven independently
of the repaired ordinary GET behavior. Failed full artifacts remain preserved.

## Frozen candidate acceptance evidence

All commands below ran in this exclusive worktree with the exact private dependency
root above, sequential build/test/format outputs and no excluded test classes.
Root independent acceptance and protected integration remain pending.

- Fresh standalone test-project Release build: **0 warnings,0 errors**.
  `dotnet build Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-request-triage-20261001/TestResults/.triage-dependencies -nodeReuse:false`.
- Initial final triage focus: **63 passed,zero failed/skipped**,
  `triage-candidate-focus/natth_MALIEV-31USFIV_2026-10-01_12_16_40_net10.0.trx`.
- After both explicit real-cache fixture seeds: fresh Release0W0E, combined
  `QuotationRequestTriageHttpTests|QuotationEmployeeActorHttpTests` focus:
  **109 passed,zero failed/skipped**,
  `triage-final-focus/natth_MALIEV-31USFIV_2026-10-01_12_23_35_net10.0.trx`.
- Fresh unexcluded affected full: **435 passed,zero failed/skipped**,
  `triage-final-full/natth_MALIEV-31USFIV_2026-10-01_12_23_53_net10.0.trx`.
  Command: `dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore -p:GITHUB_ACTIONS=false --collect 'XPlat Code Coverage' --logger trx --results-directory TestResults/triage-final-full`.
- Whole solution `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore`: exit0.
- Unchanged checker `python scripts/check_owned_coverage.py TestResults/triage-final-full/e8c45a72-d8c2-4c6e-8172-bc2f546feeac/coverage.cobertura.xml --minimum 80`:
  **5171/5474 owned handwritten lines=94.46%,PASS**;
  API handwritten **415/469=88.49%**, generated API **0/214**, raw API **415/683**.
  Report SHA256 `EF60688C2EBB5DF85F1DF983A51D8530028CA3B368C3CE5EC9673495501E4006`.
  These denominators are the actual printed checker output for this exact report,
  not a replacement interpretation of historical reports.
- Coverage-checker unit suite: `python -m unittest discover -s scripts/tests -p 'test_*.py'`:
  **5 passed**. Its deliberately printed failing miniature coverage example is
  an asserted unit-test control, not failure of the real candidate report.
- `dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive --no-restore`:
  all six solution projects report no vulnerable packages against current NuGet sources.
- `gitleaks dir . --redact --no-banner`: no leaks.
- Exact workflow-pinned signing scanner from Workflows
  `73dd7304ffe85ec504389fd7664cc39070b9f148:scripts/JwtSigningResourceScanner.ps1`,
  loaded in memory and `Test-JwtSigningResourceMaterial` applied to tracked plus
  new candidate paths: PASS. No token/resource values printed.
- `git diff --check`: PASS. Existing route/DTO/permission and financial/qualification
  contracts passed in the unexcluded suite; only the explicitly approved real-cache
  setup changed in the existing qualification test.

All original RED/diagnostic TRXs and ignored private dependencies are preserved.
The two generated `scripts/__pycache__` folders are not candidate source: cleanup
was attempted after resolved absolute inside-worktree verification but rejected
by the tool's deletion policy; they remain untracked for root's scoped cleanup,
never staging. No commits or external writes were performed.

This proves disposable normal Production HTTP with generated ordinary RS256,
PostgreSQL18, Redis and actual registered IAM client over controlled remote
transport, not production IAM deployment, consumer activation, browser/Aspire
acceptance or cross-service atomicity. Audited-root deletion policy, receipt/file
retention, global idempotency actor partition and whole source-owner #73 disposition
remain explicitly outside this bounded #80 candidate.

## Root independent acceptance

Root inspected the complete six-file candidate, actual registered middleware and
strategy boundaries, every new test/fixture, and the narrowly adapted existing
qualification cache setup; all its failure/replay/audit/eviction assertions remain.
The full Release test-project graph used the private exact CI dependency pins
above with warnings as errors: zero warnings/errors. Root executed combined focus
109/109 and complete affected suite435/435, zero skips. TRXs are ignored
`TestResults/root-triage80-focus` (12:28:35) and `TestResults/root-triage80-full`
(12:28:42, duration2m18s). Whole format verification, six-project transitive
vulnerability audits, scoped/staged secret checks and diff checks accompany the
slice. Root separately executed the unchanged five coverage-checker tests and
validated the actual frozen Cobertura report:5171/5474=94.46%; generated214 API
lines remain separately visible rather than relabeled. Protected PR and exact-main
CI remain mandatory before #80 completion; #73/retention/actor-partition and all
production-derived Aspire acceptance gates remain open. No deployment or
persistent database/schema action is authorized by these disposable results.
