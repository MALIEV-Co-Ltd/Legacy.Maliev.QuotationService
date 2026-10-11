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
| 0 | Still normalizes to 1 | EF SQL Server 8.0.3 has a zero-limit/zero-offset optimizer that removes limits and adds a false predicate. Source predicts empty items/404, but the exact source expression/provider/HTTP chain is not executed here. Keep this mismatch open. |
| negative, including int.MinValue | Still normalizes to 1 | EF translates limits/offsets; SQL Server requires nonnegative OFFSET and positive FETCH. Exact parameter optimization, overflow and original error/HTTP behavior remain unproved. Do not infer IQueryable behavior from Enumerable.Take. |
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
