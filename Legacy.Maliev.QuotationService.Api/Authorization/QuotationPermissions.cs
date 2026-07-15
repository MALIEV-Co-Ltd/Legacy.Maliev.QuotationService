namespace Legacy.Maliev.QuotationService.Api.Authorization;

public static class QuotationPermissions
{
    public const string QuotationsRead = "legacy.quotations.read";
    public const string QuotationsCreate = "legacy.quotations.create";
    public const string QuotationsUpdate = "legacy.quotations.update";
    public const string QuotationsDelete = "legacy.quotations.delete";
    public const string LinesRead = "legacy.quotation-lines.read";
    public const string LinesWrite = "legacy.quotation-lines.write";
    public const string LinesDelete = "legacy.quotation-lines.delete";
    public const string OrdersRead = "legacy.quotation-orders.read";
    public const string OrdersWrite = "legacy.quotation-orders.write";
    public const string OrdersDelete = "legacy.quotation-orders.delete";
    public const string FilesRead = "legacy.quotation-files.read";
    public const string FilesWrite = "legacy.quotation-files.write";
    public const string FilesDelete = "legacy.quotation-files.delete";
    public const string RequestsRead = "legacy.quotation-requests.read";
    public const string RequestsCreate = "legacy.quotation-requests.create";
    public const string RequestsUpdate = "legacy.quotation-requests.update";
    public const string RequestsDelete = "legacy.quotation-requests.delete";
}
