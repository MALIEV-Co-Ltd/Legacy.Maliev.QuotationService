# PR100 owned PostgreSQL storage diagnostic

## Hosted recurrence and reachable diagnostic output

Head `0ad3868cd9dabb06185bdf284ff712ccd3ff46c1` required run36955873631
failed584 passed/33 failed/zero skipped. The independent storage probe and
HighWater/FirstDecisionPrecision fixtures failed during second-database creation;
the shared InvoiceConsumer class was not among those failed cases. Joined
authority job110678595697 passed. PostgreSQL process/stream loss remains
unresolved; no disk/OOM cause is proven and no resource/retry waiver is made.

The probe used xUnit fixture initialization, so initialization failed before its
ITestOutputHelper body executed. Console and Exception.Data metadata were not
rendered in the hosted job log. The NEW probe now owns initialization inside the
test method and emits fixed bounded storage metadata in finally before disposal.
It retains the original exception and every existing assertion/resource budget;
this is diagnostic reach, not a process-loss fix. No production logs or rows.

Fresh root Release zero warnings/errors;33 affected tests and617 unfiltered
tests passed with zero failures/skips. Full duration7m22s, artifact
`TestResults/root-storage-output-full/full.trx`. Whole verify-only formatting,
scoped secret scan and whitespace checks passed; dependency graph unchanged
from the previously completed six-project transitive audit. Exact-head CI remains
required; PR100 must not merge or close the startup issues on local success alone.

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

## Bounded process diagnostic extension (Quotation #99)

The subsequent exact-head validation run36959769436/job110690656606 at
`b03fc4ed9a949a82df3688b2f1aabf6be647c9e9` failed538PASS/79FAIL/0skip.
Failure groups:46 EmployeeActor,14 FirstDecisionPrecision,18 InvoiceConsumer and
one storage probe. They fail during fixture initialization's second CREATE DATABASE,
before migrations/controller acceptance. EmployeeActor uses default storage: this
contradicts a conclusion that the InvoiceConsumer256MiB tmpfs alone caused failure.
Invoice metadata showed running/exit0/OOMfalse,39776KiB used of262144, WAL16384KiB,
both ready and failed. These Docker/storage observations do not establish the
postmaster's process identity or cause of57P01/unexpected postmaster exit/EOF.
Failed hosted evidence is preserved; no unchanged hosted rerun is an acceptance.

Approved TEST-only five-file scope: new `Infrastructure/OwnedPostgresDiagnostics.cs`
and `OwnedPostgresDiagnosticsTests.cs`, existing InvoiceConsumer and EmployeeActor
fixture observers, and this doc. No API/runtime/startup helper/CI change. Both
fixtures retain image/wait/retry/cap/timeouts and original migrations/SQL/assertions.
Employee wraps only its original database initialization with observation and bare
rethrow; Invoice uses the same helper instead of fixed-path raw df output. Disposal
and container allocation are unchanged. Existing unrelated Python cache dirs remain.

Shared observation verifies inspected exact64hex DockerId, expected owner label,
32hex run label, pg resource,1-3 attempt and exact owned name **before** Exec/log
access. One10second diagnostic cancellation budget applies. It emits only Docker
state/process numbers/timestamps/image identity, fixed allowlisted numeric proc1/
postmaster start-time/PPID, cgroupmemory/pids counters, /dev/shm and PGDATA/WAL
capacity numbers. It never reads command lines/environment/database rows. Fixed
shell metadata output is capped inside the owned container and parsed fail-closed.
Docker log Tail100/Followfalse is read internally with a16KiB actual stream budget;
overflow does not retain/print raw log content. Unknown text is discarded.

The log classifier returns only event enums/numeric signals. Ready, shutdown,
reinitializing and process-signal structural records require the actual LOG level;
ERROR-level lookalikes are rejected. NoSpace, shared-memory resize and Panic are
**diagnostic categories, not verified causes**. Any observation failure returns a
fixed unavailable marker, never its exception type/message/body. There is no Docker
event stream, retry, sleep, storage adjustment or speculative fix in this slice.

Pinned SDK verification used Docker.DotNet.Enhanced4.3.3's actual XML/API and source
commit `1e4015a84fa48cbcfe9002ecc4e2cf14177edc2d`: non-follow container logs return
MultiplexedStream with ReadOutputAsync/count/EOF. Initial new-test DTO-name/nullability
compile setup errors were corrected before test execution; they are not productRED.
Release then built0W/E. Initial32diagnostic controls and actualfresh owned two-database
probe passed; affected111 passed0skip before review refinement. Five new ERROR-level
spoof controls reproduced a genuine **new diagnostic classifier** RED (33PASS/5FAIL,
0skip), retained in `quotation99-classifier-level-red/classifier-level-red.trx`.
It is not a reproduction of hosted PostgreSQL failure. A real nested observer
failure/bare-rethrow control checks original exception instance and throw-site.

