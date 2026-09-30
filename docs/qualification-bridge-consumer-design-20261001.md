# Qualification live authority consumer: issue 76, child of 70

## Scope and gates

Base Quotation main 073ba64a2f60bcbb17d0870925d5394b202ef662. Initial TEST/DESIGN gate completed before runtime approval. Root subsequently released the listed Quotation-local runtime files and shared actor helper after Auth bridge PR116 exact main 8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0, CI36763076139 SUCCESS/publishSKIPPED. Root owns protected integration. Configuration activation, grants, schema/store, Auth/Intranet/AppHost, modern IAM, original source, GitHub, commit, provisioning, deployment and live-data changes remain prohibited. Root also approved only the two existing authorization-metadata snapshot assertions/census changes; all other old tests remain unchanged.

Private ignored dependency root TestResults/.bridge-dependencies uses exact CI Defaults 8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3 and Contracts 78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7; not latest canonical. Existing route/DTO snapshots and atomic qualification repository remain unchanged.

## Demonstrated boundary

Actual Production Program and normal RS256 authentication admit the synthetic employee identity, but shared RequirePermission(RequireLiveCheck=true) executes before the controller. Program registers no IIamServiceClient; forced-live therefore denies. Registering a bridge client without changing this endpoint-local authorization metadata cannot reach the action. Never globally replace IIamServiceClient or change shared Defaults authorization.

New QualificationBridgeConsumerHttpTests leaves normal authentication/authorization DI intact, uses PostgreSQL18/Redis and only controls named external HTTP transport. Initial allow tests for GET /quotationrequests/{positiveId}/qualification-receipt and PUT /quotationrequests/{positiveId}/qualification expect200 and observe403. Default-off signed-permission controls observe403 and no projection/audit writes. Synthetic employee sid is a transport fixture binding, not evidence of a real persisted Auth session or live IAM acceptance.

## Approved minimal runtime boundary

Only two positive-ID actions receive a new endpoint-local qualification authorization attribute/filter. Replace their shared RequirePermission metadata with the local gate, not an additional action check behind the existing gate. Retain controller Authorize and all normal JWT middleware. Correction approved by root: disabled consumer delegates EXACT original forced-live policy via existing IAuthorizationService, new RequirePermissionAttribute(permission){RequireLiveCheck=true}.Policy and HttpContext resource. Normal production DI without IAM still denies403; genuinely configured live-IAM behavior is preserved. This is never claims fallback or an enabled bridge-fault fallback. Enabled consumer requires exact immutable employee actor for both routes, then remote authority. PUT's independent existing actor check/normalization remain; shared actor helper preserves exact old extraction semantics/callsite. No role/alias compatibility expansion or internal API test scaffolding.

Runtime files:

- Api/Program.cs: register local boundary/client/options only.
- Api/Controllers/QuotationRequestsController.cs: authorization metadata only on receipt and update; no wire/persistence edits.
- New Api/Authorization/QualificationAuthorityOptions.cs: default-off Enabled; bounded request-current configuration.
- New Api/Authorization/QualificationAuthorityFilter.cs and attribute: exact employee, positive route ID, read/update purpose, explicit admission before action.
- New Api/Authorization/QualificationAuthorityClient.cs and contract/interface: bounded remote transport and strict response binding.
- New Api/Authorization/QualificationEmployeeActor.cs: verbatim semantics extracted from existing actor method; no principal fallback.

Other forced-live routes remain unchanged, including ordinary CRUD and aggregate qualification-outcomes/readback. The aggregate has no positive requestId and is OUTSIDE this bridge; do not fabricate an ID or reinterpret a global scope as request-specific authority. No source per-request ACL is invented: the authority preserves the uniform employee qualification read/update policy. Resource/purpose bind the decision to the particular caller operation, not a new DB grant resolver.

## Frozen producer wire/trust

POST /auth/v1/introspection/quotation-qualification; outer ordinary Auth-issued workload Bearer service:legacy-quotation. Reuse existing AddLegacyAuthServiceTokenExchange/ILegacyServiceAccessTokenProvider through an explicitly selected HTTP client; no modern AddAuthServiceIAMClient. The latter uses a different modern IAM transport/configuration and is not this endpoint.

