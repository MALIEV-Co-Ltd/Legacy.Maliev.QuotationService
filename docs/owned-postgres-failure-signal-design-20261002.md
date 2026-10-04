# Quotation100 failure-signal diagnostic: minimal seams, not fixture adoption

## Root final validation, October 2

The six-file diagnostic adoption is validated locally, not a diagnosis or waiver of the prior hosted failure. Root freshly rebuilt the exact private dependency graph with `--no-incremental`, Release warnings-as-errors: zero warnings/errors. Unfiltered tests with coverage executed **719 passed, zero failed/skipped/errors** in 9m6s. TRX `TestResults/failure-signal-root-final-full/full.trx`, SHA256 `6A3714C13C7C1FEC22063504D672414E5F49D1D863F384C2222FA2FB8EE52970`. Whole-solution format verification passed. Local processor count remained1, as in earlier local acceptance; hosted concurrency and resource policies are unchanged.

The earlier agent run `33050` had no surviving observation handle, matching process, or terminal TRX after context transition. Its incomplete output directory is preserved and **not** treated as a passing result. Root used a new output directory and fresh build after confirming no matching live process; no observation timeout alone triggered a restart.

Executed commands: `dotnet build Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-incremental -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false`; `dotnet test` on the same project `-c Release --no-build --no-restore --collect "XPlat Code Coverage" --logger "trx;LogFileName=full.trx" --results-directory TestResults/failure-signal-root-final-full`; `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore`. Environment selected `UseLocalMalievDependencies=true` and absolute owned `.dependencies` root. Required hosted exact-head CI and protected-main acceptance remain pending.

## Latest current status

### Minimal six-file adoption: focused GREEN, full held for root review

Fresh exact-private Release zero warnings/errors; NEW pure/sink32PASS; combined existing diagnostics (including actual owned caller), independent InvoiceStorage and Invoice consumer focus89PASS, zero failures/skips. The missing PassivePid regression is now GREEN; all original strict post-CREATE SQL/socket/cluster assertions remain. Actual fields separately expose positive passive PID and verified numeric owned process; they do NOT imply full BackendMatched before CREATE. No borrowed SQL/socket query was added pre-CREATE. Fixture catches capture the closed failure signal before further diagnostic work; independent probe emits only sanitized closed DTO with bare rethrow and contains snapshot-output exceptions. Disposal call/order and failures remain unchanged. Healthy actual focus does not reproduce or establish a hosted failure cause.

Owned scope now SIX files: helper, existing helper tests, NEW failure-signal tests, Invoice fixture file, independent InvoiceStorage probe and this doc. No startup/timing/resource/workflow/production change. No unfiltered full or hosted rerun yet. Prior gates below are historical, not current adoption restrictions.

Current artifacts: `TestResults/failure-signal-adopted-pure/pure.trx` (32PASS), `TestResults/failure-signal-adopted-actual/actual.trx` (89PASS). Both processes exited0; no live handles. Tiny comment-only truthful lifecycle correction followed focused gates; no behavior change.

SHA256 respectively: `0D006413407FF3A8772EC260666B3B776B0194DAD660C3BA471684C73E2009EE`, `26ABDD76D559BB582F19ED0A52DDE0EC796078F2C1CBC2CB89532FDA24B4EBDF`.

### Actual adoption TEST-FIRST gate (implementation held)

Fresh exact-private Release0warnings/errors. NEW actual xUnit sink controls: two passed, zero skips; literal safe DTOs reached TRX StdOut via real ITestOutputHelper. Strengthened existing actual correlation test: one genuine assertion RED, zero setup errors/skips, after the unchanged fixture completed initialization and three storage snapshots. Missing explicit PassivePid before Open is the first reached assertion at existing test line181; positive opened PID/process obligations follow, and all original post-CREATE correlation assertions are retained. Helper ObserveConnectionAsync, Invoice fixture and independent InvoiceStorage probe implementation are STILL unchanged. No full/hosted rerun; scoped owned loopback fixture only. Root must review actual RED before adoption.

Artifacts: `TestResults/failure-signal-xunit-sink/sink.trx` (2PASS); `TestResults/failure-signal-adoption-red/adoption-red.trx` (1assertionFAIL). No surviving agent process handles.

SHA256 respectively: `2F48CB3C694A68AA7146420E52C2781E5FDB6C5910DAE928130478A928E82C99`, `B369C8406CD89C26C9C59077C539ADFFC2E0CDB7C122217C11B9F03CBB145563`.

### Prior minimal-seam gate, before actual adoption tests

The four diagnostic seams are now implemented after the retained 14-assertion RED below. Fresh exact-private Release: zero warnings/errors; NEW focus30 passed; existing pure diagnostics65 passed; existing pure startup-helper42 passed, all zero skips/failures. Whole-solution `dotnet format --verify-no-changes --no-restore` succeeded after setting the same absolute private graph through environment properties. An initial format invocation incorrectly supplied unsupported `-p` arguments and exited1 before loading a workspace; the corrected verify-only command exited0. No container/full-suite/hosted rerun or Invoice/other-fixture adoption occurred. The added seams are not yet called by existing observations. Three files remain the complete owned diff; root review is required before integration/adoption. No underlying hosted cause claim.

