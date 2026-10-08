# Proposed actual Ubuntu Scanner helper controls

This additive harness is source-only and unexecuted on Linux. It consumes only
the new raw-entrypoint scanner_docker_command.py 89daed40/18024-byte
counterpart, with the original _Lease implementation unchanged. Prior native
Docker receipts are historical and cannot qualify this new exact source graph. A held regular nonblocking/no-follow file reader checks exact size,
pre/post inode/metadata and SHA before compiling those held bytes. Workflow
qualification must independently pin this harness/test plus scanner and helper
before execution, bind the checked-out head/run/attempt, and retain typed failure
artifacts via signed v7 strict upload. This packet does not provide that workflow.

Run ONE case per standard Ubuntu workflow process, without any custom Docker
host/context/certificate environment or secret grants:

    python tools/InvoiceCompletionProducerAcceptance/companion/scanner_helper_hosted_controls.py --case natural --receipt TestResults/ScannerHelper/natural.json

The other exact cases are selector-register, post-signal and post-waitid.
Natural executes real docker --version. Selector-register delegates the real
selector registration on the retained original pipe before injecting its fault;
it permits the original Docker process either to exit naturally or be actually
signalled. It does not model an unknown throwing selector constructor as closed.

Signal/wait cases run a real Docker events query with a fixed synthetic output
format and a one-second command deadline. No container/network/image is created,
removed or modified by these commands. The actual Docker daemon must support the
local read-only query; a quick daemon/CLI failure cannot pass a fault case. A
post-signal fault is raised only after the real exact pidfd SIGKILL delegation.
A post-waitid fault is raised only after the actual original settled nonreaping
waitid observation inside qualified wait_original, following the actually
delegated kill and before the live deadline. It never replaces syscall results.
If scheduling never reaches those causal gates, the case fails, not forecasts.

The post-waitid case keeps the original zombie, pidfd and private source lease
reachable. Under the same reacquired exclusive source lifetime fence, a second
PRIVATE cleanup qualification call can perform actual exact waitpid and remaining
handle closes. Only the exact delegated waitpid returning the held original PID
sets the reaping witness. Original first-attempt historical fields/fault objects
remain unchanged and sticky quarantine remains reachable even after physical
settlement. This is explicitly control-only access to a private qualified source
lease, not a new production recovery API or launcher cleanup authority.

The module-owned witness registry is allocated before actor acquisition and
retains the helper/Popen/selector/lease until the harness exits, including faults.
Each per-case receipt separates source initial cleanup/reap flags from later
physical original settlement. Actual retained pipe identities are independently
searched in a bounded4096-entry, one-second parent-FD census; no ambient whole-FD
set equality, all-FD/descendant census or parent-death guarantee is claimed.
Original epoll closed state, actual source pidfd close and stream EOF/closed
objects are separately required. Public flags contain no PID/FD/generation,
output, command arguments, URI/path or exception text. Failed receipts cannot
be interpreted as successful cases. The30-second Linux alarm is a control
guard, not proof of kernel CPU/memory caps or universal opaque-syscall deadlines.

The source helper with unchanged _Lease remains responsible for actual CLI leader lifetime;
the direct workflow process owns the witness and borrowed original objects. No
nested Python actor is created. Docker CLI descendants/daemon jobs, parent
termination and arbitrary asynchronous partial acquisitions remain outside this
qualification; uncertain owners stay retained while the harness is alive and
never produce acceptance. AllDescriptorsCensusProved, KernelCapsObserved,
ParentDeathGuaranteeProved and ActualBusinessGraphAccepted remain false.

Eight local validator methods exercise five MODELED matrices, causal-negative
gates, canonical hosted context and fixed private-safe diagnostics. SourceHead,
RunId, Attempt and github-hosted runner fields bind declared workflow context;
the independent workflow must additionally observe exact checked-out source and
artifact provider metadata. The harness does not silently self-certify Git HEAD.
Failed receipts retain a fixed admission/load/control/recovery/receipt category
and observed flags; no exception message or dynamically named exception is
published. These tests check modeled admission conditions only. They never
call main/run_case/load_helper or acquire actual
descriptors/children. No local Docker, SDK, actor, pidfd or new socket execution
has occurred. Actual four-case artifacts and coherent manifest/workflow
integration remain pending independent review and hosted execution. This is
separate from accepted scanner Engine/native14 and financial eight-host proof.