Request contains exactly camel-case employeeAccessToken, permission, purpose, requestId. EmployeeAccessToken is the normal validated incoming bearer, never an actor header, browser-exposed token or workload substitute. Permission is exactly legacy.quotation-requests.read or legacy.quotation-requests.update. Purpose is quotation-request-qualification; requestId positive Int32. Employee token max16384 characters; producer body cap24576 bytes. Never send oversized input or token/exception details to logs.

Response200 has exactly allowed:boolean, subject:string|null, permission:string, purpose:string, requestId:Int32, explicit null on denial. Require all five fields exactly once with correct types and no unknown fields; reject duplicate fields, truncation, trailing JSON, over-limit body, absent/null echoes, wrong subject or resource/purpose/permission. Approved consumer response cap4096 bytes, both declared and streaming without Content-Length. allowed=true requires subject exactly equals locally validated employee sub; allowed=false requires subject=null and produces403. Malformed responses cannot authorize.

Approved status mapping: normal invalid/missing incoming JWT401; valid nonemployee/ambiguous actor403; disabled exact old policy (normal no-IAM403); nonpositive ID400; valid remote denied200=>403; Auth401/403=>403; Auth404/429/503/network/body-I/O/timeout or malformed response=>503, generic/no upstream details. Never copy untrusted Retry-After. Explicit10-second HttpClient timeout plus one linked10-second overall deadline covering streamed body as well as headers; ResponseHeadersRead alone does not bound body. No retries or automatic positive replay. Caller cancellation propagates rather than being reclassified unavailable. Existing repository404/409 and seven-field validation400 unchanged after admission.

No positive authority decision cache across requests, including idempotent write replay. Replays must reauthorize live before reaching unchanged durable ChangedBy/payload/version reconciliation. Short-lived workload-token cache is not an authority-decision cache. No signed permission fallback, service allowlist, invoice delegation reuse, customer/service employee admission or new grants.

## Transport tests and sequence

1. Exact-pin Release0W0E then existing actor focus baseline. New normal-DI HTTP allow RED for both routes, plus disabled signed-grant/no-write controls; preserve TRX.
2. Completed controlled normal service-login HTTP fixture via existing provider: fixture-only credentials, signed workload token and exact dedicated capability. Both allow routes verify exact outer workload bearer differs from employee token, exact employee JWT appears only in four-field body and no Authorization on service-login exchange. No replacement/mock IIamServiceClient, auth handler, actor validator or repository. Additional root-approved fixture-only actual pinned IamServiceClient registration with normal dependencies and controlled remote IAM HTTP proves disabled allowed200/enabled bridge-deny403, with zero old IAM calls on enabled denial. No production IAM registration or real external credentials/traffic.
3. Implement only corresponding local admission and bounded client. Keep old tests intact; review intentional enabled receipt employee-only boundary separately. Test read receipt privacy, PUT exact seven-field wire, stored subject, version/xmin, same-actor replay, cross-actor409 and payload conflict using real DB.
4. Before each additional repair, observe intended RED for wrong response subject/permission/purpose/id, missing/extra/duplicate/type/null fields, oversized streamed body, disabled/unavailable/denied authority, service/customer/alias/conflicting/old-unbound/expired input, cancellation and fresh authorization on replay. Every denial verifies unchanged ModifiedDate/projection/version and zero new audit rows; permitted read verifies no writes.
5. Preserve ordinary and aggregate forced-live denial with consumer enabled, proving no global bypass. Test no positive authority cache: allow first, then deny second even same token/key; no write on second. Controlled answers prove consumer behavior, not producer revocation or live IAM.
6. Separate integrated acceptance after both protected mains green: actual Auth Program ordinary service-login + independently signed employee JWT/session rows and current capability reload, revoked family/stamp/email/lockout, strict final expiry. No actual credential provisioning or configuration activation implied by fixture acceptance.
7. Sequential final Release/focus/full/static/format/audit/gitleaks/unchanged coverage checker and route/DTO snapshots. No parallel same-output commands. A RED test/design deliverable is not a completed runtime slice.

