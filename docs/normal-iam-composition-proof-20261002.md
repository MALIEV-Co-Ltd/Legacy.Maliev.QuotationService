# Quotation95 normal IAM composition proof (reviewed runtime candidate)

Issue: https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/issues/95

Base: `5ac9a8c202b1189b910d5c4870d64bb5f820743c`. Exclusive workspace:
`B:\maliev-legacy\.worktrees\quotation-normal-iam-composition-20261002`.
Initial test/design ownership was this document and
`Controllers/QuotationNormalIamCompositionHttpTests.cs`. After root reviewed actual
RED, ownership additionally covers one call in `Api/Program.cs` and the new
`Api/Authorization/QuotationIamComposition.cs` helper. After the first full run,
root reviewed and authorized external workload-login preparation in five existing
fixture setups. Their existing assertions, statuses, claims, permissions and
denial modes are unchanged; each file has only two additive lines:

- `QuotationRequestTriageHttpTests.cs`
- `QuotationRequestCreateRetryHttpTests.cs`
- `QuotationFinancialOutcomeReadbackHttpTests.cs`
- `QuotationDraftAggregateHttpTests.cs`
- `QuotationDraftReadParityHttpTests.cs`

One line calls the shared test-only recording login helper in the NEW95 test file;
one adds supplementary outbound Bearer/RS256/`service:legacy-quotation` validation
to the unchanged IAM transport. No runtime candidate expansion or grant change.
Accounting48's six files and retained diagnostics remain frozen in its separate workspace.

## Reached boundary and limitation

The actual `Program` runs under Production with normal RS256 JWT validation,
PostgreSQL 18 owner EF migrations and Redis 7. No test registration of
`IIamServiceClient`, fake principal, authorization handler or authentication scheme
is allowed. Only the primary handlers of existing named external HTTP pipelines
are controlled. HTTPS origins are canonical; this exercises origin/pipeline
composition, not network TLS or deployed Auth grants. Incoming Accounting-shaped
JWTs and returned Quotation-shaped workload JWTs are synthetic, signed fixture
profiles, not a claim that deployed Auth authorizes those capabilities.

The retained HTTP cases require IAM reach before classifying a controller result.
Ordinary updates preserve money, provenance and acceptance. Historical full PUT
promotion must reach 409 and unchanged rows; it is not desired completion proof.
Accounting employee-intent must still fail after authorization; no audience,
subject or employee-capability substitution is proposed. Invalid JWT controls
must reject before either external HTTP boundary. Cancellation must occur after
observed live-check entry. Missing credentials must resolve the actual IAM client
and reject without sending. Each case seeds an independent row and host; the
class-owned database is serialized by xUnit, not shared with another worktree.

Before container creation, ambient Docker overrides and remote selected contexts
are refused. Explicit local endpoint is passed to both fresh uniquely labelled
containers, published only on 127.0.0.1 with tmpfs storage. No adoption, persistent
DB fallback, operational MigrationRunner, schema receipts or copied DDL. Owner EF
migrations affect only these disposable fixture databases. Private CI dependencies:

- Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`
- CompatibilityContracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`
- Accounting `ae0826156b06c34476e95de8c53dfccfcf5a5972`

## Observed design gap and root-authorized minimal candidate

Normal `Api/Program.cs` registers Legacy Auth token exchange and real JWT policies,
but neither `IIamServiceClient` nor `IAMService`. Defaults' permission handler treats
the absent optional IAM client as denied for forced-live checks. Existing producer
factories explicitly register this client and therefore cannot prove normal host
composition. This can produce 403 before controller/conflict/employee-intent logic.

The root-reviewed candidate adds scoped `IIamServiceClient -> IamServiceClient` and named `IAMService` using
the existing Legacy Auth authentication handler, service discovery and bounded
timeout. Do not use the older local-signing `AddIAMServiceClient` extension.
Origin policy rejects credentials/query/fragment/non-root paths, requires HTTPS
in Production, and allows loopback HTTP only in Testing/Development. The separate
`https+http` discovery scheme is restricted to `IAMService` and
`legacy-maliev-iam-service`, with no custom port. Configuration precedence is the
existing Defaults `Services:IAMService:BaseUrl`, then `Services:IAM:BaseUrl`, then
`Services:IAM`. Absent-only configuration retains the existing Defaults bounded
`https+http://IAMService` routing convention; explicit blank/malformed is rejected.
This preserves unchanged old factories' later HTTPS named-client configuration.
Unresolved discovery and absent live credential still fail closed; routing is
not an authorization fallback or fresh deployed IAM acceptance. Credential header
redaction is retained, redirects are disabled, and no resilience retry is added.
No change to live
checks, permission granularity, default-disabled qualification authority, financial
semantics, decision trust, claims or deployment grants.

