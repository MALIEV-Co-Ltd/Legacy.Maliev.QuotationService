# Quotation99 remaining actor/qualification startup owners

Current bounded candidate, based on `ff604f61f84e8ab1bad6b1a30b21871b6b1e6f22`, in the exclusively owned `quotation-remaining-startup-proof-20261002` worktree. Root-reported required CI36957963959 returned531PASS/86FAIL/0skip. The86 failures share exact Docker500 networking/TCP-bind collision grammar in QualificationBridgeConsumerFixture and QuotationEmployeeActorFixture before HTTP assertions. They are setup evidence, not86 independent product/authentication defects. Historical hosted PostgreSQL postmaster-death failures remain unresolved; this repair neither retries nor claims to solve them.

## Exact ownership and compatibility

Only fixture lifecycle sections of `Legacy.Maliev.QuotationService.Tests/Controllers/QualificationBridgeConsumerHttpTests.cs` and `Controllers/QuotationEmployeeActorHttpTests.cs` change, plus this new document. All assertions, endpoints, authority configuration, migration/database order, RSA/token lifetime, waits and request/status/time budgets remain unchanged. The qualification fixture retains its concrete controlled upstream transports; employee actor tests retain their existing synthetic external IAM answers. Neither becomes deployed Auth/IAM acceptance.

Both callbacks now allocate fresh containers through the unchanged reviewed `Infrastructure/DisposableContainerStartup.cs` DisposableContainerPair. Before start, each receives exact owner/run/resource/attempt labels and unique helper name, and uses the guarded local Docker endpoint. PostgreSQL remains `postgres:18-alpine` with its original builder defaults; Redis remains `redis:7-alpine` with the original internal-port wait. Published host ports are loopback127.0.0.1 only. No tmpfs, storage size cap, volume or PostgreSQL tuning is added or changed. On disposal, the pair verifies matching ownership and absence before disposing its Docker client; RSA resources are disposed in finally even if pair cleanup fails.

The helper rejects ambient DOCKER_HOST/DOCKER_CONTEXT before allocation, allows only canonical local Unix socket/named pipe (normalizing the reviewed raw named-pipe spelling), bounds inspection output/time and never accepts a remote endpoint. It retries only exact500 collision JSON/message grammar, maximum3 attempts, after verified owned cleanup; cancellation, postmaster death and all unrelated errors propagate. No helper, collision predicate, image, runtime, schema, test retry, CI gate or timeout change.