## Configuration readiness and ownership

Quotation consumer owns local client/gate and default-off QualificationAuthority:Enabled, with Services:Auth:BaseUrl (or existing Services:Auth discovery), ServiceAuthentication:ClientId=legacy-quotation and runtime-only ClientSecret; existing Jwt:PublicKey/Issuer/Audience must match producer's ordinary issuer trust. Safe origin admits only https/http/https+http discovery, no userinfo/query/fragment or non-root path;10-second timeout. No public activation/credential configuration changed. Request-current IOptionsMonitor reads enabled state; configured malformed options/origin fails unavailable, never authority allow.

Auth owns independently validated employee JWT, bound sid/current session/family/identity checks, current request-reloaded ServiceClients configuration and default-off QualificationIntrospection:Enabled/PermitLimit (30 per60s zero queue). AppHost currently gives legacy-quotation only legacy.order-status.write; dedicated legacy-auth.quotation-qualification.introspect is NOT provisioned. Its grant and activation are separate security/readiness gates, not consumer implementation work. No removal of existing ordinary grant or new employee grant.

Intranet owns separate qualification-only employee token forwarding from existing server-side distributed ticket/EmployeeSessionService, not service-authenticated QuotationRequestsProxy's unconditional workload replacement. Current canonical Intranet2589c562815bbdea394a72e416325be220031b4e and AppHost55d80acfeebf58d17a07f8d3226cc82a8dd38d79 remain read-only; recovery mapper ownership stays separate. Preserve cookie/antiforgery, refresh owner checks, session storage and no browser token disclosure. #70 remains open until consumer and orchestration/runtime acceptance.

## Source traceability and TOCTOU

Readonly committed source mirror checkpoint bed10c7d15e0698e0b75f1329d0f312937f5d77f and Workflows migration/source-commit-ledger.json:

- 362308b605ff94878f684258ade46c67ae0b08ee: QuotationRequest controller, seven-field update/receipt models and audit/projection; SQL belongs DataMigration, never runtime SQL here.
- e78ab85594e688aed223f54ef31c7b6df399a735: Intranet Pages/QuotationRequests/View.cshtml(.cs), actual employee access-token forwarding.
- 99462c12c33fc62da281fc90fc61a8ee458d0d7a: immutable identifier/receipt privacy and persistence hardening.
- d852d3ef0ea45bba51bb29de557784b5e5fffae6 and da58002047bcea0d00d0769deb99173ef21bdf81: aggregate readback, separate from positive-ID bridge scope.
- 8e133abe65078c6a0bbca1d43473c6546ed6284f: Workflows scorecard validation.
- 92f2263531971457cd9d71da41a653e91a1b6079 and 3e38f9691b1c502755f1ab2b00ed90f8261eafef: Intranet aggregate receipts/validation, separate consumer scope.

Source employee role/identifier intent is preserved through reviewed target typed identity/live session authority; the dedicated bridge and cross-actor replay hardening are approved target architecture, not literal original endpoint behavior. Ordinary Auth/refresh remain unchanged.

Point-in-time only: Auth session/identity reads and Quotation commit are separate databases/processes. A revocation/stamp/lockout change committed before the relevant authoritative read denies; an in-flight allow can race a later change before Quotation commit. No atomic cross-database revocation claim, identity positive cache, SQL workaround or outbox/transaction policy is invented. Characterize this limitation in acceptance; stronger guarantees require a separate architecture gate.

## Evidence at test/design gate

dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=<owned>/TestResults/.bridge-dependencies: baseline and new-test builds terminal0W0E.

dotnet test <Tests.csproj> -c Release --no-build --no-restore --filter FullyQualifiedName~QuotationEmployeeActorHttpTests --logger trx --results-directory TestResults/bridge-baseline-focus:46pass0fail0skip.

