# Quotation source attribution: preserved documented members

This preserves the documented member contracts from original change `c6ac93af03a0dafc506d9570aca96e4aed3b1643` after the Quotation code moved into the current API, Application, Domain and test projects. Original compiler-generated API/Data/Tests XML files are retained in the source evidence; they are not copied into the current source tree as hand-maintained build outputs. All three current project files enable `GenerateDocumentationFile`. That setting describes generation, not byte-identical XML member names after type and namespace changes.

## Readback counters

The original API XML members under `Maliev.QuotationService.Api.Models.QualifiedOutcomeReadbackDay` map to the same four `int` properties on [QuotationOutcomeReadbackDay](../../Legacy.Maliev.QuotationService.Application/Models/QuotationModels.cs).

| Property | Preserved documented meaning |
| --- | --- |
| SourceAttributedPersistedQuotationCount | Persisted quotations carrying both source reconciliation keys. |
| SourceAttributedAcceptedQuotationCount | Accepted quotations carrying both source reconciliation keys. |
| UnattributedPersistedQuotationCount | Persisted quotations missing one or both source reconciliation keys. |
| UnattributedAcceptedQuotationCount | Accepted quotations missing one or both source reconciliation keys. |

Persisted attribution uses the quotation's source keys; acceptance attribution uses the immutable accepted outcome's source keys. Readback exposes aggregate counts without exposing the identifiers. Quoted amounts are quotation values grouped by source currency; they are not revenue. The current [repository](../../Legacy.Maliev.QuotationService.Data/QuotationRepositories.cs) supplies the values. [Financial outcome HTTP tests](../../Legacy.Maliev.QuotationService.Tests/Controllers/QuotationFinancialOutcomeReadbackHttpTests.cs) and [PostgreSQL readback tests](../../Legacy.Maliev.QuotationService.Tests/Data/QuotationPostgresMigrationTests.cs) preserve the fully attributed and partial/missing-key oracles.

## Outbox properties

The original Data XML members under `Maliev.QuotationService.Data.Database.QuotationContext.GoogleAnalyticsOutbox` map to [Domain.GoogleAnalyticsOutbox](../../Legacy.Maliev.QuotationService.Domain/GoogleAnalyticsOutbox.cs).

| Property | Type | Preserved documented meaning |
| --- | --- | --- |
| SourceRequestId | `int?` | Originating quotation-request identifier. |
| SourceJourneyId | `Guid?` | Privacy-safe originating journey identifier. |

The physical columns remain `SourceRequestID` and `SourceJourneyID`. The original SQL source excludes nulls from each independent reconciliation index. The PostgreSQL source adaptation uses quoted physical names with `IS NOT NULL`; it does not change source values or deliver events. Source-only migration and model changes require their normal validation before publication or execution.

## Relocated test documentation

| Original documented test | Preserved contract and current counterpart |
| --- | --- |
| `GoogleAnalyticsPayloadFactoryTests.Build_WithoutSource_OmitsSourceReconciliationFields` | A quotation without a request source must not fabricate reconciliation identifiers. The fixture-free source contract is [QuotationAnalyticsSourceContractTests.Payload_WithoutSource_OmitsSourceReconciliationFields](../../Legacy.Maliev.QuotationService.Tests/Analytics/QuotationAnalyticsSourceContractTests.cs): retain the quotation transaction ID and omit both optional keys. This test is implemented and executed in the independent four-case C6 focus described below; it is no longer an unexecuted source proposal. |
| `GetQualifiedOutcomeReadbackAsync_UnitTest.MissingOrPartialSourceKeys_CountAsUnattributedWithoutExposingIdentifiers` | Partial or missing source keys remain visible as an aggregate attribution gap. The current counterparts are `QuotationPostgresMigrationTests.OutcomeReadback_PartialSourceKeysRemainAggregateOnlyAndCountAsUnattributed` and `QuotationFinancialOutcomeReadbackHttpTests.Half_open_window_orders_days_and_currencies_and_counts_partial_keys_as_unattributed`. |

Existing accepted counterpart execution is recorded by the migration evidence, rather than by these documentation names. This document preserves the meanings and relocation map; it grants no generated-file retirement, whole original-change closure, new-head test pass, deployment, provider activation or financial execution.