Root reviewed complete initial tests, actual RED and the minimal runtime helper.
Root independent focused validation and unchanged old factories/suites remain
mandatory before integration. Accounting48 completion remains independently blocked by missing
employee-authority transport; accepted-late machine replay is not employee proof.

## Validation

Baseline solution and new test-project Release builds: zero warnings/errors.
The initial new-file build had five missing-namespace errors; correcting imports
produced the clean build. That compile diagnostic is not runtime RED.
Focused actual run: **14 total, 11 failed, 3 passed, 0 skipped**, no fixture failures.
Two DI assertions found a null IAM client. Nine cases failed their required
actual-IAM-reach assertion (the cancellation case completed the request before
IAM entry). Therefore no controller 409, employee-intent or canceled-live-check
claim is made. The three passing unsigned/wrong-audience/wrong-issuer controls
reached the normal validator's 401 and fresh PostgreSQL unchanged readback.
Original evidence: `TestResults/quotation95-normal-iam-red/normal-iam-composition-red.trx`.

Origin-only test-first run: **16 failed, 0 passed, 0 skipped**, all reached named
client contract assertions; `TestResults/quotation95-origin-red/origin-red.trx`.
First candidate: 29 passed, one NEW fixture failure because `UseSetting(null)`
represented a blank rather than absent configuration. Original diagnostic remains
`TestResults/quotation95-candidate/normal-iam-candidate.trx`. Corrected NEW fixture
omits null origin; explicit blank remains an independent negative.

Fresh candidate Release build: zero warnings/errors. Focused **33 passed, 0 failed,
0 skipped**, `TestResults/quotation95-normal-iam-green/normal-iam-green.trx`.
This now actually reaches live IAM and its real workload exchange handler, ordinary
controller update with fresh PostgreSQL readback, historical full-PUT 409, unchanged
employee-intent 403, and canceled live-check. The actual configured primary handler
is inspected with transport replacement disabled and redirects are false. HTTPS,
both case-normalized logical discovery hosts, absent discovery routing, local
Testing/Development HTTP, custom-port and all unsafe-origin negatives are reached.
No deployed TLS/grants or Accounting completion capability is inferred.

Scoped gitleaks scans of the new test and runtime helper are clean. The initial
synthetic fixed live credential triggered a generic-key rule; it was replaced with
an in-memory generated fixture credential also verified by the HTTP transport.
No scanner suppression was introduced. Final scoped solution formatting verifier
and `git diff --check` passed. Unchanged affected invoice HTTP suite:
**52 passed, 0 failed, 0 skipped**, including
`QuotationInvoiceConsumerContractHttpTests` and
`QuotationInvoiceDecisionHttpPostgresTests`;
`TestResults/quotation95-unchanged-invoice-http/unchanged-invoice-http.trx`.
The helper's bounded absent-origin routing keeps those two old fixtures unchanged.

First unfiltered coverage run: **573 total, 454 passed, 119 failed, 0 skipped**,
`TestResults/quotation95-full/quotation95-full.trx` (SHA256
`D494EFCEC6FA7C0CBBB20715A9EF552ACA9C0A9E046465F548188E36D12672DF`).
Failures mapped to seven classes but five fixture setups: Triage57, CreateRetry37,
FinderEnvelope3 (Triage), FinderEnvelopeCreate2 (CreateRetry), Financial11,
Aggregate7, ReadParity2. Those fixtures already supplied a real IAM client and
controlled IAM HTTP, but omitted actual workload-login credentials and transport.
The new authenticated IAM pipeline correctly refused before IAM. Existing create
and financial denied controls recorded expected IAM calls1 versus actual0; draft
readback recorded no live resource. No invented call counts for other fixtures.
This was incomplete fixture boundary preparation, not grounds to weaken runtime
authentication or change existing policy/expected outcomes.