After restricting structural records toLOG, freshRelease0W/E and final38controls
pass0skip, `quotation99-process-diagnostic-final-focus/final-focus.trx`. Actualowned
probe asserts all three phases have available metadata, numeric postmasterPID,
positive storage/shm totals and reached classified events. Local image/kernel still
differ from hosted: these passes establish observation reach/safety, not rootcause.
Final affected/full/static evidence will be appended after terminal validation.

## Final diagnostic candidate validation

Repository `.gitattributes` requires LF. Only the five owned files were mechanically
normalized to that policy; unrelated Python cache dirs were preserved. Fresh serial
Release build using the command above then passed0warnings/0errors. The final
38-case focus passes0fail/0skip, TRX SHA256
`6B5DC0C113701D161CA343DC57987D78F56EAFC4F159DDF9B6569D1FE831B55E`.
The fresh combined five-class filter (new diagnostic controls plus original four
affected classes) passes117/117,0skip,2m23s:
`quotation99-process-diagnostic-final-affected/final-affected.trx`, SHA256
`4FD2D0E6406B31858326EF8481F62ADE2821A1DE4493E993C51FAA18428934DA`.
The diagnostic classifier's retained5RED SHA256 is
`1734137727D76416808547F332D891053C76670616839EB8C03EBE08ED2DA1CE`.

Same direct test command without filter, collect XPlat coverage, results directory
`TestResults/quotation99-process-diagnostic-full`:655PASS/0FAIL/0skip,6m59s,
`diagnostic-full.trx` SHA256
`D1E0BEDCAA9CB7B89C28229FD2797E4339770BE344B1E7EF5D1C866268921747`.
Root observed low host memory headroom during this run; its actual green result is
retained, but no additional full run is started without a serial-window decision.
No user processes/containers were stopped, and no pruning/resource/budget changes
were made. Raw coverage remains API465/716=64.94%, Application157/163=96.32%,
Data3958/4066=97.34%, Domain96/99=96.97%, MigrationRunner667/819=81.44%.
The **unchanged existing Quotation** owned-handwritten checker passes5239/5523=94.86%
at minimum80; its five existing success/failure unit controls pass using Python-B
so unrelated cache files are not touched. Expected negative-control stderr is not
a product coverage failure. No new exclusion/checker or coverage waiver was added.

Local reached metadata shows postmasterPID1/PPID0 at all three phases, /dev/shm
65536KiB total/1056KiB used, PGDATAfree222368/220392/219520KiB and Ready category.
Local cgroup memory.events.oom_kill is **absent**, not zero. DockerOOMfalse plus
missing cgroup data cannot rule out host/child OOM. These facts do not establish
the failed hosted image's process/reaper/cgroup/shared-memory state or rootcause.
No speculative PostgreSQL/storage/readiness fix or hosted rerun has occurred.

Whole solution format verification completed successfully. Ten sequential
`dotnet list <project> package --vulnerable --include-transitive` audits completed
with no vulnerable packages: six solution projects, private Defaults/Contracts and
private Accounting Application/Data. Five owned files passed
`gitleaks stdin --redact --no-banner`, `git diff --check`, explicit trailing/extraEOF
whitespace and LF-policy checks. Owner-filtered Docker inventory found no retained
InvoiceConsumer/EmployeeActor containers. All handles are terminal and the five-file
candidate is frozen for root independent review; unrelated cache dirs remain.
No commit, push, external GitHub mutation or persistent database/provider operation
is authorized by this diagnostic candidate.

Root integration review additionally places exception-Data attachment and console
output inside the already tested PreserveFailureAsync boundary in both fixtures.
An observer output failure must not replace the original initialization exception;
the original bare rethrow remains. Prior655 evidence is pre-refinement; fresh root
focused/full validation is required before committing this final integration.

Final root integration: the fresh Release build had zero warnings/errors; all38
focused diagnostic cases passed, then the unfiltered suite passed655/655 with
zero skips, errors, timeouts or aborts. Final full evidence is
`TestResults/root-quotation99-output-guard-full/full.trx`, SHA256
`C68BEE8A79657F6963BE6C5EE7ED2B39CF6877FF5E56B689B7FDA5A2EBE4D9CE`.
This supersedes the pre-output-guard local full run but does not establish the
hosted PostgreSQL failure cause or waive the required replacement CI gate.
