# Dormant deployment contract

These service-owned manifests are dormant templates. They document the minimum runtime contract for the existing cluster and the `maliev-legacy` namespace; they are not a direct deployment mechanism. No direct deployment is authorized from this repository.

Central GitOps owns environment overlays and must replace the non-zero placeholder with the Trivy-scanned, immutable digest published for the reviewed release. A mutable image tag is never an acceptable release coordinate. The service is internal `ClusterIP` only, starts with one replica, and must run on existing capacity with no new node pool or other paid infrastructure.

## Runtime projection

Central GitOps projects the single Google Secret Manager JSON secret `maliev-legacy-secrets` into exactly one service-specific Kubernetes Secret named `legacy-maliev-quotation-runtime`. The existing dormant projection contains the following confidential/environment-specific API runtime coordinates:

- `ConnectionStrings__QuotationDbContext`
- `ConnectionStrings__QuotationRequestDbContext`
- `ConnectionStrings__redis`
- `Jwt__PublicKey`
- `Jwt__Issuer`
- `Jwt__Audience`
- `ServiceAuthentication__ClientSecret`

The API Deployment contains no migrator credentials. The current dormant projection supplies no signing private key, symmetric token key, analytics credentials, storage credential, or key file. Its dedicated `legacy-maliev-quotation` Kubernetes service account is tokenless and has no Google Cloud Workload Identity binding because this API does not access GCS.

## Consented analytics: unprovisioned configuration boundary

Issue #101 owns the implemented, separate default-off consented-delivery annex; issue #22 owns
its dormant deployment contract. The actual options bind the following environment names:

| Environment name | Existing runtime behavior |
| --- | --- |
| `GoogleAnalyticsMeasurementProtocol__Enabled` | Absent means disabled; credentials alone do not enable delivery. |
| `GoogleAnalyticsMeasurementProtocol__MeasurementId` | Required only when delivery is explicitly enabled. |
| `GoogleAnalyticsMeasurementProtocol__ApiSecret` | Required only when delivery is explicitly enabled; never commit its value. |

No analytics keys are currently supplied by the committed central runtime projection.
This document does not allocate new Secret Manager property names or assert that credentials
have been provisioned. A reviewed central projection and secure operator provisioning remain
future prerequisites, using the existing consolidated-secret ownership contract. Missing
analytics credentials must not become a new startup requirement while delivery is disabled.
When enabled, existing startup validation requires both credentials even in standby mode.

Only exact ordinal `MALIEV_OBSERVABILITY_STANDBY=true` suppresses the analytics hosted worker;
it does not bypass options validation, remove the store/processor or stop quotation expiry.
Consent admission, identifier privacy, durable leases/retries and terminal logging remain the
existing runtime contracts. No delivery enablement or provider call is authorized here.

The existing NetworkPolicy permits no external provider egress. A separately reviewed central
network contract is also required before enabling delivery. Keep the current internal-only
network policy unchanged until that work and the release gates below are complete. Documentation
and options-binding tests do not prove a deployed Secret, provider reachability or whole-source parity.

## Network assumptions

Ingress is restricted to the expected Web, Intranet BFF, and AccountingService consumers in `maliev-legacy`. AccountingService requires this boundary for invoice creation/linking and accepted-quotation processing. Kubernetes NetworkPolicy cannot meaningfully select kubelet-originated health probes; supported GKE networking permits node-local probe traffic independently, and central GitOps must verify that behavior in the selected dataplane before release.

Egress is restricted to cluster DNS, the same-namespace PostgreSQL/PgBouncer endpoint on 5432, the dedicated Redis workload on 6379, and the Auth and Order APIs on HTTP ports 80/8080. Both the Service port and pod target port are explicit because the NetworkPolicy evaluation point around Service DNAT varies by CNI. CloudNativePG owns the generated PgBouncer pod labels, so this base intentionally selects the `maliev-legacy` namespace for port 5432 instead of guessing operator labels. Central GitOps must verify the actual CloudNativePG pooler service, endpoint slices, Redis label, Auth label, Order label, and service target ports against the deployed environment.

## Release gate

Central GitOps may consume this template only after all of the following evidence exists:

1. DataMigration copy and reconciliation receipts for both quotation databases, including source/target counts, checksums, rejected-row evidence, and timestamped snapshots.
2. Restorable PostgreSQL snapshots and a rehearsed rollback path.
3. The consolidated-secret projection and the exact service-client permission are independently reviewed.
4. Aspire exercises both database contexts, Redis fail-closed behavior, Auth token exchange, Order decision calls, and all health probes against the release candidate.
5. Existing-cluster capacity evidence supports the resource envelope without creating infrastructure cost.
6. Service tests, security scans, image scans, central GitOps validation, and owner approval all pass.

Until those gates pass, the manifests remain planning artifacts and production rollout remains disabled.
