# Normal-host readiness integration

The exact reviewed parent-lifecycle v1 implementation is reused in
`tools/InvoiceCompletionProducerAcceptance/companion` and
`tools/HostedProcessStartObserver`. The fourteen imported files match the original receipt after Git LF normalization.
The immutable raw receipt remains provenance; Git stores normalized bytes.
This successor applies the hosted formatter's whitespace corrections only to
Program.cs and the observation tests. The observer project continues to link
the existing ChildStartObservation and producer project. Native build and hosted
execution are still required.

`NormalHostReadinessStage` connects these existing APIs to the real held
`OwnedNormalHosts` lifetime owner. `start_phase` permits an explicit File-only
stage before the authenticated signing handshake. It retains the exact native
seven-digit `StartedUtc` separately from the older observation timestamp.
After front bootstrap and all upstream readiness, `admit_ready_graph` calls all
eight actual source-owned health routes, retries within one finite health
deadline, and obtains a second exact native census. A dead host, missing owner,
changed native identity or deadline failure closes the exact held normal owner.
The owning parent remains responsible for retaining any cleanup-failure evidence.

The actual caller must create the stage inside the `OwnedNormalHosts` context:

```python
with OwnedNormalHosts(...) as hosts:
    stage = NormalHostReadinessStage(hosts, parent_profile, observer_dll, observer_hash)
    stage.start_phase(first_explicit_owners)
    # Complete the source-owned front/File handshake and dependency setup.
    stage.start_phase(remaining_explicit_owners)
    readiness = stage.admit_ready_graph()
    # Invoke the real financial scenario while the same owner remains alive.
```

No source graph, arbitrary startup order, healthy dependency result, native time,
JWT or success receipt is synthesized here. This module has no CLI and does not
yet replace the missing canonical parent/resource acquisition/scenario connection.
Its receipt explicitly leaves genuine financial acceptance false. The full
scenario must independently retain persistence/PDF/storage/scanner/replay/no-send
and complete cleanup evidence.

Validation: seven new orchestration controls and all 116 companion Python
controls passed on 2026-10-07. They use controlled calls and do not start hosts,
containers or a native SDK. Run standard Ubuntu-hosted native validation before
accepting this integration. No IAM owner files or financial runtime bundle files
were changed.
