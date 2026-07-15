# Legacy.Maliev.QuotationService agent guidance

## Boundaries

- Preserve 33 approved controller actions and 34 route templates across quotations, lines,
  order links, quotation files, quotation requests, and request files.
- Keep `Quotation` and `QuotationRequest` as separate databases and DbContexts on the same
  planned `legacy-postgres-quotation` cluster. Do not add cross-database foreign keys.
- `CustomerID`, `EmployeeID`, `CurrencyID`, `InvoiceID`, and `OrderID` are external scalar
  references. Resolve rich data through APIs or immutable snapshots, never another DbContext.
- Preserve decimal(18,2), computed `OrderItem.Subtotal`, computed `Quotation.QuotedAmount`,
  withholding-tax date behavior, statistics, and expiry transitions with golden tests.
- GCS rows contain bucket/object metadata only and runtime access uses ADC/Workload Identity.
- Never copy legacy connection strings, NLog database logging, credentials, service-account
  files, signed URLs, access keys, or source configuration into this public repository.

## Runtime constraints

- .NET 10, Scalar/OpenAPI, Npgsql, Redis, standard MALIEV service defaults, and built-in
  `ILogger<T>` are required.
- Run only in the existing GKE cluster and `maliev-legacy` namespace. No new node pool,
  Cloud SQL, or other paid infrastructure.
- All routes remain authenticated and permission-protected. Financial/destructive writes use
  live checks; retried creates use hashed Redis idempotency keys; mutable financial rows use
  `ModifiedDate` optimistic concurrency without new source columns.

## Validation and commits

- Contract changes require route/DTO snapshots, financial golden cases, and PostgreSQL 18 tests
  for both databases. Document-snapshot and expiry behavior must remain covered.
- Run release build, tests, format verification, package vulnerability audit, and gitleaks.
- Commit coherent validated slices and preserve source SQL Server unchanged.
