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
| `GoogleAnalyticsPayloadFactoryTests.Build_WithoutSource_OmitsSourceReconciliationFields` | A quotation without a request source must not fabricate reconciliation identifiers. The fixture-free source contract is [QuotationAnalyticsSourceContractTests.Payload_WithoutSource_OmitsSourceReconciliationFields](../../Legacy.Maliev.QuotationService.Tests/Analytics/QuotationAnalyticsSourceContractTests.cs): retain the quotation transaction ID and omit both optional keys. This new test is a source proposal until it is integrated and executed. |
| `GetQualifiedOutcomeReadbackAsync_UnitTest.MissingOrPartialSourceKeys_CountAsUnattributedWithoutExposingIdentifiers` | Partial or missing source keys remain visible as an aggregate attribution gap. The current counterparts are `QuotationPostgresMigrationTests.OutcomeReadback_PartialSourceKeysRemainAggregateOnlyAndCountAsUnattributed` and `QuotationFinancialOutcomeReadbackHttpTests.Half_open_window_orders_days_and_currencies_and_counts_partial_keys_as_unattributed`. |

Existing accepted counterpart execution is recorded by the migration evidence, rather than by these documentation names. This document preserves the meanings and relocation map; it grants no generated-file retirement, whole original-change closure, new-head test pass, deployment, provider activation or financial execution.
