# Qualification before source integration

The separate dispatch workflow validates a reviewed, uncommitted candidate before
its C# files enter a Git commit. The ordinary protected build and test workflow
remains unchanged. A successful transport PR only validates the transport.

The committed policy pins manifest SHA256, exact accepted base, dependency commits
and all 73 file hashes/sizes. Operators upload the reviewed manifest and raw ZIP
through this repository's Git blob API, then dispatch with the two returned blob
IDs. The materializer reads only this repository, recomputes Git object identities
and SHA256, bounds bytes, and rejects duplicate entries, noncanonical paths,
symlinks, missing files and extra files. It writes the exact raw bytes into an
isolated checkout and verifies them before any SDK build. Text patches are review
artifacts; they cannot attest raw source with different line endings.

The hosted job builds the original IAM catalogue boundary using genuine pinned
Contracts and Defaults sources. Its four cases use mocked repository and event
transport. They do not establish HTTP/database registration or live IAM grants.
The job then builds the affected Quotation solution with warnings treated as
errors, runs its suite, checks formatting and package audit evidence, and retains
the existing focused checks and both 80 percent coverage floors. The historical
Accounting dependency is compile-time input; this run does not qualify a current
multi-host financial graph or malware scanner database.

All four original test-project reports must match their compiled discovery,
assembly and execution identities. The known nonserializable `Claim[]` member
theory can expand during execution: its reviewed source hash and ten-row inventory
are pinned separately. Other unknown discovery expansions fail closed. Audit
evidence must cover the exact solution project paths and their `net10.0` frameworks.
The trusted transport checks both solution and catalogue audits independently:
literal integer schema version, transitive vulnerable scope, reviewed actual feeds,
empty well-formed affected-package collections and empty retained stderr are
required. Catalogue restore feeds must match the genuine local package builder and
nuget.org; the hosted package cache starts fresh. The frozen candidate ZIP stays
unchanged when these transport checks are tightened.

Jobs have finite timeouts, read-only repository permissions and a 4096 MiB memory
admission guard. They use disposable hosted workspaces and start no detached
workers or provider sessions. Original logs, TRX, coverage and raw source identity
receipts are retained for seven days. Missing or failed native evidence leaves the
candidate ineligible for a C# commit. Successful native evidence still requires a
subsequent source PR and its normal protected checks before integration.
