# Supplied collection page sizes

Original root `5fac706a7983a6d359b39acbd670e6800afe020e` has no parent. Its
`Maliev.QuotationService.Api/Controllers/QuotationsController.cs` and
`Maliev.QuotationRequestService.Api/Controllers/QuotationRequestsController.cs` pass
`size ?? await query.CountAsync()` into
`Maliev.Entities/ViewModels/PaginatedListWebApi.cs`. Neither controller nor helper
applies a positive-size upper bound. Current PostgreSQL collection reads restore
that supplied-positive-size behavior across global quotations, customer quotations,
and quotation requests. Omission still uses the filtered count. Accepted long-offset
and empty-page 404 behavior remains in place; the original unchecked multiplication
is not restored.

## Focused acceptance

Fifteen normal RS256 HTTP cases use the existing real PostgreSQL/Redis fixtures:
five sizes (`100`, `101`, `250`, `251`, `int.MaxValue`) on each of the three routes.
Each case seeds 261 matches plus excluded rows, checks both ID sort directions and
omitted/-1/0/1/2/3/int.MaxValue indexes, exact row IDs, counts/page metadata,
empty-page 404, absent PageSize wire field, anonymous 401 and permission-denied 403.
Customer cases exclude a same-marker row belonging to a different customer.
Request cases also retain journey and server-owned transaction readback.

The separate focused invocation must produce exactly the frozen fifteen joined,
passing test identities. Full suite, Release build, route/DTO and financial golden
regressions, both database suites, format/static/security checks and unchanged
coverage floors remain required on the exact proposed head. Existing independent
14 controller, 12 pagination and 12 omitted-size invocations are retained.

## Open full-range obligations

| Size/input class | Current behavior | Original evidence and required follow-up |
| --- | --- | --- |
| omitted, empty filtered set | 404 | Accepted empty-page regression; retain it. |
| omitted, nonempty filtered set | All filtered rows on page 1 | Accepted omission regression; retain it. |
| 1 through int.MaxValue | Supplied value, with accepted long-offset guard | Positive-cap slice; focused fifteen and full hosted checks required. |
| 0 | Accepted baseline normalizes to 1; #172 drafts count-first 404 | Pinned EF SQL Server 8.0.28 has the same zero-limit/zero-offset optimizer and official empty-result golden as 8.0.3. The source adaptation returns the existing empty 404 after counting. Hosted validation is pending; no historical SQL Server execution is claimed. |
| negative, including int.MinValue | Accepted baseline normalizes to 1; #172 drafts count-first failure | A zero filtered count returns 404 before paging. For a positive count, pinned EF retains a negative FETCH, which violates the SQL Server contract. Latest source Production middleware sets 500. The adaptation raises a fixed app-local collection-read exception through the existing private 500 mapper, without invalid PostgreSQL SQL or provider text. This is source-derived behavior, not an executed historical HTTP result. |
| malformed/out-of-int query value | Existing ASP.NET binding behavior | Preserve current binding tests; compare the original formatter/binder contract before claiming full-range equivalence. |

Future full-range acceptance must cover size omitted/int.MinValue/-1/0/1/100/101/
250/251/int.MaxValue, filtered counts 0/1/51/261, all three routes, both ID orders,
and index omitted/int.MinValue/-1/0/1/2/3/int.MaxValue. Original-provider outcomes
must be proved through permitted library/source-contract evidence before changing
the lower bound. Original SQL Server runtime access is excluded from this lane.
No whole-original migration closure follows from this positive-size slice.

Primary provider references, inspected at the original package pin:

