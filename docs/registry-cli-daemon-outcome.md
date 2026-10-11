# Ordinary registry preparation outcomes

The preload journal records the mutation's CLI outcome separately from daemon
settlement authority. `commandSettled` retains its existing meaning and cleanup
guard. This change adds evidence; it does not discharge an unresolved mutation.

`cliOutcome.execution` is `not-observed` before mutation or when command-work
completion is unknown, `returned-success` when the required runner returns,
`returned-failure` for a terminal `DockerCommandFailure`, or `interrupted` when
the owned runner did not complete command work or its child exited by signal.
`completed-custody-unresolved` records completed command work whose CLI cleanup
failed before the required runner could return. `terminalReturnCode` records the
retained process status, including a status obtained during terminate/kill
recovery. Exit confirmation and CLI custody resolution are separate fields;
missing evidence is null. Recovery exit does not convert interruption into
normal command completion.

`daemonSettlementAuthority` starts as `unproved`. Only the existing successful
CLI return path records `successful-cli-completion` and sets `commandSettled`.
A nonzero CLI exit, registry rejection, timeout, killed CLI, partial image
mutation, absent image, or expired lease does not grant daemon authority.
Reconciliation failure after a successful mutation preserves that mutation's
CLI outcome rather than replacing it with the later inspection's outcome.
Old journals remain readable using the unchanged `commandSettled` guard.

The original main run 38124768418 at
d21a4f2b7be82b646a744824022629b99709abc4 failed on a pinned public ECR pull.
Its journal records return code 1 and registry-rate-limit classification with
explicit HTTP status unknown. CLI exit was confirmed and custody resolved;
daemon settlement and external runner destruction remain unproved. The sealed
original journal and unresolved lease must remain unchanged. No retry, pin
change, credential change, or destruction assertion is part of this correction.

Validation requires the registry Python suite in normal and optimized modes,
syntax compilation, and Linux hosted process checks. Windows skips three
existing actual POSIX-process checks; mocked process controls are supplementary
and do not substitute for those checks. No .NET source or workflow is changed.