New bridge focus initial TRX TestResults/bridge-initial-red/natth_MALIEV-31USFIV_2026-10-01_02_13_23_net10.0.trx:2 intended allow failures expected200/actual403;2 disabled controls pass;0skip. Confirmed latest Release0W0E plus TestResults/bridge-confirmed-red/natth_MALIEV-31USFIV_2026-10-01_02_17_17_net10.0.trx repeats2fail2pass0skip after enabled-denial DB assertions: both denied allow cases pass unchanged projection/version/ModifiedDate and empty audit checks before the intended200/403 assertion. No runtime repair authorized or performed. Full suite/coverage/runtime security acceptance deliberately deferred until runtime gate; baseline focus and intended RED are the bounded gate, not a passing feature claim.

Scoped format first identified new-file whitespace; formatting only that owned file repaired it. Fresh final Release0W0E then TestResults/bridge-formatted-red/natth_MALIEV-31USFIV_2026-10-01_02_18_59_net10.0.trx retained2 intended403failures/2pass/0skip. Scoped dotnet format --no-restore --verify-no-changes --include <new test file> terminal0 and git diff --check passed. Only the two new owned files are untracked changes; no runtime/old tests changed.

## Runtime verification chronology (supersedes initial gate status above)

- Completed service-login/provider fixture RED:2fail2pass; expanded response/input boundary RED:12fail2pass, expected200/503 versus old403.
- Minimal client/local gate GREEN:60focus (14new +46unchanged actor).
- Slow body RED: caller15s timeout instead of503 because ResponseHeadersRead does not bound body; paired actual pinned IAM compatibility proof passed. Linked overall10s deadline repair =>16new focus GREEN.
- Broken streamed body RED:1fail15pass, expected503/actual500 after IOException. Narrow body-I/O unavailable repair; expanded focus75 GREEN.
- Deliberate metadata RED:32pass2fail. Root approved only census and two qualification assertions:38methods39routes, exactly2local attributes/exact permissions/purpose/int requestId, original attributes on36others; aggregate live Employee unchanged. No inheritance/null-policy camouflage.
- Cache-header RED:5fail0pass. Enabled local filter now sends Cache-Control:no-store and Pragma:no-cache for its allow/deny/unavailable responses, before action/model execution. Ordinary/aggregate behavior unchanged. Normal JWT middleware401 occurs before local admission; ordinary middleware behavior is not reordered.
- Diagnostic focus117pass1fail and full319pass2fail preserved, not acceptance: expired fixture assumed30s skew but exact Quotation Defaults8f uses5MINUTES; Auth5c uses30SECONDS and its bridge additionally denies at exact final fresh JWT/session expiry. Fixture401 case now expires more than5min ago; no normal validator or skew changed. Second full failure was eager bridge client resolution in old minimal default-off host; client is now resolved only enabled, preserving its old live policy without changing that fixture. Diagnostic full was mistakenly started before noticing failed focus; explicitly excluded from acceptance.
- Fresh Release0W0E then focus121pass0fail0skip (38bridge +46actor +34contract +3oldreadback). Fresh full321pass0fail0skip (283baseline +38new): TestResults/bridge-full-green-coverage/natth_MALIEV-31USFIV_2026-10-01_03_07_03_net10.0.trx.
- Unchanged coverage checker executed separately, exit0 against EXACT TestResults/bridge-full-green-coverage/c28eefe1-dfce-477b-beaa-438123f98894/coverage.cobertura.xml: owned4984/5281=94.38%; API handwritten392/450=87.11%, raw392/664. Coverage checker tests5pass (their intentionally failing fixture output is not candidate coverage failure). Full solution format verify terminal0, package vulnerable audit six owned projects none, gitleaks owned changed/new content no leaks, exact CI Workflows73dd7304ffe85ec504389fd7664cc39070b9f148 JwtSigningResourceScanner tracked+new files PASS, diff/owned whitespace checks.

Controlled authority transport does NOT prove real Auth session validation. No whole #70 chain/runtime acceptance claimed.

## Joined exact-pin real Auth HTTP fixture (separately approved tools/test authority)

