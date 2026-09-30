# Quotation qualification employee-actor producer acceptance

This is the producer-only QuotationService #70 slice, based on exact green main
`3b27b4babd2a742aad29a29c6b7ff8beca1846bb`. It does not establish complete
Intranet qualification, live IAM, hosted deployment, source/data migration or
whole-source acceptance. The delegated writer had no commit or push authority;
root integration now owns the validated protected-main PR. Deployment, persistent
DDL/data writes, Auth/Intranet/shared-contract edits and external notifications
remain excluded.

## Approved identity and wire contract

The qualification audit actor is the authenticated employee's exact stable
subject, never a display name, email, client field/header or workload subject.
The normal JWT handler validates RS256 signature, issuer, audience and lifetime
without inbound claim mapping. Qualification requires one nonblank `sub` of at
most256 characters, a non-service subject and one exact
`identity_kind=employee`. Conflicting/duplicate stable aliases fail closed.
Role-only compatibility is not enabled. The existing forced-live
`legacy.quotation-requests.update` permission remains necessary.

PUT `/quotationrequests/{requestId}/qualification` retains its seven body fields:
State, Reason, Completeness, DuplicateCount, UnmatchedClassification,
IdempotencyKey and ExpectedVersion. Limits, types, nullability, normalization,
six states and 400/404/409 statuses are preserved. The qualification receipt
retains PascalCase/null-omitted serialization and does not expose retry keys or
request contact fields. Existing controller inventory is38 actions/39 templates.

The durable audit already contains required `ChangedBy` (maximum256) and unique
(RequestID, IdempotencyKey). Same employee plus same normalized transition
replays the original event; another employee reusing that key must receive409.
This actor binding is approved new hardening, not an assertion that the source
already enforced it. No column/migration is needed. Stale expected versions
remain acceptable only for a matching original replay; new transitions must
match the current version.

## Separately demonstrated runtime prerequisites

1. The positional qualification record put validation attributes on generated
   properties. Actual MVC model binding threw InvalidOperationException before
   the action, mapped by production middleware to400. The qualification-only
   metadata repair attaches those same attributes to constructor parameters.
   It neither suppresses model validation nor changes JSON fields or limits.
2. Production shared AddPostgresDbContext enables Npgsql retry execution strategy.
   The qualification repository's manual transaction was not inside that
   strategy; reaching the repository threw InvalidOperationException and400.
   The repair executes the complete atomic/advisory-lock/replay operation
   inside the configured strategy. Each invocation creates/disposes a fresh
   attempt context using the existing exact runtime options/data source and
   interceptors; it never replays tracked audit/projection state. Ambiguous
   commit outcomes reconcile against the durable key. Production retry is not
   disabled in the test host.

These prerequisites are distinct from the actor lookup and replay binding and
should remain separately explainable logical changes during Root integration.

## Source history and consumer dependency

Read-only source authority: isolated
`B:\maliev-legacy\.artifacts\source-commit-mirror-20260930.git`, checkpoint
`bed10c7d15e0698e0b75f1329d0f312937f5d77f`.

- `362308b605ff94878f684258ade46c67ae0b08ee` added Employee-role qualification PUT
  and receipt GET, employee audit actor, projection/version and immutable history.
  Relevant paths: `Maliev.QuotationRequestService.Api/Controllers/QuotationRequestsController.cs`,
  `Maliev.QuotationRequestService.Common/Models/QualificationStateUpdateRequest.cs`,
  `Maliev.QuotationRequestService.Common/Models/QualificationReceipt.cs`,
  `Maliev.QuotationRequestService.Data/Database/QuotationRequestContext/{Request,RequestQualificationAudit,QuotationRequestContext}.cs`.
- `99462c12c33fc62da281fc90fc61a8ee458d0d7a` removed the display-name actor fallback
  and receipt idempotency key, added restrictive audit FK, and replaced in-memory
  qualification proof with SQL Server integration tests.
- Source Intranet `Maliev.Intranet/Pages/QuotationRequests/View.cshtml.cs` obtains
  the actual employee token and forwards it for qualification. Target
  `Legacy.Maliev.Intranet.Bff/Quotations/QuotationRequestsProxy.cs` instead uses
  `LegacyServiceAuthenticationHandler`, replacing identity with the service token.

Current target Auth `RsaAccessTokenIssuer.Issue` emits employee sub +
identity_kind + granular permissions, but no Employee role. Typed employee
identity preserves source employee ownership under the approved target
architecture; it is not literal role-claim parity. Ordinary service tokens
remain denied, including service:legacy-intranet. Future verified employee
forwarding or qualification-specific delegation is a separate consumer/Auth
contract. Existing invoice delegation audience `legacy-accounting:invoice-create`
and Accounting scope are not reusable. No actor headers or service allowlisting.

## Validation evidence

Exact private ignored dependency clones under `TestResults/.dependencies`:
ServiceDefaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`; Contracts
`78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.

Baseline commands, before tracked edits:

```
dotnet build Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-employee-actor-20260930\TestResults\.dependencies
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-employee-actor-20260930\TestResults\.dependencies --logger "trx;LogFileName=quotation70-baseline.trx" --results-directory TestResults
```

Baseline: Release0 warnings/0 errors;237/237,0skipped,2m31s.
Tests use actual `WebApplicationFactory<Program>` in Production, normal shared
RS256 handler and real permission provider/handler; only IIamServiceClient's
external authoritative answers are controlled. PostgreSQL18 and Redis7 are
disposable Testcontainers, with distinct quotation/request databases. They are
not live IAM, Intranet end-to-end, deployed schema readiness or external data.

