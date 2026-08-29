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
- `/quotationrequests/files[/{requestFileId}]` and `/quotationrequests/{requestId}/files`

The service preserves 33 actions, 34 route templates, PascalCase/null-omission JSON, named
routes, six-value sort enums, pagination fields, and the legacy 250-row safety ceiling. Every
action requires JWT authentication and an explicit permission.

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

An empty database may be initialized directly. A non-empty database fails closed unless it is
accompanied by a signed, unexpired production schema-baseline receipt. The signed payload is bound
to the exact workload, source snapshot, copy plan, schema hash, PostgreSQL host/port/database, and
expiry. Production composition must project the trusted RSA public key as a read-only file; an
absent, invalid, expired, tampered, or mismatched receipt is rejected before `MigrateAsync`.
Required configuration is `Migration__SourceSnapshotId`, `Migration__CopyPlanId`,
`Migration__SchemaHash`; optional file projections are `Migration__ReceiptPath` and
`Migration__TrustedPublicKeyPath`. `Migration__LockTimeoutSeconds` is bounded to 1-30 seconds.
Neither the receipt nor logs contain credentials or a raw connection string.

## Validate

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release
dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build
dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 1 --no-banner --no-color
```