In-process dual WAF aliases are rejected: Auth and Quotation pin different builds of the SAME Defaults assembly name; one version could silently serve both. Plan requires separate process/build/output for Auth and its EF fixture tool, with real localhost HTTP to normal Quotation WAF. No new runtime, old-test/CI or public API scaffolding changes.

Proposed NEW owned projects/files only:

- tools/QualificationAuthorityAuthFixture/QualificationAuthorityAuthFixture.csproj + Program.cs: executable referencing ONLY private exact Auth Infrastructure; EnsureCreated and seed synthetic employee/customer identities through existing EF models/PasswordHasher into disposable PostgreSQL18, plus fixture-only security-stamp mutation command if needed. No production connections, hand-written SQL schema, session/grant invention or Auth edits. Sessions are issued by actual Auth login/refresh, not fabricated by this tool.
- tools/QualificationAuthorityJoinedTests/QualificationAuthorityJoinedTests.csproj + QualificationAuthorityJoinedHttpTests.cs: standalone xUnit/Testcontainers/WAF project referencing ONLY this Quotation API/EF models. Not added silently to existing solution/CI. Runtime invokes the separately built EF tool and Auth API process; existing solution coverage remains distinct.

Private ignored roots: TestResults/.joined-auth/Legacy.Maliev.AuthService exact8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0, TestResults/.joined-auth/.dependencies/Defaults exact5c5f9479313710fa576f83d3b396442997a2fcf4 and Contracts78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7. Quotation remains TestResults/.bridge-dependencies Defaults8f4f5f/Contracts78. Local git object clones/checkouts only, no external fetch. Verify physical Defaults DLL hashes in each separate output against its private pinned build; no assembly unification/alias workaround.

Sequential commands, with ROOT=this owned worktree, AUTH=ROOT/TestResults/.joined-auth/Legacy.Maliev.AuthService and AUTHDEPS=ROOT/TestResults/.joined-auth/.dependencies:

1. dotnet build AUTH/Legacy.Maliev.AuthService.Api/Legacy.Maliev.AuthService.Api.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=AUTHDEPS
2. dotnet build tools/QualificationAuthorityAuthFixture/QualificationAuthorityAuthFixture.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=AUTHDEPS -p:QualificationAuthSourceRoot=AUTH
3. dotnet build tools/QualificationAuthorityJoinedTests/QualificationAuthorityJoinedTests.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=ROOT/TestResults/.bridge-dependencies
4. Supply fixture-only absolute Auth API/tool DLL paths to joined test process; dotnet test tools/QualificationAuthorityJoinedTests/QualificationAuthorityJoinedTests.csproj -c Release --no-build --no-restore --logger trx --results-directory TestResults/bridge-joined-auth

Fixture owns PostgreSQL18+Redis, three Auth stores and separate Quotation/QuotationRequest DBs. Tool seeds before Auth starts. Child ProcessStartInfo uses dotnet/private Auth API DLL and loopback URL; all generated keys, credentials and DB connections passed only via child environment, never CLI/log output or public configuration. Activate bridge/capability ONLY in ephemeral fixture configuration; unchanged normal JWT issuer/audience and real credential exchange. Bound child output retained privately in memory, sanitized failure categories only. Readiness polling is bounded and asserts actual normal Auth health before testing. Disposal kills ONLY captured fixture-owned child PID/tree after verified exact start path, awaits exit and drains output; disposes hosts/contexts before containers, clears only owned pools if needed. No ClearAllPools or other-process/container cleanup.

Joined cases: actual POST/auth/v1/login employee password response gives persisted sid; normal Quotation provider POST/auth/v1/service/login then actual bridge independent employee validation; read/update stores exactsubject and immutable audit, sameemployee samekey replay no duplicate; actual refresh rotates sid and consumed-unrevoked old token remains valid until strict expiry; actual POST/auth/v1/revoke family makes subsequent read/update/replay403 without writes. Separate committed stamp change via EF tool denies next request. Add expired-within-Quotation5min-skew employee token (normal Auth-signed bound identity fixture) to prove Auth bridge exact expiry denies even though Quotation ordinary validator admits. No synthetic authority response or IIam replacement in joined project.

