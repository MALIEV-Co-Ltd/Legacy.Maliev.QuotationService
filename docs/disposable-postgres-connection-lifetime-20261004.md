# Disposable PostgreSQL connection lifetime

PR #100 at `b8c4dbb5cab7c489663401a4a0aa37ee960bc7ef` failed its required
validation with 83 failures and 636 passes. The first failure was an Npgsql
stream EOF during the invoice fixture's initial database creation; subsequent
tests received PostgreSQL `57P01` shutdown errors.

A bounded owned-container experiment reproduced a failure when two sequential
PostgreSQL lifetimes reused the same dynamically reserved loopback port and
default pooled connection configuration. Two otherwise identical nonpooled
controls succeeded and correlated their SQL backend identity with the current
owned container. The experiment preserves startup readiness, images, tmpfs,
credentials and the first SQL operation; it does not retry SQL or assertions.
This establishes a reproducible pool-lifetime hazard without claiming that
every historical CI failure had an independently observed connector identity.

`DisposablePostgresConnectionPolicy.Isolate` disables pooling only for generated
test-container connection strings. All disposable Quotation test consumers
apply it to direct SQL, EF and HTTP factory configurations, including secondary
databases and migration-runner tests. Runtime projects and production connection
policy are unchanged. Authentication, financial assertions, concurrency,
deletion controls, receipt checks, route contracts and test parallelism remain
unchanged. Pool clearing is limited to the characterization experiment's exact
baseline pool; there is no global pool reset.

Four policy regressions failed against the unchanged scaffold and original
fixture configurations, then passed after implementation. Three inspect real
initialized direct EF contexts and both factory-registered database contexts.
The sequential-lifetime experiment also passed with its baseline failure and
healthy isolated controls. It reports an inconclusive baseline when the hazard
does not reproduce rather than forcing a failure or weakening the controls.

The reviewed test scaffolds were copied into a separate checkout from the
existing operator worktree; the original uncommitted files were preserved.
Dependency validation uses the same pinned Defaults, Contracts and Accounting
commits as required CI. Full-suite and hosted exact-head acceptance evidence is
recorded in the PR; this document does not waive those gates.

Hosted validation of the initial pool fix had 690 passes and 34 failures, all
from the invoice-decision fixture's unguarded single-container startup hitting
the exact Docker bind-collision signature. The Auth-to-Quotation joined gate
passed. A single-container adapter now uses the same reviewed three-attempt
startup predicate and ownership-checked cleanup. All remaining direct fixture
startups were converted, including paired PostgreSQL migration tests with
distinct resource labels. SQL and assertions are not retried.

The follow-up focused suite passed all 78 cases, including 34 invoice-decision
cases and the existing startup contracts. A new real single-container test
opens the current backend and independently verifies Docker's exact named
container returns 404 after teardown. The full suite and exact hosted-head
gates must also pass before integration.
