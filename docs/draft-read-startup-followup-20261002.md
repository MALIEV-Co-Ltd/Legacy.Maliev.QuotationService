# Draft read fixture: independent bind-collision follow-up

Protected PR100 head c9601d5775ed1ff556efd83ad4eaa3a9f1fdebe6 failed in
required run36964354119 (604 passed,51 failed,zero skipped). The DraftReadFixture
failed before application assertions: Docker returned an exact500 networking
bind-collision message ending `failed to listen on TCP socket: address already
in use`. This is distinct from Invoice/Precision SQL57P01 failures, whose cause
remains unproven. No server-death or capacity repair is claimed here.

This fixture now uses the already reviewed DisposableContainerPair shared by
other PR100 fixtures. It allocates unique run/attempt names and ownership labels,
observes the local Docker endpoint, publishes5432/6379 on loopback, and settles
both sibling starts. Only the exact existing Docker500 collision classifier may
retry, at most three attempts, after independently verified owned-resource
cleanup. No database-operation retry, provider/image change, extra startup wait,
resource tuning, synthetic-data change, authentication change, or assertion
waiver is introduced. PostgreSQL18/Redis7, both original migrations, request
database creation, real HTTP/search/cache/live-IAM controls remain unchanged.
Final disposal verifies only fixture-owned resources; RSA disposal remains in
finally. No foreign container, persistent database, or application workload is
removed.

Regression evidence is the retained hosted pre-assertion failure plus the existing
shared startup-helper collision/non-collision/ownership controls and unchanged
DraftRead HTTP suite. Fresh Release, focused tests and unfiltered suite must pass
on the integrated tree before a commit or replacement CI is accepted. No passing
focused/full result is asserted by this implementation note alone.

## Integrated local acceptance evidence

Fresh Release built with zero warnings/errors. The unchanged DraftRead suite
passed all 17 tests with zero skips in
`TestResults/peer-correlation-draft-read/draft-read.trx` (SHA-256
`74E6DFD01784DA174F78472F1C96E82C88833767F50536765DC713921BC6E05A`).
The integrated unfiltered suite passed all 681 tests with zero failures/skips in
`TestResults/peer-correlation-integrated-full/full.trx` (SHA-256
`4DD5197ED5D3523D040FAA6109BDA6360AF8B82B7F48A09BF018DAD42C1B063A`).
The pure diagnostics/startup suite passed 101 tests; three actual storage and
correlation probes also passed. Whole-solution formatting verification, ten
transitive vulnerability audits, and the unchanged coverage checker passed.
Original failure evidence is retained. Replacement exact-head CI and post-main
acceptance are still required; local passes do not establish the hosted SQL
57P01 cause or waive that gate.
