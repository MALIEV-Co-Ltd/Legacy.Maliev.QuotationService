# Legacy.Maliev.QuotationService

Public, sanitized .NET 10 compatibility extraction merging the legacy Quotation and
QuotationRequest APIs from the private `maliev-web` monorepo. It is independently buildable and
deployable while retaining separate database ownership and legacy HTTP/wire behavior.

The runtime consumes the public `Legacy.Maliev.ServiceDefaults` package while preserving the
existing `Maliev.Aspire.ServiceDefaults` CLR namespace. CI and image builds also use the public
`Legacy.Maliev.CompatibilityContracts` source repository, removing new-platform shared-library
source and private package credentials without changing quotation contracts.

## Preserved surface

- `/Quotations[/{quotationId}]`, `/Quotations/customers/{customerId}`,
  `/Quotations/invoices/{invoiceId}`, `/Quotations/stats`, and withholding-tax lookup
- `/quotations/orderitems[/{orderItemId}]` and `/quotations/{quotationId}/orderitems`
- `/quotations/orders[/{id}]` and `/quotations/{quotationId}/orders[/{orderId}]`
- `/quotations/files[/{quotationFileId}]` and `/quotations/{quotationId}/files`
- `/QuotationRequests[/{requestId}]`
- `/QuotationRequests/qualification-outcomes/readback` for bounded, employee-only qualification receipts
- `/quotationrequests/files[/{requestFileId}]` and `/quotationrequests/{requestId}/files`

The service preserves the original route templates, named routes, six-value sort enums,
pagination fields, and omitted-size behavior: the first page contains all matching rows after
customer/search filtering, with no substituted default or cap. Supplied positive sizes pass through
without an upper cap on global quotations, customer quotations, and requests. Supplied zero and
negative sizes still normalize to 1; original SQL Server provider behavior for these values remains
an open parity obligation. Most JSON remains PascalCase with
null omission; the qualification-outcome readback intentionally retains the source's camelCase,
null-omission receipt wire. Every action requires JWT authentication and an explicit permission.

## Financial and document behavior

- PostgreSQL retains `numeric(18,2)` money fields and database-computed line `Subtotal` and
  quotation `QuotedAmount` columns.
- Withholding-tax calculation preserves the historical 1.5% interval and current 3% behavior;
  golden boundary tests prevent silent financial drift.
- A deterministic `QuotationDocumentSnapshot` combines the quotation, lines, and GCS metadata
  for the later QuestPDF migration without reading Customer, Material, or Order databases.
- The expiry worker atomically marks open quotations declined every four hours and logs only
  meaningful changes/errors.
- Quotation and request creates support SHA-256-keyed Redis idempotency. Quotation, line, and
  request updates support `X-Expected-Modified-Date` and return HTTP 409 for stale writes.

## Quotation decision workflow

Quotation decisions use the server-side `ServiceAuthentication` client identity to call
`Services:Order` through the legacy AuthService token exchange. Credentials are runtime-only;
the QuotationService identity needs only `legacy.order-status.write`. `PUT
/quotations/{quotationId}/decision` uses quotation optimistic concurrency and deterministic
OrderService idempotency keys so an interrupted multi-order decision can be retried safely.

## Privacy-safe marketing reconciliation

`GET /Quotations/outcomes/readback` preserves the source system's employee-only,
31-day aggregate reconciliation boundary. Each UTC day reports persisted and first-accepted
quotation totals split into fully source-attributed and unattributed counts. A quotation is
source-attributed only when both its opaque request identifier and privacy-safe journey UUID are
present; partial keys remain unattributed. The response never exposes either identifier, customer
or employee identifiers, contact data, event keys, or acceptance-origin detail.

The immutable accepted-outcome record remains the provider-neutral business fact. The service also
implements a separate default-off, consent-governed delivery annex under issue #101. Aggregate
readback does not reconstruct consent, delivery identifiers, or a provider payload. Missing optional
consent context creates no delivery intent; later acceptance replay does not reconstruct it.
The PostgreSQL outbox, validated options, retry/lease fences, privacy guards and exact standby
behavior remain separate from neutral acceptance and the always-registered expiry worker.

