# Quotation99 remaining owned startup proof

Root independent final gate: whole Release zero warnings/errors; affected126 and unfiltered616 passed, zero failures/skips. Full `TestResults/root-remaining-startup-full/full.trx` SHA256 `93C5C70375E495DD3086AD2F21D918115AE88C025CEC185CA704ECC2E4292413`. Root whole format, unchanged owned coverage gate5239/5523=94.86%, three-file redacted scans and whitespace verification passed. Only the two reviewed fixture lifecycle files and this doc belong to the commit. Generated Python caches and ignored evidence remain unstaged. Protected-head and post-main CI remain required; failed old-main evidence is not erased.

Bounded [Quotation99](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/issues/99), based on protected main `9dbeff7d1334e4ac8af61613e900de1d96588604`. Root-reported exact-main run36950361546 failed565PASS/51FAIL/0skip:33 NormalIamComposition and18 DecisionHighWater cases failed at Docker500 TCP-bind startup, BEFORE behavioral assertions. Joined qualification3PASS remains separate. This slice does not classify those environmental setup failures as application/auth/financial defects and does not blindly rerun unchanged startup.

## Ownership and preserved behavior

Only two existing test fixture owners change: `Controllers/QuotationNormalIamCompositionHttpTests.cs` (QuotationNormalIamFixture) and `Controllers/QuotationInvoiceConsumerContractHttpTests.cs` (InvoiceConsumerFixture). The latter also serves QuotationFirstDecisionPrecisionHttpTests and QuotationDecisionHighWaterHttpTests, so all three consumers belong to affected validation. No assertions, helper/predicate, test cases, runtime, CI, schema or migration definitions change.

Both owners delegate construction/start/disposal to the already reviewed `Infrastructure.DisposableContainerPair`. Each callback constructs a NEW container for each attempt, assigning the helper-generated owner/run/resource/attempt labels and name BEFORE start. Helper cleanup verifies that exact label tuple, removes only its owned resource, verifies absence and settles SDK disposal before a replacement attempt. The same reviewed exact Docker500 JSON/message predicate permits at most three attempts; other errors and exhausted collisions remain failures. Failed sibling startup cancels/settles its peer and verifies all allocated cleanup. There is no global Docker prune, unrelated container removal, test retry or application retry.

NormalIAM preserves PG18/Redis7 images, database quotation95, random password, original waits, loopback binding, PG256MiB tmpfs `/var/lib/postgresql`, Redis16MiB tmpfs `/data`, RSA/key/token lifetime, separate request database creation and migration order. InvoiceConsumer preserves its PG builder's existing database/password defaults and Redis wait/image, and adds explicitly approved matching loopback/tmpfs controls. Connections are obtained from the owned runtime containers; no ambient database target or persistent volume is used. Pair disposal is awaited and RSA disposal occurs in finally. Existing normal RS256/IAM transport and Accounting consumer controls remain exactly as written; controlled external transports are not production grant/provider proof.

## Pre-create authority comparison

The old NormalIAM inspection rejected DOCKER_HOST/DOCKER_CONTEXT, ran Docker context show then inspect, required one array entry, normalized raw `npipe:////./pipe/`, allowed alphanumeric/underscore/hyphen pipe suffixes, or accepted only `unix:///var/run/docker.sock`. It bounded process time15s and checked stdout length16KiB AFTER reading; stderr/output read accumulation was not independently bounded.

The reused helper still rejects both ambient overrides BEFORE creation, directly asks current Docker context inspect for its endpoint, permits only the canonical local Unix socket or named pipe, and normalizes the same raw named-pipe spelling. Named pipe suffix syntax additionally allows periods (legitimate local pipe names); this is explicitly not a remote endpoint allowance or claimed byte-for-byte grammar equivalence. Remote HTTP/TCP/SSH and ambiguous/malformed output fail closed. Both output streams are bounded16KiB while reading, inspection has15s cancellation, only its own inspection child is terminated on failure, and pipe/exit settling is bounded. This is the reviewed centralized local-authority guard, not a removed pre-create check. Helper code and its43 controls are unchanged.

## Private graph and initial evidence