Private dependency pins remain Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`, Accounting `ae0826156b06c34476e95de8c53dfccfcf5a5972`. Initial unchanged and adopted-source Release builds both returned zero warnings/errors. Focus/full/static evidence will be recorded after terminal execution, not assumed from hosted failures or helper-only controls. Python cache directories pre-existed and remain unstaged.

## Read-only remaining direct startup inventory

All paths below are relative to `Legacy.Maliev.QuotationService.Tests`; none was edited. This inventory does not authorize their adoption or classify them as currently failing.

| Path / fixture | Direct resources | Existing second database |
| --- | --- | --- |
| Controllers/QuotationDraftAggregateHttpTests.cs / DraftAggregateFixture | PostgreSQL18-alpine + Redis7-alpine | aggregate_requests |
| Controllers/QuotationDraftReadParityHttpTests.cs / DraftReadFixture | PostgreSQL18-alpine + Redis7-alpine | draft_requests |
| Controllers/QuotationRequestCreateRetryHttpTests.cs / RequestCreateRetryFixture | PostgreSQL18-alpine + Redis7-alpine | request_create_retry |
| Controllers/QuotationInvoiceDecisionHttpPostgresTests.cs / QuotationInvoiceDecisionPostgresFixture | one PostgreSQL18-alpine | quotation68_requests |
| Data/LegacyQuotationOutcomeAdopterTests.cs | one PostgreSQL18-alpine | per-test contexts |
| Data/QuotationPostgresMigrationTests.cs | two PostgreSQL18-alpine containers | separate quotation/request containers |
| MigrationRunner/MigrationRunnerPostgresTests.cs | two PostgreSQL18-alpine containers | separate quotation/request containers |

Infrastructure/DisposableContainerStartupContractTests.cs deliberately creates controlled resources to test the helper itself; its direct calls are not unguarded production-fixture owners. Remaining `app/sink/redirect.StartAsync` calls are local HTTP hosts, not Docker starts. Other HTTP owners already use DisposableContainerPair (financial outcome, triage, normal IAM, invoice consumer). A future cohesive remaining-owner slice would preserve each fixture's existing image/storage/wait/schema contract and needs separate root ownership approval; singleton PG owners require the reviewed single-resource adapter rather than manufacturing a Redis dependency.

No persistent database, provider, production, deployment, original source, GitHub or ledger operation. Root owns final independent validation, commits and protected integration. MALIEV testing/TDD and verification skills guided actual boundary validation; their generic stub/commit guidance does not override the preserved existing authority fixtures or explicit no-commit scope.

## Final local validation and reproduction

All handles terminal before root independent validation. Adopted-source whole Release: zero warnings/errors. Focus129PASS/0skip/36s:40 QualificationBridgeConsumerHttpTests +46 QuotationEmployeeActorHttpTests +43 unchanged DisposableContainerStartupContractTests; `TestResults/actor-qualification-startup-affected/affected.trx`, SHA256 `9E14D4A4966E656E98E9234526715D7A372F77C06B72309BEDD67208D9575DEC`.

Unfiltered full617 executed/617PASS/0FAIL/0ERROR/0TIMEOUT/0ABORT/0NOTEXECUTED/0skip,3m10s, `TestResults/actor-qualification-startup-full/full.trx`, SHA256 `936C32467CF6B86129898D71A8688897DEC248E15090C05A5621A876888846D1`. Coverage path: `TestResults/actor-qualification-startup-full/579743d4-730b-4683-bd73-697c1bd93ee6/coverage.cobertura.xml`. Existing unchanged handwritten owner gate5239/5523=94.86% passes80. Its existing generated-obj/external dependency separation was not altered. Raw unexcluded aggregate6602/15919=41.47% lines/39.13% branches; raw API64.94%/56.34%, Application96.31%/87.50%, Data97.34%/78.57%, Domain96.96%/100%, MigrationRunner81.44%/61.59%. These raw residuals are not waived or conflated with owned handwritten coverage. The separate joined qualification CI job was not executed here and is not counted in617.

Whole format verification exited0. Existing Python gate tests5PASS; their deliberately failing sample output remains a test control, not the real report. Six no-restore transitive package vulnerability audits returned zero vulnerable entries; initial local audit-summary script incorrectly counted two null placeholders as packages and stopped, then corrected non-null counting independently reran all6 reports unchanged. This was diagnostic tooling, not a package finding or repo change. Whole actionlint, three redacted gitleaks owned-file scans and diff whitespace check exited0. No API/DTO/permissions, message definitions, schema/migrations, production I/O/retry, runtime or shared helper changes were found in the final scoped review.

```powershell
$env:MalievWorkspaceRoot='B:/maliev-legacy/.worktrees/quotation-remaining-startup-proof-20261002/.dependencies'
$env:UseLocalMalievDependencies='true'
$env:GITHUB_ACTIONS='false'
dotnet build Legacy.Maliev.QuotationService.slnx -c Release --nologo
$filter='FullyQualifiedName~QualificationBridgeConsumerHttpTests|FullyQualifiedName~QuotationEmployeeActorHttpTests|FullyQualifiedName~DisposableContainerStartupContractTests'
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=affected.trx' --results-directory TestResults/actor-qualification-startup-affected
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --collect:'XPlat Code Coverage' --logger 'trx;LogFileName=full.trx' --results-directory TestResults/actor-qualification-startup-full
dotnet format Legacy.Maliev.QuotationService.slnx --no-restore --verify-no-changes
python -m unittest discover -s scripts/tests -p 'test_*.py'
python scripts/check_owned_coverage.py TestResults/actor-qualification-startup-full/579743d4-730b-4683-bd73-697c1bd93ee6/coverage.cobertura.xml --minimum 80
```

Each Api/Application/Data/Domain/MigrationRunner/Tests project was separately audited with `dotnet list <project> package --vulnerable --include-transitive --no-restore --format json`. Tool paths: `C:/Users/natth/go/bin/actionlint.exe` (enumerated allworkflow paths) and `C:/Users/natth/go/bin/gitleaks.exe dir <ownedfile> --redact --no-banner`. Final doc readback/scoped scan follows this evidence update. No commits/push or new scope granted by local GREEN; protected-head/main CI and root review remain mandatory. Historical hosted postmaster-death evidence remains an independent unresolved gate.