Delivery is disabled by default. Its existing configuration names are
`GoogleAnalyticsMeasurementProtocol__Enabled`,
`GoogleAnalyticsMeasurementProtocol__MeasurementId` and
`GoogleAnalyticsMeasurementProtocol__ApiSecret`. Enabled delivery requires both credentials even
in exact standby mode. The dormant deployment does not currently project these settings or permit
provider egress. Their reviewed central projection, secure operator provisioning and network
contract remain prerequisites under issues #22/#101, not evidence that credentials already exist.
See [the dormant runtime contract](deploy/README.md); this repository change does not enable delivery.

Original source `b60778f9cc03f6a591b5ee03b71cf12f999a604a`, parent
`2bb747212f8c722115e53691e10a522d8a793cf7`, added these static references to
`Maliev.QuotationService.Api/deployment.yaml` (names only, not secret values):

| Original environment setting | Original Secret / key | Migration disposition |
| --- | --- | --- |
| `ConnectionStrings__QuotationDbContext` | `maliev-quotationservice-runtime` / `quotation-connection-string` | Separate PostgreSQL Quotation/QuotationRequest connections through the existing target runtime projection. |
| `ConnectionStrings__LogDbContext` | `maliev-quotationservice-runtime` / `log-connection-string` | SQL Server/NLog database logging is replaced by native private diagnostics; no logging database connection is restored. |
| `JwtSecurity__SigningKey` | `maliev-quotationservice-runtime` / `jwt-signing-key` | RSA public-key validation replaces symmetric signing; no private or symmetric key is projected. |

The original deployment test also retained the pre-existing
`maliev-ga4-measurement-protocol` references: `enabled`, `measurement-id` and `api-secret`
for those three analytics environment settings. These original static names do not establish
new target Secret Manager properties or a live projection. The source test's generated XML added
only its type/method summaries, with no runtime consumer; source `03dc9a1271c16e6535934445e9dd6e3f30e8fffe`
later removed that generated file. Compiler-owned output and the existing six-component real
Release XML-output tests preserve documentation emission without restoring source-root XML.
The deployment binding and provider prerequisites remain open; this mapping does not close b60778f.

The lossless source-fact import boundary is documented in
[`docs/legacy-outcome-adoption-contract.md`](docs/legacy-outcome-adoption-contract.md).

## Data ownership and deployment gate

- Planned existing-cluster target: `legacy-postgres-quotation` in `maliev-legacy`.
- Database `Quotation`: `Quotation`, `OrderItem`, `QuotationFile`, `QuotationHasOrder`.
- Database `QuotationRequest`: `Request`, `RequestFile`.
- `CustomerID`, `EmployeeID`, `CurrencyID`, `InvoiceID`, and `OrderID` remain external scalar
  references. No cross-domain database access or FK is introduced.
- Files store GCS bucket/object metadata only; object access uses ADC/Workload Identity.
- Source SQL Server remains untouched.

Extraction does not deploy. Cutover requires repeatable copy/parity/rollback artifacts for both
databases, golden PDF comparison, GCS reconciliation, Web/Intranet consumer tests, a dedicated
legacy Workload Identity, and GitOps manifests. It must use the existing cluster with no new node
pool, Cloud SQL, or other paid service.

## Dormant schema migration runner

`Legacy.Maliev.QuotationService.MigrationRunner` is a service-owned .NET 10 executable and
dedicated non-root container image. It is intentionally dormant: this repository contains no Job,
CronJob, publish, or deployment command. Central GitOps may add a one-shot Job only after the
DataMigration copy/parity gate, immutable image digest, capacity review, Aspire validation, and
owner approval are complete.

The workload is selected exactly once with the `quotation` or `quotation-request` argument (or
`Migration__Workload`). Only the selected `ConnectionStrings__QuotationDbContext` or
`ConnectionStrings__QuotationRequestDbContext` value is required or opened. The runner acquires a
context-specific PostgreSQL advisory lock before inspecting or changing schema and releases it on
success or failure. It applies existing EF Core migrations only; it does not seed, copy, truncate,
or downgrade data.