## Executed C6 source evidence and remaining original scope

Accepted [PR #150](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/pull/150) main `56da221cc384e2371e6fcb42419776cd32d0ffb0` executed all four independent C6 cases with zero failures or skips in [run 38552372342](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/actions/runs/38552372342). Its complete suite passed 1,398 cases twice. The four cases are:

- `QuotationAnalyticsSourceContractTests.Payload_WithoutSource_OmitsSourceReconciliationFields`
- `QuotationAnalyticsSourceContractTests.Outbox_Model_PreservesNullableSourceColumnsAndPartialIndexes`
- `QuotationAnalyticsSourceContractTests.SourceIndexMigration_OnlyReplacesTheTwoIndexesAndRestoresNames`
- `QuotationPostgresMigrationTests.AnalyticsSourceIndexMigration_GeneratesScriptsAndPreservesRowsAcrossUpgradeRollbackAndReupgrade`

After [PR #161](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/pull/161), main `34b45bbed74a027ccac010b091a602de4be63579` ordinary job `115850479716` in [run 38563571024](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/actions/runs/38563571024) again executed these same four cases with zero failures or skips. Its complete suite passed 1,423 cases twice; the existing baseline identities were retained. This is code-contract evidence using controlled fixtures. The same-main joined job failed before its application checks on registry index-manifest HTTP 429, so overall exact-main joined-environment acceptance remains pending. Neither the ordinary result nor the previous accepted C6 slice supplies production database, provider or genuine eight-host financial execution authority.

Original `c6ac93af03a0dafc506d9570aca96e4aed3b1643`, parent `25418c95b5ac79400029ce274541f0e51728da3e`, changed 34 paths: twelve Quotation paths, one SQL path, seventeen Web paths and four Workflows documents. This document and the four C6 tests preserve only the admitted Quotation attribution/index assertions and selected SQL adaptation. They do not close that full original change.

| Remaining source obligation | Explicit hold |
| --- | --- |
| `Maliev.QuotationService.Api/Controllers/QuotationsController.cs` | The held original controller delta has not received semantic intake/adjudication; current endpoint behavior and runtime results do not substitute for exact original delta parity. |
| `Maliev.QuotationService.Api/Models/QualifiedOutcomeReadback.cs` | The held original DTO/wire delta remains unadjudicated; the current aggregate model is a counterpart, not whole-file equivalence proof. |
| `Maliev.QuotationService.Tests/Quotations/AcceptQuotationAsync_UnitTest.cs` | Held original acceptance assertions remain unadjudicated; existing acceptance/outbox regressions do not retire this source row. |
| `Maliev.QuotationService.Tests/Quotations/GetQualifiedOutcomeReadbackAsync_UnitTest.cs` | Held original readback assertions remain unadjudicated; the admitted XML summary above preserves its meaning without claiming intake of that test body. |
| Original API/Data/Tests generated XML | The six-member semantic relocation map is preserved here. Byte-identical generated XML names and explicit generated-artifact retirement/disposition remain separate, unapproved obligations. |
| `Maliev.SqlServer/Deployments/2026-08-30-ga4-quotation-source-reconciliation.sql` | The selected nullable-column/non-null index witness is adapted and tested in PostgreSQL source. DataMigration ownership and the original SQL artifact's disposition remain separate; no live SQL Server or production migration is authorized here. |
| Seventeen original `Maliev.Web` / `Maliev.Web.Tests` paths | Existing Web owner must qualify the exact current-main public/contact/LIFF hooks, browser/privacy contracts, source/test relocations and generated-document disposition. An absent literal filename is not missing-behavior or retirement proof. |
| Four original Workflows documents | `docs/analytics/2026-08-28-persisted-lead-channel-reconciliation.md`, `docs/seo/2026-08-30-search-owner-and-local-coverage-audit.md`, `docs/superpowers/plans/2026-08-30-marketing-measurement-and-search.md`, and `docs/superpowers/specs/2026-08-30-marketing-measurement-and-search-design.md` still require owner content/relocation or explicit disposition evidence. |

The retained per-path assessment preserves all 34 full original paths and ownership. These holds remain open; this documentation correction makes no source-ledger update, original retirement, whole Quotation completion, deployment, historical daemon-lease discharge or financial execution claim.