The two new standalone projects were explicitly approved and implemented without modifying the solution or CI. The EF tool accepts only loopback `joined_` disposable databases and migrates the existing exact Auth models; it seeds a synthetic employee identity but never fabricates a refresh session. Actual normal password login creates the row/sid. Separate process outputs verify exact Auth8cdb/Defaults5c and Quotation Defaults8f pins, both Contracts78 pins and separate Defaults DLL hashes. Child cleanup is restricted to the owned process identity/DLL, with redacted bounded capture. All fixture credentials and generated RSA private keys are environment-only synthetic material.

Preserved setup failures, not security RED evidence: `TestResults/bridge-joined-auth-initial` 3fail because normal Auth JwtOptions rejects a60-second lifetime (reviewed range300..1800); `TestResults/bridge-joined-auth-minimum-lifetime` 3fail because global readiness remains503 with unrelated EmployeeRecovery disabled; `TestResults/bridge-joined-auth-targeted` 3fail because the new fixture initially seeded UTC into the existing timestamp-without-time-zone request fields. No issuer/health/store runtime policy was weakened. Correct fixture uses the normal minimum300-second lifetime, unspecified legacy request timestamps and root-approved targeted admission: live child/liveness, normal employee-login200 and authenticated service-login200. It independently asserts global `/auth/readiness`503 and `auth_employee_recovery_schema` Unhealthy, without enabling recovery. Global readiness/Aspire acceptance remains a separate unresolved gate.

First actual joined boundary pass: `TestResults/bridge-joined-auth-real-boundary/natth_MALIEV-31USFIV_2026-10-01_03_41_30_net10.0.trx`, 3pass0fail0skip in5m6s. Exact expiry proof waits for a genuinely issued300-second JWT's actualexp+250ms, no forged claims/sid or clock override. This is point-in-time authority evidence, not atomic identity/session plus Quotation commit, not production activation, not yet a protected CI gate. Root must independently wire and verify the portable joined gate before claiming protected joined acceptance.

## Primary-handler redirect boundary

Normal primary HttpClient transport was tested using two owned real loopback Kestrel origins, not a controlled named authority handler. Before repair307 and308 each forwarded the employee JWT POST body to the second origin (`TestResults/bridge-redirect-real-red`, 2fail0pass). The qualification-authority client's existing primary SocketsHttpHandler/HttpClientHandler now sets AllowAutoRedirect=false, without replacement/global pool/TLS changes. Both regressions then pass and return generic503 with no qualification audit write (`TestResults/bridge-redirect-real-green`, 2pass0fail). All non200 redirect responses remain unavailable; tokens are never followed to another origin. The earlier321full/121focus/coverage above predates these two tests; final acceptance must use a fresh323full and corrected123focus.

## Final local candidate and portable gate

Independent root review found that the earlier 323-test run preceded the new
joined CI job. Root's first composed run passed 322 and failed the existing
workflow contract that permitted only `validate`; it was not accepted. The
initial root focused filter also ran 122 rather than 123 because it omitted one
existing receipt control. Both diagnostics are retained, not substituted for
final acceptance. Root expanded only the workflow contract to require exactly
the existing validation job plus the unconditional eight-step joined job, with
all five immutable checkout pins, isolated dependency paths, read-only permissions,
the existing setup action, bounded timeout and exact reviewed script. Nine new
mutation cases reject renamed/conditional/soft-failing jobs, moving pins, shared
output paths, SDK drift and skipped/replaced commands. Existing validation,
coverage threshold/collector and secret/permission guards remain enforced.
Release zero warnings/errors, workflow 27/27, corrected combined focus 150/150
and full 332/332 all passed with zero skips. Root's exact coverage checker exited
0 for `TestResults/root-final-full/f31ed0ac-2005-4fa7-9b46-e4605fd8fa04/coverage.cobertura.xml`:
API handwritten 395/455 (86.81%), owned 4987/5286 (94.34%). The checker's five
unit tests also passed; their deliberately failing fixture output is not a
candidate coverage failure. Root independently ran the portable joined script:
all three isolated Release builds had zero warnings/errors, three actual tests
passed with zero skips in 5m4s, script exit 0. Exact report:
`TestResults/bridge-joined-script/40d84f50abec42cab711f21a801117c8/natth_MALIEV-31USFIV_2026-10-01_04_17_17_net10.0.trx`.
Whole solution format verification also passed. Protected PR and exact-main
acceptance still remain required; global recovery readiness and Aspire are not
inferred from this real joined boundary.