Root read all five exact two-line diffs and the complete shared helper. The helper
generates a secret in memory, configures canonical synthetic HTTPS Auth origin and
`legacy-quotation` client ID, and overrides only the real named workload-login
external transport. It asserts exact POST/origin/path/body/no incoming bearer and
returns an RS256-signed synthetic quotation workload without permissions. It never
registers IAM, authorization or a principal. Existing transport-null negatives
remain; routing failures do not receive a synthetic allow. This is fixture
transport preparation, not deployed Auth grant/issuer acceptance.

Fresh test-project Release: zero warnings/errors. All seven affected classes plus
new composition tests: **185 passed, 0 failed, 0 skipped**,
`TestResults/quotation95-fixture-prepared-focus/fixture-prepared-focus.trx`.
Fresh unfiltered coverage run: **573 passed, 0 failed, 0 skipped** (2m36),
`TestResults/quotation95-full-green/quotation95-full-green.trx`, with actual
`5ae39b4c-a126-4849-b451-0d69c3f05394/coverage.cobertura.xml` attachment.
Root separately rebuilt and reproduced the 33-case normal HTTP gate before this
full run. Whole-solution formatting verifier passed. Transitive vulnerability audit
against NuGet.org reported no vulnerable packages for all six Quotation projects
and private Defaults, CompatibilityContracts, Accounting Data/Application/Domain.
Nine owned-file gitleaks scans passed without suppression; worktree whitespace
check passed. Exactly nine owned files were staged; cached whitespace (including
new-file EOF) and staged gitleaks checks passed. No commit has been created.

Final full TRX SHA256:
`E6AD87CE1CF142E79417DD24BCA4E06E0FF8F0D8685A598E3EACB3A6925EED7D`.
Coverage SHA256:
`E669578D02DB3B47AC0DD76F6FBB7E3D5B221DAAED487B672D67CFBCAB1DD3BC`.
Reported aggregate coverage includes transitive assemblies: 6404/14688 lines
(43.6%) and 1517/3854 branches (39.36%); not a claim of complete owner coverage.

Commands (executed from the exclusive workspace):

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-normal-iam-composition-20261002\.dependencies --verbosity minimal
dotnet build Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-normal-iam-composition-20261002\.dependencies --verbosity minimal
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj --no-build -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-normal-iam-composition-20261002\.dependencies --filter FullyQualifiedName~QuotationNormalIamCompositionHttpTests --logger 'trx;LogFileName=normal-iam-composition-red.trx' --results-directory TestResults/quotation95-normal-iam-red --verbosity minimal
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj --no-build -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-normal-iam-composition-20261002\.dependencies --collect 'XPlat Code Coverage' --logger 'trx;LogFileName=quotation95-full-green.trx' --results-directory TestResults/quotation95-full-green --verbosity minimal
$env:MalievWorkspaceRoot='B:\maliev-legacy\.worktrees\quotation-normal-iam-composition-20261002\.dependencies'
dotnet format Legacy.Maliev.QuotationService.slnx --no-restore --verify-no-changes --verbosity minimal
dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive --no-restore
```

The same `dotnet list <project> package --vulnerable --include-transitive --no-restore`
was also run separately on each private Defaults/Contracts and Accounting
Data/Application/Domain project; no packages or CI configuration were changed.

Root independently reviewed all five fixture-only diffs and the recording exchange helper, parsed the final573PASS/0FAIL/0skip coverage TRX and reproduced a fresh zero-warning/error Release build plus the entire573-case unfiltered suite. Root evidence: `TestResults/root-quotation95-full/root-full.trx`. The earlier root33 normal-composition focus also passed. Runtime origin/authentication remains unchanged by fixture preparation; existing assertions, permission decisions and financial/retry expectations were preserved. Final staged whitespace and secret scans gate the logical commit. This is not deployed Auth/TLS or Accounting employee-authority acceptance.

No source closure or deployment.
Frozen source mirror `bed10c7d15e0698e0b75f1329d0f312937f5d77f` remains read-only;
the source Startup's SQL/auth configuration is not restored by this proposal.
