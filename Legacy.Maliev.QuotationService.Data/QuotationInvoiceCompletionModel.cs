using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Data;

internal static class QuotationInvoiceCompletionModel
{
    internal static void Configure(ModelBuilder builder)
    {
        var receipt = builder.Entity<QuotationInvoiceCompletionOperation>();
        receipt.ToTable("QuotationInvoiceCompletionOperation");
        receipt.HasKey(x => x.OperationId);
        receipt.Property(x => x.OperationId).ValueGeneratedNever();
        receipt.Property(x => x.AuthorityJson).HasColumnType("text").IsRequired();
        receipt.Property(x => x.OrderIdsJson).HasColumnType("text").IsRequired();
        receipt.Property(x => x.CompletedOrderIdsJson).HasColumnType("text").IsRequired();
        receipt.Property(x => x.State).HasMaxLength(32).IsRequired();
        receipt.Property(x => x.DecisionOrderVersion).HasConversion(ExactDateTime2Text.Converter).HasColumnType("text");
        receipt.Property(x => x.ModifiedDate).HasConversion(ExactDateTime2Text.Converter).HasColumnType("text");
        receipt.Property(x => x.ClaimUntil).HasColumnType("timestamp without time zone");
        receipt.HasIndex(x => x.QuotationId).IsUnique();
    }
}
