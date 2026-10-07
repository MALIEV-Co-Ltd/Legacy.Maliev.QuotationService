// Generated only from exact public Git migration/type declarations by inspect_exact_sources.py.
// No runtime source graph or physical business schema acceptance is inferred.
namespace FinancialCompositionAdapter;
internal sealed record PinnedBusinessSchema(string ContextFullName, string AssemblyName, string SourceCommit, string[] Migrations);
internal static class PinnedBusinessSchemas
{
    internal static readonly IReadOnlyDictionary<string, PinnedBusinessSchema> Contracts = new Dictionary<string, PinnedBusinessSchema>(StringComparer.Ordinal)
    {
        ["AUTH_CUSTOMER_IDENTITY"] = new(
            ContextFullName: "Legacy.Maliev.AuthService.Infrastructure.CustomerIdentityDbContext",
            AssemblyName: "Legacy.Maliev.AuthService.Infrastructure",
            SourceCommit: "88e430946465a1df6238e10b815d495d43453411",
            Migrations:
            [
                // Immutable EF migration identifier.
                "202607150001_InitialCustomerIdentityPostgres",
                // Immutable EF migration identifier.
                "202609070001_AddCustomerPasswordSetupLifecycle",
                // Immutable EF migration identifier.
                "202609260001_AddCustomerIdentityCreateOperations",
            ]),
        ["AUTH_EMPLOYEE_IDENTITY"] = new(
            ContextFullName: "Legacy.Maliev.AuthService.Infrastructure.EmployeeIdentityDbContext",
            AssemblyName: "Legacy.Maliev.AuthService.Infrastructure",
            SourceCommit: "88e430946465a1df6238e10b815d495d43453411",
            Migrations:
            [
                // Immutable EF migration identifier.
                "202607150002_InitialEmployeeIdentityPostgres",
                // Immutable EF migration identifier.
                "202609300001_AddEmployeeRecoveryEffects",
            ]),
        ["AUTH_SESSIONS"] = new(
            ContextFullName: "Legacy.Maliev.AuthService.Infrastructure.RefreshSessionDbContext",
            AssemblyName: "Legacy.Maliev.AuthService.Infrastructure",
            SourceCommit: "88e430946465a1df6238e10b815d495d43453411",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715104357_InitialRefreshSessions",
                // Immutable EF migration identifier.
                "20260715140429_AddIdentityActionTokens",
                // Immutable EF migration identifier.
                "20260726184305_AddGoogleIdentityNonces",
                // Immutable EF migration identifier.
                "20260727193121_AddIdentityActionTokenTargetEmail",
                // Immutable EF migration identifier.
                "20260930125706_AddEmployeeRecoveryBinding",
            ]),
        ["PAYMENT"] = new(
            ContextFullName: "Legacy.Maliev.AccountingService.Data.PaymentDbContext",
            AssemblyName: "Legacy.Maliev.AccountingService.Data",
            SourceCommit: "668f2cb63c64b911db776329b983dd91944b3b8c",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715055536_InitialPostgres",
                // Immutable EF migration identifier.
                "20260721024322_FixTimestampColumnType",
                // Immutable EF migration identifier.
                "20261006180000_RequirePaymentFileMetadata",
                // Immutable EF migration identifier.
                "20261007100000_RequirePaymentMasterSourceStrings",
            ]),
        ["INVOICE"] = new(
            ContextFullName: "Legacy.Maliev.AccountingService.Data.InvoiceDbContext",
            AssemblyName: "Legacy.Maliev.AccountingService.Data",
            SourceCommit: "668f2cb63c64b911db776329b983dd91944b3b8c",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715055541_InitialPostgres",
                // Immutable EF migration identifier.
                "20260721024204_FixTimestampAndInvoiceColumnCasing",
                // Immutable EF migration identifier.
                "20260905113336_AddInvoiceMarketingAttribution",
                // Immutable EF migration identifier.
                "20260928094829_AddInvoiceCreationAdmission",
                // Immutable EF migration identifier.
                "20261001105037_AddInvoiceNotificationCorrelation",
                // Immutable EF migration identifier.
                "20261001133820_RetainInvoiceNotificationReceipt",
                // Immutable EF migration identifier.
                "20261005210000_RetainInvoiceCreationOriginAndFinancialResult",
                // Immutable EF migration identifier.
                "20261006140000_RetainInvoiceFinancialOwnership",
                // Immutable EF migration identifier.
                "20261006160000_RequireFileMetadata",
                // Immutable EF migration identifier.
                "20261007100000_RequireInvoiceMasterSourceStrings",
            ]),
        ["RECEIPT"] = new(
            ContextFullName: "Legacy.Maliev.AccountingService.Data.ReceiptDbContext",
            AssemblyName: "Legacy.Maliev.AccountingService.Data",
            SourceCommit: "668f2cb63c64b911db776329b983dd91944b3b8c",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715055545_InitialPostgres",
                // Immutable EF migration identifier.
                "20260721024327_FixTimestampColumnType",
                // Immutable EF migration identifier.
                "20260927184934_PreserveNullableAmountPaid",
                // Immutable EF migration identifier.
                "20261006160000_RequireFileMetadata",
                // Immutable EF migration identifier.
                "20261007100000_RequireReceiptMasterSourceStrings",
            ]),
        ["QUOTATION"] = new(
            ContextFullName: "Legacy.Maliev.QuotationService.Data.QuotationDbContext",
            AssemblyName: "Legacy.Maliev.QuotationService.Data",
            SourceCommit: "bfa31128fea506bc638cce7cabd7519356d5051c",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715051625_InitialQuotationPostgresCompatibility",
                // Immutable EF migration identifier.
                "20260721032128_FixTimestampColumnType",
                // Immutable EF migration identifier.
                "20260829074943_AddAcceptedQuotationOutcome",
                // Immutable EF migration identifier.
                "20260830071526_PreserveOutcomeTimestampPrecision",
                // Immutable EF migration identifier.
                "20260927050000_PreserveHistoricalAcceptedUtc",
                // Immutable EF migration identifier.
                "20260930070000_PreserveDecisionOrderVersion",
                // Immutable EF migration identifier.
                "20261004063000_AddConsentedAcceptanceIntent",
                // Immutable EF migration identifier.
                "20261006080000_QuotationInvoiceCompletionOperations",
            ]),
        ["QUOTATION_REQUEST"] = new(
            ContextFullName: "Legacy.Maliev.QuotationService.Data.QuotationRequestDbContext",
            AssemblyName: "Legacy.Maliev.QuotationService.Data",
            SourceCommit: "bfa31128fea506bc638cce7cabd7519356d5051c",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715051630_InitialQuotationRequestPostgresCompatibility",
                // Immutable EF migration identifier.
                "20260720112732_AddRequestCreateIdempotency",
                // Immutable EF migration identifier.
                "20260721032134_FixTimestampColumnType",
                // Immutable EF migration identifier.
                "20260906120000_AddRequestJourneyId",
                // Immutable EF migration identifier.
                "20260912163831_AddRequestQualificationContract",
                // Immutable EF migration identifier.
                "20260924084450_AlignQualificationPrecisionWithSqlServer",
            ]),
        ["ORDER"] = new(
            ContextFullName: "Legacy.Maliev.OrderService.Data.OrderDbContext",
            AssemblyName: "Legacy.Maliev.OrderService.Data",
            SourceCommit: "7a191cba37aefdf63cd46835c49db1c017a4a0de",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715053157_InitialOrderPostgresCompatibility",
                // Immutable EF migration identifier.
                "20260721030103_FixTimestampColumnType",
                // Immutable EF migration identifier.
                "20260829182133_AddDurableOrderOperationKey",
                // Immutable EF migration identifier.
                "20261001051000_AddOrderDeletionIntent",
            ]),
        ["ORDER_STATUS"] = new(
            ContextFullName: "Legacy.Maliev.OrderService.Data.OrderStatusDbContext",
            AssemblyName: "Legacy.Maliev.OrderService.Data",
            SourceCommit: "7a191cba37aefdf63cd46835c49db1c017a4a0de",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715053202_InitialOrderStatusPostgresCompatibility",
                // Immutable EF migration identifier.
                "20260721030107_FixTimestampColumnType",
                // Immutable EF migration identifier.
                "20261007120000_RequireOrderStatusSourceStrings",
            ]),
        ["IAM"] = new(
            ContextFullName: "Maliev.IAMService.Infrastructure.Persistence.IAMDbContext",
            AssemblyName: "Maliev.IAMService.Infrastructure",
            SourceCommit: "4fe6642e5674013de9a3672505aec898fdcae0ed",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260304050510_InitialCreate",
                // Immutable EF migration identifier.
                "20260715224757_AddWorkloadPrincipalProvisioning",
                // Immutable EF migration identifier.
                "20260715231400_HardenWorkloadPrincipalProvisioning",
            ]),
        ["FILE_DATABASE"] = new(
            ContextFullName: "Legacy.Maliev.FileService.Data.FileDbContext",
            AssemblyName: "Legacy.Maliev.FileService.Data",
            SourceCommit: "07f0b5b721e3df758a9ec742c6f167491f899e44",
            Migrations:
            [
                // Immutable EF migration identifier.
                "20260715033302_InitialPostgresCompatibility",
                // Immutable EF migration identifier.
                "20260719033405_AddInstantQuoteUploadWorkflow",
                // Immutable EF migration identifier.
                "20260929015203_AddStorageMoveJournal",
                // Immutable EF migration identifier.
                "20261001023704_AddQuarantineUploadIntent",
                // Immutable EF migration identifier.
                "20261006070500_RestoreLegacyUploadSizeNullability",
                // Immutable EF migration identifier.
                "20261006073000_RestoreLegacyUploadReferenceNullability",
            ]),
    };
}
