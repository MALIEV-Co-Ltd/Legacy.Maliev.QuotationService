namespace Legacy.Maliev.QuotationService.Application.Services;

/// <summary>Deterministic legacy financial behavior isolated for golden tests.</summary>
public static class QuotationCalculations
{
    public static decimal WithholdingRate(DateTime utcNow) =>
        utcNow >= new DateTime(2020, 4, 1, 0, 0, 0, DateTimeKind.Utc)
        && utcNow < new DateTime(2020, 9, 30, 0, 0, 0, DateTimeKind.Utc)
            ? 0.015m
            : 0.03m;

    public static decimal WithholdingAmount(decimal subtotal, DateTime utcNow) => subtotal * WithholdingRate(utcNow);
}
