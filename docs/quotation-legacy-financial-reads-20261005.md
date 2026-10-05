# Legacy financial read-route acceptance

This independent bundle adds nine actual Production Program/DI, signed JWT, real live IAM
client with controlled remote transport, PostgreSQL 18 and Redis cases for the three retained
read routes: statistics, invoice-reference lookup and withholding amount. Existing qualified
outcome readback and deterministic calculator/repository suites remain intact.

Statistics count the stored nullable Accepted flags as accepted/declined/open. Historical
fixtures with those flags do not constitute real first acceptance, qualification or analytics
intent. Delta counts and exact PascalCase shape are checked through HTTP. Invoice lookup
checks the external scalar reference, persisted quotation/decimal readback and null omission;
it does not validate a real invoice or an Accounting authorization/creation flow.

Withholding cases use supported TimeProvider injection and five exact UTC points before/at
the 2020-04-01 start, before/at the 2020-09-30 exclusive end and outside that historical window.
For Subtotal 100.50 the preserved source implementation yields 1.5075 or 3.015 without rounding,
independently of the stored WithholdingTax field 9.99. This is historical software compatibility,
not tax/legal guidance or a new policy. Missing rows/references remain 404. All three routes
are checked under missing JWT and live IAM denial with exact entry counts, no financial
disclosure and unchanged persisted metadata/outcomes/analytics intent.

Source `5fac706a7983a6d359b39acbd670e6800afe020e` has no parents. The root quotation
controller blob is `f7de54587ff77294289ed9335b19f0a981d625d0`; final checkpoint
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f` controller blob is
`a598e44d3d39d8c7cc882dca2552cfde97af81f9`. The final controller retains the raw flag
statistics, scalar invoice predicate, subtotal multiplied by the private rate helper, and
the exact inclusive/exclusive UTC cutoffs. Original invoice/statistics source tests and the
financial calculation method are inspected for the bounded mapping. Later acceptance/
qualified-outcome obligations retain separate mappings; no whole SHA/path or issue closure.

Only this note and a test file change. No runtime, route, DTO, schema, default feature,
dependency pin, production grant, local SDK/Docker, deployment, persistent-data operation,
provider/customer effect or original-source write. Hosted Release/full/raw coverage/format/
audit/security/joined and protected/exact-main acceptance remain pending.
