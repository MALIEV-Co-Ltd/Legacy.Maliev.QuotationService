using Legacy.Maliev.QuotationService.Application.Services;

namespace Legacy.Maliev.QuotationService.Tests.Application;

public sealed class QuotationCalculationTests
{
    [Theory]
    [InlineData("2020-03-31T23:59:59Z", 0.03)]
    [InlineData("2020-04-01T00:00:00Z", 0.015)]
    [InlineData("2020-09-29T23:59:59Z", 0.015)]
    [InlineData("2020-09-30T00:00:00Z", 0.03)]
    [InlineData("2026-07-15T00:00:00Z", 0.03)]
    public void WithholdingRate_PreservesLegacyDateBoundaries(string timestamp, decimal expected) =>
        Assert.Equal(expected, QuotationCalculations.WithholdingRate(DateTime.Parse(timestamp).ToUniversalTime()));

    [Fact]
    public void WithholdingAmount_PreservesUnroundedLegacyDecimalBehavior() =>
        Assert.Equal(37.0368m, QuotationCalculations.WithholdingAmount(1234.56m, new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)));
}
