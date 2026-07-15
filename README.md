# Legacy.Maliev.QuotationService

Public, sanitized .NET 10 compatibility extraction merging the legacy Quotation and
QuotationRequest APIs from the private `maliev-web` monorepo. It is independently buildable and
deployable while retaining separate database ownership and legacy HTTP/wire behavior.

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

## Validate

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release
dotnet test Legacy.Maliev.QuotationService.slnx -c Release --no-build
dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 1 --no-banner --no-color
```