Fresh final main Release build0warnings0errors; corrected focused run123pass0fail0skip (`TestResults/bridge-final-focus123/natth_MALIEV-31USFIV_2026-10-01_03_55_52_net10.0.trx`); full323pass0fail0skip (`TestResults/bridge-final-full323-coverage/natth_MALIEV-31USFIV_2026-10-01_03_57_02_net10.0.trx`). Unchanged checker executed against exact `TestResults/bridge-final-full323-coverage/46febb63-e421-44f6-831a-4f6d2a9ef4c9/coverage.cobertura.xml`: owned4987/5286=94.34%, API handwritten395/455=86.81%, API raw395/669, exit0. Previous321 report is historical, not substituted. Forty new bridge cases include both real redirect regressions. Main full-solution and both standalone owned-project scoped format verifications exit0. Exact CI pinned signing scanner73dd7304ffe85ec504389fd7664cc39070b9f148 passed. Owned gitleaks initially classified the public Auth8cdb pin's inline method context as generic-api-key; naming the unchanged immutable producer revision removed that false positive without any scanner suppression/allowlist. Rerun found no leaks.

Root approved `scripts/run_qualification_authority_joined.ps1` plus unconditional `qualification-authority-joined` job in the existing reusable build workflow. Existing validate/coverage job is unchanged. Five clean source checkout pins are required before sequential Auth API5c, fixture tool5c and joined Quotation8f Release builds with warnings-as-errors; missing checkout/build/test/report fails, never skips. Each existing checkout action SHA is retained; setup-dotnet uses exact same action SHA as the reviewed Workflows73dd CI action. Job timeout20minutes; test hang timeout8minutes with no memory dump. Runner requires exactly3executed/3passed/0failed/0notExecuted in the unique TRX. Caller GITHUB_ACTIONS environment is restored in finally, and no secrets/environment values are printed. Local entry point: `pwsh -File scripts/run_qualification_authority_joined.ps1 -RepositoryPath <absolute-owned-checkout>`; no manual aliases or assembly unification.

Standalone final post-bounded-output proof also passed3/3, zero skips (`TestResults/bridge-joined-auth-final/natth_MALIEV-31USFIV_2026-10-01_03_49_10_net10.0.trx`,5m5s). Workflow YAML parsing and unconditional five-pin job structure passed locally. Protected CI at the eventual same PR SHA and whole-service/Aspire readiness remain root-owned acceptance gates; local proof does not assert them. No commits, pushes, GitHub mutations, production credentials, schema changes or activation performed.

Portable runner executed locally to terminal exit0 after its final edits: all three sequential isolated Release builds0warnings0errors, actual3executed/3passed/0failed/0notExecuted (`TestResults/bridge-joined-script/31991a2fc36e41958865e45f47aa1779/natth_MALIEV-31USFIV_2026-10-01_04_01_35_net10.0.trx`,5m3s). Missing-checkout negative control fails before any build. PowerShell parser syntax passes. Parsed YAML comparison confirms the old validate/coverage job is exactly preserved (native git output normalized with its terminal newline); new job is unconditional with all five refs. Fresh NuGet vulnerable audits: six main solution projects plus both new standalone projects, none. Generated Python coverage-test `scripts/__pycache__` and `scripts/tests/__pycache__` remain unstaged; approved scoped cleanup command was denied by the execution policy, not bypassed. Root owns their cleanup before staging, all ignored failure/acceptance evidence is retained.

Cross-database race remains point-of-check, not atomic. This separate project's execution cannot be claimed covered by protected main CI until root explicitly wires that gate. Source/infrastructure provisioning/AppHost/Intranet remain unresolved separate ownership.