GREEN TRXs: `TestResults/failure-signal-implemented-focus/focus.trx` (30), `TestResults/failure-signal-existing-pure/pure.trx` (65), `TestResults/failure-signal-existing-startup/startup.trx` (42). Filters explicitly select pure tests and exclude actual container cases because root has not authorized those yet; these focused results are NOT an unfiltered-suite gate.

SHA256 respectively: `BBE9A387668E87651ACB30A599EA660BFCA66D1F65BB83F7D72D0BC533AEB66F`, `C655CBD0208B7E51A071DC45F7737C3C13CEA2FBD2DDB8DD0A1151A0E1044785`, `C2EEA4104A1CCF62046F0134F28E6F21C91C754C820BEAE589F5076CF7B0FD69`.

## Historical scaffold and RED chronology

Base: `427be87bd3169fd65ca8a0f0725bb0176acf0e66`. Historical first diagnostic gate: fresh exact-private Release build succeeded with zero warnings/errors; NEW pure tests executed 30: 16 passed / 14 expected assertion RED / zero skips or setup errors. At that gate null/unavailable/no-op seams were compile scaffolds, NOT implemented observations. Existing Invoice probe and every fixture/startup/workflow/resource policy remain unchanged.

Build: `dotnet build Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-remaining-startup-proof-20261002/.dependencies -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false` (exit0). Focus: same project, `-c Release --no-build --no-restore --filter FullyQualifiedName~OwnedPostgresFailureSignalTests --results-directory TestResults/failure-signal-scaffold-red --logger trx;LogFileName=failure-signal-red.trx` (exit1 from14 assertions). Private pins Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`, Accounting `ae0826156b06c34476e95de8c53dfccfcf5a5972`. Processes terminal; no implementation/adoption authorized by these results alone.

## Observed hosted failure, not inferred cause

Scaffold RED TRX: `TestResults/failure-signal-scaffold-red/failure-signal-red.trx`; SHA256 `9EB7C510C5078503D6F1A76B6BE1C9A62D7F2E40AC7DE2870A3D834951E303FF`. Every failure is a reached xUnit assertion; error/timeout/aborted/not-executed counters zero.

Run `36976121304`, validate job `110740221794`: 568 passed / 119 failed / zero skips. Independent InvoiceStorage xUnit output contains two bounded typed snapshots; artifact count is zero, but that output route is proved reachable. Original CREATE at Invoice fixture line325 fails with Npgsql stream error and inner EOF, rendered 2026-10-02T07:00:27Z. No create-complete phase. Other fixture groups report 57P01 separately.

Both snapshots: PGDATA total262144, used39776, free222368, du39776, WAL16384 KiB; SHM total65536, used1056, free64480 KiB. Same container ID SHA256 `53D31342D0188DFC1098642799F8D6BF32675E6C1DE97B89690D1130B20D40C5`; PID1/postmaster1/start28180, Runningtrue/Exit0/OOMfalse/Restart0; memory.events allzero; Events Ready only. Configured 256MiB tmpfs agrees with observed capacity, but current output does not independently establish mount filesystem. These samples neither prove nor exclude the hosted cause.

## Proposed bounded boundaries and independent tests

- ReadPassivePid: actual open connection ProcessID getter only. Positive PID separate from SQL/Owned correlation; unopened/invalid remains null. Existing real borrowed-SQL boundary must still issue zero commands for open-complete/create-start. A PID alone never proves lineage.
- ReadOwnedProcessAsync: exact existing ID/name/owner/run/resource/attempt verification BEFORE process delegate; positive numeric PID only. Fixed three-line PID/parent/start format; missing, zero, mismatch, malformed or extra fields remain unknown. No SQL, socket identity substitution or caller mutation.
- ClassifyFailure: closed enum categories (Unknown/Postgres/Npgsql/EndOfStream/Io/Canceled/Timeout), maximum eight chain entries. Optional SQLSTATE from actual PostgresException, exact five uppercase ASCII letters/digits. Never exception type names/messages/stack/Data, SQL, tokens, environment or application rows.
- EmitFailureAsync: observer returns only the closed typed DTO, not arbitrary serialized text. It sanitizes enum/SQLSTATE/count, serializes only Categories/SqlState (maximum2048 characters), and never propagates observer/output exceptions over the original caught error. Caller retains bare rethrow.

NEW pure tests use synthetic typed exceptions and literal inspected ownership, no transport. Executed scaffold assertion failures: positive passive PID2, throwing-getter invocation1, verified process observation1, refused ownership mismatch3, actual failure classification2/eight-entry bound1, safe emitted DTO2 and observer/output invocation2. Unknown/invalid controls pass the no-op scaffold; that is not implemented-feature proof. No missing-type/compilation error counts as RED. Fourteen genuine new-feature assertions are retained before minimal implementation review.

## Authority and next gate

Owned files: existing `Infrastructure/OwnedPostgresDiagnostics.cs`, NEW `Infrastructure/OwnedPostgresFailureSignalTests.cs`, this doc. Existing helper tests and independent InvoiceStorage probe are unchanged. Root reviewed actual RED and authorized only the four minimal seams; that implementation is complete and focused-green above. No fixture adoption approved yet. Separate root review is required for actual fixture passive/failure capture and ITestOutput probe adoption. No raw log artifact or workflow change, hosted retry, pool/image/timeout/resource policy change, persistent operation or cause claim.
