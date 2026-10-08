# Original scanner startup PING handoff

Actual run37747397482 at93dd failed the exact nonempty relay-history condition.
Its failure artifact did not identify the exception type or timestamp. This
source correction addresses the original readiness retry/history contract;
it is not proof that a particular recorded failure was transient in that run.

The relay retains each original startup caller/accepted/backend socket and
worker before operations. Its complete exact zPING frame precedes backend
connect. Only the actual backend-connect ConnectionRefusedError/ECONNREFUSED,
matched to the original caller peer and original failure-list index, can be
accounted after caller-observed truncation and independent original close/join
settlement. Every failure row remains unchanged. No constructor, validation,
timeout, unknown-peer/frame/errno, cleanup fault or post-seal error qualifies.
Original uncertain ownership stays retained and prevents further attempts.
The total attempt/history cap is16 and each attempt shares one finite deadline.

An actual PONG plus complete original worker/socket settlement seals only those
exact privately held rows. Bridge readiness checks this narrow handoff instead
of blindly requiring an empty historical list. The remaining six original relay
conditions, exact image/network/backend checks and ownership policy remain.
Scanner.command is unchanged. The optional startup_tracking argument defaults
to false, preserving ordinary connect-before-frame behavior and idle cleanup.
The actual Scanner opts in before acceptor birth; only its retained startup
attempt reads the complete PING before backend connect. Unknown preseal peers
refuse this tracked handoff. Ordinary postseal streams remain available;
Scanner startup catches only the new source-owned settled transient refusal.
Public startup observations use fixed counts/flags without peer/exception data.

Dedicated held pure controls run before actors through bounded source admission.
They are models and prove no socket/thread/container/cleanup/File/eight run.
The original Docker runner, native four cases,14 readiness controls, source graph
and existing strict upload/security gates remain independently required on the
new head. Pair/cleanup/File/eight acceptance remain false until raw actual proof.