- [EF Core SQL Server 8.0.3 parameter processor](https://github.com/dotnet/efcore/blob/v8.0.3/src/EFCore.SqlServer/Query/Internal/SqlServerParameterBasedSqlProcessor.cs)
- [EF Core SQL Server 8.0.3 zero-limit optimizer](https://github.com/dotnet/efcore/blob/v8.0.3/src/EFCore.SqlServer/Query/Internal/SkipTakeCollapsingExpressionVisitor.cs)
- [EF Core 8.0.3 relational Skip/Take translation](https://github.com/dotnet/efcore/blob/v8.0.3/src/EFCore.Relational/Query/RelationalQueryableMethodTranslatingExpressionVisitor.cs)
- [SQL Server OFFSET/FETCH contract](https://learn.microsoft.com/en-us/sql/t-sql/queries/select-order-by-clause-transact-sql?view=sql-server-ver16)

## Nonpositive-size source adaptation (#172)

This draft covers the coherent zero/negative family across global quotations,
customer quotations and quotation requests. It preserves count-first ordering:
count failure or cancellation propagates before size classification; no matches
retain the existing 404; explicit zero with matches also returns 404; a negative
size with matches raises `QuotationCollectionReadException`. Both controllers pass
the original nullable size through to the repository. Index normalization, positive
sizes, omission, long-offset guards, permissions, routes and response DTOs remain
unchanged. The current Production JSON error envelope is an explicit privacy
adaptation; the old empty provider-error response is not reproduced.

Original checkpoint `770cb58032969ae4a0fe169686b53ab61a44fff8` (parent
`f96a6cbe23407ac25af3ebc4bc669296a09ac164`) retains helper blob
`9684bc3f0ba2cd399621b3891c00b1b05ba6b398`. Its path history contains only root
`5fac706a7983a6d359b39acbd670e6800afe020e` (no parent). The ordinary collection
sections of both latest source controllers preserve the same ordered query,
helper call and null/empty 404. The held C6 controller delta was not adjudicated.

The latest source Data project pins EF Core SQL Server 8.0.28. Its relevant
translator, zero-limit optimizer, parameter processor, SQL generator and SQL
golden are byte-identical to the 8.0.3 inputs inspected for the positive-size slice:

- [8.0.28 zero-limit optimizer](https://github.com/dotnet/efcore/blob/983cf3c21c0f9484cc0ed66e61b8c252d701a30b/src/EFCore.SqlServer/Query/Internal/SkipTakeCollapsingExpressionVisitor.cs)
- [8.0.28 SQL generator](https://github.com/dotnet/efcore/blob/983cf3c21c0f9484cc0ed66e61b8c252d701a30b/src/EFCore.SqlServer/Query/Internal/SqlServerQuerySqlGenerator.cs)
- [8.0.28 parameterized zero golden](https://github.com/dotnet/efcore/blob/983cf3c21c0f9484cc0ed66e61b8c252d701a30b/test/EFCore.SqlServer.FunctionalTests/Query/NorthwindMiscellaneousQuerySqlServerTest.cs#L5968)

The original helper counts before page execution and constructs metadata afterward.
For zero, floating-point division yields infinity; the unchecked integer conversion
has no portable metadata value to preserve, and the empty collection is discarded
by the controller. Negative limits fail before constructor execution for nonempty
queries. The adaptation does not restore unchecked offset overflow.

Current ordinary CI pins ServiceDefaults
`f72be151917cb3962380971418c187e3df563f8d`; its exception middleware blob
`7af8790b4ea490670465758e0898ae309289ffcb` maps this plain exception subtype to
generic 500. It maps argument/invalid-operation exceptions to 400, so those are not
used to represent negative paging. The exception retains no request values,
provider details or inner exception.

Authored regressions reuse the existing DraftRead and RequestTriage fixtures:
counts 0/1/51/261, sizes int.MinValue/-1/0, both ID orders and index edge classes;
401/403 before database admission; count-only SQL observation; fixed private 500
wire; unchanged row snapshots; and synthetic count-failure/caller-cancellation
ordering. Two existing happy-path tests retain their positive search/index/wire
assertions with size 1 and gain explicit zero 404 assertions. All existing test
method identities remain. A deliberate synthetic count failure retains its own
existing 400 classification; it does not classify a negative size as 400.

Local build, tests, format, audits and security checks: **NOT RUN**. These are
unreferenced source blobs for review. Existing hosted Release, focused collection
and complete suite, route/DTO/golden/both-database, formatting/security and
unchanged raw coverage gates remain mandatory before merge and fresh main
acceptance. Upstream source and golden tests are evidence inputs, not newly
executed SQL Server or historical-host results. No database/provider execution,
new workflow, financial-stopped run, source retirement or deployment is authorized.
