using System.Globalization;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Tests.Data;

public sealed class QualificationPrecisionContractTests
{
    [Fact]
    public void QualificationDates_RoundTripSevenFractionalDigits()
    {
        var options = new DbContextOptionsBuilder<QuotationRequestDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=design-time;Username=design-time")
            .Options;
        using var context = new QuotationRequestDbContext(options);
        DateTime timestamp = DateTime.ParseExact(
            "2026-09-24T08:44:50.1234567", "yyyy-MM-dd'T'HH:mm:ss.fffffff",
            CultureInfo.InvariantCulture, DateTimeStyles.None);

        var request = context.Model.FindEntityType(typeof(QuotationRequest))!;
        var requestDate = request.FindProperty(nameof(QuotationRequest.QualificationStateChangedUtc))!;
        Assert.Equal("text", requestDate.GetColumnType());
        var requestConverter = requestDate.GetValueConverter()!;
        Assert.Equal("2026-09-24T08:44:50.1234567", requestConverter.ConvertToProvider(timestamp));
        Assert.Equal(timestamp, requestConverter.ConvertFromProvider("2026-09-24T08:44:50.1234567"));

        var audit = context.Model.FindEntityType(typeof(RequestQualificationAudit))!;
        var auditDate = audit.FindProperty(nameof(RequestQualificationAudit.ChangedUtc))!;
        Assert.Equal("text", auditDate.GetColumnType());
        var auditConverter = auditDate.GetValueConverter()!;
        Assert.Equal("2026-09-24T08:44:50.1234567", auditConverter.ConvertToProvider(timestamp));
        Assert.Equal(timestamp, auditConverter.ConvertFromProvider("2026-09-24T08:44:50.1234567"));
    }
}