Before opening any database connection, every run requires a signed, unexpired recoverable PostgreSQL snapshot receipt.
Its domain-separated payload binds the workload, canonical run UUID, source snapshot, copy plan, schema hash,
snapshot identifier, UTC recovery point, immutable backup object URI/generation/byte length/SHA-256,
CloudNativePG cluster namespace/name/UID/generation, target host/port/database, trust key, and expiry.
The runner independently observes the fixed CloudNativePG target twice and requires identical, healthy observations
matching the signed UID and generation immediately before opening the PostgreSQL connection.
Snapshot and schema-baseline trust roles must use distinct P-256 public keys. A non-empty database additionally fails
closed unless it is accompanied by a signed, unexpired production schema-baseline receipt. The ECDSA P-256 trust model
and externally projected trusted key identifier match the DataMigration attestation boundary. The versioned receipt schema
is parsed strictly, then re-derived into a domain-separated canonical binary payload before signature verification; JSON
property order and insignificant whitespace cannot change the signed meaning. The signed payload is bound
to the exact workload, source snapshot, copy plan, schema hash, PostgreSQL host/port/database, and
expiry. Production composition must project exactly one trusted ECDSA P-256 SPKI `PUBLIC KEY` as a read-only file; an
absent, invalid, expired, tampered, duplicate/unmapped, or mismatched receipt is rejected before `MigrateAsync`.
Required configuration is `Migration__SourceSnapshotId`, `Migration__CopyPlanId`, `Migration__SchemaHash`,
`Migration__TrustedKeyId`, `Migration__RunId`, and `Migration__SnapshotTrustedKeyId`. Snapshot receipt and snapshot
public-key file projections are required for execution through `Migration__SnapshotReceiptPath` and
`Migration__SnapshotTrustedPublicKeyPath`; schema-baseline projections remain `Migration__ReceiptPath` and
`Migration__TrustedPublicKeyPath`. `Migration__ClusterNamespace` and `Migration__ClusterName` identify the exact
CloudNativePG resource. `Migration__LockTimeoutSeconds` is bounded to 1-30 seconds.
Neither the receipt nor logs contain credentials or a raw connection string.

## Validate

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release
dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build
dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 1 --no-banner --no-color
```

CI also runs the complete test assembly with the `XPlat Code Coverage` Cobertura collector,
then enforces at least 80% line coverage across this repository's handwritten `Api`,
`Application`, `Data`, `Domain`, and `MigrationRunner` assemblies. The gate is implemented in
[`scripts/check_owned_coverage.py`](scripts/check_owned_coverage.py) and fails if any owned
assembly is missing from the report. It prints covered/total lines for every assembly, including
the separately owned `Legacy.Maliev.ServiceDefaults` and
`Legacy.Maliev.CompatibilityContracts` dependencies. Only those two sibling assemblies are
outside the owned denominator; no handwritten QuotationService file is excluded. Lines whose
source path is under an `obj` directory are reported as generated lines but excluded from the
handwritten denominator. The API assembly's handwritten coverage remains visible separately
from the service-wide gate, so passing 80% overall does not imply its endpoint coverage is 80%.
The coverage command selects the same local sibling-project graph as the shared validation
action with the MSBuild property `-p:GITHUB_ACTIONS=false`; it does not override the test
process's `GITHUB_ACTIONS` environment variable.

To reproduce the gate locally, run the full test assembly with
`--collect "XPlat Code Coverage" --results-directory TestResults/CoverageGate`, then pass the
single generated `coverage.cobertura.xml` to
`python scripts/check_owned_coverage.py <report> --minimum 80`. Run
`python -m unittest discover -s scripts/tests -p 'test_*.py'` to demonstrate that the gate
rejects a below-threshold report and accepts a qualifying report. The earlier
`/p:Threshold=80` collector property is not a working gate in this setup.
