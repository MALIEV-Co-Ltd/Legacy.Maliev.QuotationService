# Quotation private startup and request observation

This slice explicitly selects the private console formatter and request observation already supplied by reviewed Defaults `ecb05cbbd68717e415f69df2ac488c1d323b1da3`. It does not change dependency pins, JWT/grant behavior, GA payloads, retries, persistence or deployment settings.

Program registers `PrivateFailureConsoleFormatter`, selects its console name, constrains console output to Warning and above, and selects the `quotation` request/health prefix. The standard middleware runs the diagnostic admission boundary before forwarded-header rewriting. The controlled probe requires direct loopback, GET and exactly one GUID-N nonce; it produces Warning/Error/Critical, a private nonce response and per-host one-minute throttling. Registered health GET responses carry host-local instance/no-store headers; completed server failures are observed, while thrown failures remain owned by the existing exception boundary. Health fingerprints throttle for five minutes and reset quietly on recovery.

QuotationStartupBoundary retains its executable entry-assembly and `HostAbortedException` guards. Inside that guard it calls the producer's `PrivateStartupBoundary.ReportFailure`, which synchronously writes safe private JSON to stderr and sets exit code one. It deliberately does not use the broad generic `RunAsync` catch around test-host discovery. The three real executable startup cases now check the private wire shape and safe exception type across stdout/stderr.

Private console JSON has a finite allowlist, discarding arbitrary formatted text and structured state/scopes. The previous cloud formatter already constrained exception text; this change does not claim a proven pre-existing leak. GA logger calls remain unchanged: null exception, exact safe fields and one owner after durable persistence. Rendered GA JSON now exposes safe EventName/Dependency/Operation/StatusCode/AttemptCount fields directly; arbitrary structured `ExceptionType` is omitted and runtime `exceptionType` is null. Existing GA regression assertions retain the logger field contract and explicitly check this private rendered shape. No generic private HTTP observer is selected for GA or other clients in this slice, so accepted fallback/terminal/cancellation ownership remains intact. OpenTelemetry registration is unchanged; private console formatting is not a claim about remote sink ingestion.

## Source mapping

Source `a59193ae2ac030d0e1d373ce4ec50c1b35437391`, parent `8175e8f3383d31030de2321c480bf6f5bc0fbb2b`, is scoped provenance, not whole-SHA closure.

| Original path pair (QuotationService and QuotationRequestService) | Modern disposition |
| --- | --- |
| `Maliev.QuotationService.Api/Program.cs`, `Maliev.QuotationRequestService.Api/Program.cs` | Combined API Program selects private console/request observation; startup reporting retains caller-specific guards. Broad all-client observer registration remains deferred. |
| `Maliev.QuotationService.Api/Startup.cs`, `Maliev.QuotationRequestService.Api/Startup.cs` | Selected existing SDK request/health/controlled-probe pipeline in combined API. Original custom middleware is not copied. |
| `Maliev.QuotationService.Api/Maliev.QuotationService.Api.csproj`, `Maliev.QuotationRequestService.Api/Maliev.QuotationRequestService.Api.csproj` | Existing immutable Defaults project input supplies shared runtime types; original linked source/project layout is not copied. |
| `Maliev.QuotationService.Api/deploy.ps1`, `Maliev.QuotationRequestService.Api/deploy.ps1` | Out of scope; no release or infrastructure changes, no retirement inferred. |

Shared source `tools/diagnostics/ProductionDiagnostics.cs`, `ProductionDiagnosticsRegistration.cs`, `ProductionObservabilityMiddleware.cs`, `ProductionStartupBoundary.cs`, README and tests are read-only compatibility evidence. Exact path/blob content is retained in the lane's `outputs/quotation-next-a591-source-evidence.json`. GA source `27e4ea41f49c6a9bffe3829b0ba3489b9b63f3de` terminal ownership already accepted by #113/#114 remains retained, not reopened or retired.

## Validation boundary

New actual Program tests cover private formatter/state/scope privacy, source diagnostic admission and forwarded-header exclusion, loopback pipeline/severities/rate limit, host-local health identity, readiness throttling/recovery, completed 5xx versus thrown owner, and caller startup guard/quiet success. They use the existing real PostgreSQL 18/Redis fixture; only an explicitly registered test-only controller and a controlled health check provide deterministic responses. Formatter capture invokes the actual registered formatter with real logger entries/scopes; it does not claim stdout/cloud ingestion. Existing GA25, worker6, standby5, workload15 and real executable startup3 remain required regressions, alongside the historical Auth3 gate.

The requested deliverable is an uncommitted review diff. Local SDK/Docker execution is prohibited in this lane; no build/test/format acceptance is claimed. Hosted build zero warnings/errors, focused tests, full suite twice, historical Auth join, static checks and native raw five-assembly coverage remain required before any candidate/main acceptance. No peer pins, provider calls/retirement, parent ledger or original-source writes, production persistence or deployment. Owner Aspire and explicit release authority remain required.