Owned worktree `B:/maliev-legacy/.worktrees/quotation-remaining-startup-proof-20261002`, branch `codex/quotation-remaining-startup-proof-20261002`; no sibling outputs. Detached clean `.dependencies` pins: Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`, Accounting consumer `ae0826156b06c34476e95de8c53dfccfcf5a5972`. The canonical checkout and all older worktrees remain untouched.

Initial clean Release and helper-adoption Release both returned zero warnings/errors. Initial affected126PASS/0skip at `TestResults/remaining-startup-affected/affected.trx` (1m48s). This precedes the subsequently approved InvoiceConsumer loopback/tmpfs addition and is not relabeled final evidence. No new product RED is claimed: existing exact failed-main setup evidence establishes the remaining helper-adoption need.

Final source Release passed zero warnings/errors. Final affected126PASS/0skip (1m40s), `TestResults/remaining-startup-final-affected/affected.trx`, SHA256 `94180D821EAF38162BD085A4B94981EC181554A7470C04512FCD7EE666DFB58D`:33 NormalIamComposition,18 InvoiceConsumerContract,14 FirstDecisionPrecision,18 DecisionHighWater and43 unchanged DisposableContainerStartupContract controls. No assertions or helper code were altered.

Unfiltered full616 executed/616PASS/0FAIL/0ERROR/0TIMEOUT/0ABORT/0skip (2m26s), `TestResults/remaining-startup-full/full.trx`, SHA256 `777DE8D4E2D809072902F7C5E61526A48A7F39527F88F11DF2F17273EC008E4B`. Coverage is `TestResults/remaining-startup-full/ff25175e-368c-41d2-a92e-976c9b68bef2/coverage.cobertura.xml`. Existing unchanged gate: owned handwritten5239/5523=94.86%, minimum80. Its existing generated-obj classification and external dependency separation are unchanged, not a new exclusion/waiver. Raw unexcluded aggregate6602/15919=41.47% lines/39.13% branches. Raw package line/branch: API64.94%/56.34%, Application96.31%/87.50%, Data97.34%/78.57%, Domain96.96%/100%, MigrationRunner81.44%/61.59%; external Accounting Application0.45%/0%, Data0.83%/7.98%, Domain0%/100%, Contracts0%/100%, Defaults27.38%/21.96%. Do not conflate raw API with handwritten API465/502=92.63%, or the owned gate with aggregate coverage.

Whole format verification exited0; existing Python coverage-gate controls5PASS (their deliberately failing sample prints below-threshold as expected; real report passed). Six no-restore transitive vulnerability audits reported no vulnerable packages. Whole actionlint exited0, three scoped redacted gitleaks dir scans found no leaks, and diff whitespace check exited0. No ignored private fault/credential-bearing artifacts were printed/scanned. Disposable joined qualification proof3PASS belongs to the separate existing CI job; this local616 run does not relabel that proof as executed here.

Reproduction (all outputs and dependencies private to this workspace):

```powershell
$env:MalievWorkspaceRoot='B:/maliev-legacy/.worktrees/quotation-remaining-startup-proof-20261002/.dependencies'
$env:UseLocalMalievDependencies='true'
$env:GITHUB_ACTIONS='false'
dotnet build Legacy.Maliev.QuotationService.slnx -c Release --nologo
$filter='FullyQualifiedName~QuotationNormalIamCompositionHttpTests|FullyQualifiedName~QuotationInvoiceConsumerContractHttpTests|FullyQualifiedName~QuotationFirstDecisionPrecisionHttpTests|FullyQualifiedName~QuotationDecisionHighWaterHttpTests|FullyQualifiedName~DisposableContainerStartupContractTests'
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=affected.trx' --results-directory TestResults/remaining-startup-final-affected
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --collect:'XPlat Code Coverage' --logger 'trx;LogFileName=full.trx' --results-directory TestResults/remaining-startup-full
dotnet format Legacy.Maliev.QuotationService.slnx --no-restore --verify-no-changes
python -m unittest discover -s scripts/tests -p 'test_*.py'
python scripts/check_owned_coverage.py TestResults/remaining-startup-full/ff25175e-368c-41d2-a92e-976c9b68bef2/coverage.cobertura.xml --minimum 80
```

Every external exit code was checked. Each of Api/Application/Data/Domain/MigrationRunner/Tests was audited with `dotnet list <project> package --vulnerable --include-transitive --no-restore`. Tools: `C:/Users/natth/go/bin/actionlint.exe` and `C:/Users/natth/go/bin/gitleaks.exe` (`dir <ownedfile> --redact --no-banner`). All handles are terminal before root independent validation. No skips, assertion/timeout changes, denominator waiver, production readiness, deployment, persistent SQL or source-owner completion claim. Root owns review/commits/PRs; no agent commit/push.
