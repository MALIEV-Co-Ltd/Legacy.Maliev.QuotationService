# PR100 owned PostgreSQL storage diagnostic

## Root independent diagnostic validation

Whole Release build zero warnings/errors; affected33 and unfiltered617 passed
with zero failures/skips. Root full duration9m36s and TRX
`TestResults/root-storage-full/full.trx`, SHA256
`D3F735A0E47CE9F79DDFDB26FA2423B99E8233942262414EA4758A033811BD76`.
Whole verify-only formatting and six-project transitive audit passed. Existing
owned handwritten coverage gate remains5239/5523=94.86%, minimum80%; all five
checker regression tests passed (their deliberately failing66.67% synthetic
fixture is not the actual full-suite coverage result). No coverage exclusions,
thresholds, runtime or fixture budgets changed.

Root reviewed all diagnostic plumbing and the synthetic readback test. This
is a reviewed diagnostic improvement, not proof of the hosted process-death
cause or a storage correction. Retain the original failed run. Protected
required-head and post-main CI are still pending; do not close source parity
or claim PostgreSQL instability fixed merely from this local success.

Source candidate: `48eeca30f90491dc56be462564462567397602b4`.
Failed required run: 36952918565 / validation job 110669557991 (584 passed,
32 failed, zero skipped). All failures originate at InvoiceConsumerFixture's
second CREATE DATABASE before migrations/application assertions. The original
hosted failure is retained; this probe does not retry it or alter assertions.

The candidate newly bounds this fixture's entire PostgreSQL data directory to
256 MiB tmpfs. This is a hypothesis, not observed proof of disk exhaustion.
The 57P01 postmaster-death report cannot distinguish OOM, disk/WAL failure or
external lifecycle termination without container diagnostics.

Owned diagnostics inspect only the already-created local container's state,
exit/OOM fields, image identity and fixed-path storage metadata. No credentials,
environment, connection strings, application rows or arbitrary server logs are
emitted. Diagnostics run before disposal and cannot replace the original
initialization exception. The fresh probe uses unchanged startup/readiness,
second database creation, both normal EF migrations and synthetic readback.
Local success does not reproduce hosted runner resource pressure or prove a fix.

No resource-size, timeout, retry, assertion, runtime, CI or financial-policy
change is authorized by this diagnostic. Root review is required before any
storage correction. Validation results will be appended after execution.

## Local reached evidence

Release build: `dotnet build Legacy.Maliev.QuotationService.slnx --configuration Release
-p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-remaining-startup-proof-20261002/.dependencies
--no-restore`: zero warnings/errors. An initial diagnostic API-member compilation
error was corrected before any tests; that setup error is not a product RED.

Focused `dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj
--configuration Release --no-build --no-restore` with the same workspace property,
filter `FullyQualifiedName~QuotationInvoiceStorageDiagnosticTests`:
1 passed, zero failed/skipped. TRX SHA256:
`306EAA69A2E4BBF4F9FCB549C023D7E7DFF687A185343517DAA91A8EA2765139`.

Fresh combined filter `FullyQualifiedName~QuotationInvoiceStorageDiagnosticTests|
FullyQualifiedName~QuotationInvoiceConsumerContractHttpTests|
FullyQualifiedName~QuotationFirstDecisionPrecisionHttpTests`:
33 passed, zero failed/skipped, 2.0261 minutes. Original application assertions
remain unchanged. TRX under `TestResults/invoice-storage-affected`, SHA256:
`09CA527E2C8A154CEB75A5F60B3D2E02C6DDF2310E5B71DCEA9B14C65E4948F6`.

Local Docker Desktop 29.8.0 / WSL2 reported 7.76 GB memory. Local immutable image
identity: `sha256:bd1890816ae0b8ad4644f05728570d4be774e1f1490d7232f5084b52ea335183`.
The combined probe observed 262144 KiB total tmpfs, with 39776 KiB used at
readiness, 41752 after second database creation and 42624 after both migrations
(219520 KiB free, 16% used). WAL occupied 16384 KiB at each snapshot. All snapshots
reported running, exit zero and OOMKilled false. Fresh seed/readback passed.
This does **not** reproduce disk exhaustion or establish the hosted image/state,
concurrency pressure or cause of the original postmaster exit. No size change is
supported by these results alone; no storage fix has been made.

Scoped format verification and `git diff --check` passed. Normal owned fixture
disposal completed; a read-only owner-filtered Docker inventory found no retained
containers. No full suite, CI rerun, commit, push or persistent operation ran.