Initial production HTTP run:33 tests,23failed/10passed (`quotation70-red.trx`),
valid requests returned400 before actor lookup. A temporary Development
diagnostic exposed MVC's constructor/property metadata exception; Production
was restored, not replaced by a permissive Testing JWT environment.

After metadata repair:42 tests,16failed/26passed (`quotation70-actor-red.trx`).
Normal issuer-shaped employee requests now demonstrated403. All seven HTTP
annotation-limit cases passed. URI-bearing requests reaching persistence still
returned400. A separate temporary Development diagnostic exposed the configured
retry strategy/manual transaction mismatch (`quotation70-repository-diagnostic.trx`);
Production was restored immediately.

After both runtime prerequisites, `quotation70-isolated-actor-red.trx` recorded
2failed/9passed: normal employee200 expected/403 actual, and cross-employee
replay409 expected/200 actual. Seven annotation-limit and both retry-fault
regressions passed. The strict typed employee actor and durable ChangedBy
comparison were implemented only after those isolated failures were observed.

The replay cache-eviction regression was run with replay eviction deliberately
absent (`quotation70-cache-red.trx`):1failed/1passed. After a successful durable
write followed by a synthetic eviction error, the retry returned the receipt
but left the stale real Redis entry. Restoring replay eviction removed that
entry without another audit event. Caller cancellation during the atomic save
propagated and left projection/audit unchanged.

Final commands used the same exact private dependency root above:

```
dotnet build Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-restore -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-employee-actor-20260930\TestResults\.dependencies
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~QuotationEmployeeActorHttpTests|FullyQualifiedName~QuotationQualificationControllerTests|FullyQualifiedName~QuotationControllerContractTests" --logger "trx;LogFileName=quotation70-focused-final.trx" --results-directory TestResults
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-employee-actor-20260930\TestResults\.dependencies --collect "XPlat Code Coverage" --logger "trx;LogFileName=quotation70-full-final.trx" --results-directory TestResults/FinalCoverage
python -B scripts/check_owned_coverage.py TestResults/FinalCoverage/8c5ace6a-0a38-41a3-a26a-d987e19945f7/coverage.cobertura.xml --minimum 80
```

Final Release0 warnings/0 errors; focused88/88,0skipped (11s);
full283/283,0skipped (2m17s). The46 new component cases include real permission
denial/unavailability behavior, identity/alias/trust negatives, signed bearer
failures, constructor validation, forged actor inputs, expected versions,
same/cross-employee replay and concurrency, rollback, observed transient-save
and lost-commit faults, cancellation and real Redis postcommit cache recovery.
Only the external IAM answers are synthetic. Fault tests explicitly verify the
fault was injected; a missing test interceptor cannot masquerade as retry proof.

The unchanged checker passed4903/5195 owned handwritten lines (94.38%, floor80).
API handwritten311/364 (85.44%), generated obj0/214, class-entry raw311/578.
Coverage file SHA256:
`5E35769BB44EF6298309557354F8008D2804A87CCDBA66DC7EEC77F225BA1EF4`.
Use executed checker denominators, not independent XML arithmetic or an old raw
package percentage. This does not establish all untouched endpoint lifecycles.

Static checks: scoped `dotnet format --no-restore --verify-no-changes` with the
dependency root in MalievWorkspaceRoot passed; `dotnet list` solution packages
with `--vulnerable --include-transitive --no-restore` reported no vulnerable
packages in all six owned projects. Coverage checker tests5/5 passed (the
intentional failing-gate fixture prints a failure message). Redacted Gitleaks
scans of the tracked diff, new component test file and this document found no
leaks. `git diff --check` passed. No coverage checker/policy was changed.

The existing direct-controller success fixture now uses canonical sub plus
employee kind; its display-name negative and the new actual HTTP URI-only
negative remain. No assertion on authorization, version, payload replay or
persisted state was removed. No other production registration or auth policy
was replaced by the component fixture.

## Remaining gates and excluded work

Independent root integration checks: Release build0 warnings/0 errors, focused54
and full283 passed with zero failures/skips (full2m24s). Root inspected the complete
runtime, DTO, direct-controller and actual HTTP fixture diffs. Whole-solution
format verification, all six projects' transitive vulnerability audit, unchanged
owned-coverage checker94.38%, diff and complete changed/new text secret checks
passed. This is independent producer acceptance, not an executed Intranet chain
or production-derived Aspire proof. Required exact-head and post-merge main CI
remain integration gates.

Intranet employee-token forwarding or qualification-specific delegation remains
unimplemented. No whole #70/consumer-chain acceptance is claimed. Root owns
protected review, integration, exact-main CI and deployment/runtime acceptance.
Shared Auth issuer/security review, live IAM and persistent schema/data are not
covered by this slice.

Untouched request operations still use manual transactions without an outer
execution strategy: CreateRequestAsync, CreateRequestIdempotentlyAsync and
CreateRequestFileIdempotentlyAsync. This is a static sibling finding, not an
executed failure of those endpoints, and requires separate tracking/acceptance.
No assertion that quotation decision has this same explicit-transaction pattern.

Python checker unit tests generated untracked scripts/__pycache__ and
scripts/tests/__pycache__ entries. The safety-checked cleanup command was blocked
by tool policy, so they are left as transient artifacts, explicitly excluded
from any producer commit/PR. The only intended new tracked files are this
document and QuotationEmployeeActorHttpTests.cs.
