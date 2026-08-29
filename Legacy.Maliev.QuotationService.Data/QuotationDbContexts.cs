using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Legacy.Maliev.QuotationService.Data;

public sealed class QuotationDbContext(DbContextOptions<QuotationDbContext> options) : DbContext(options)
{
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<QuotationOrderItem> OrderItems => Set<QuotationOrderItem>();
    public DbSet<QuotationFile> Files => Set<QuotationFile>();
    public DbSet<QuotationOrderLink> OrderLinks => Set<QuotationOrderLink>();
    public DbSet<QuotationAcceptedOutcome> AcceptedOutcomes => Set<QuotationAcceptedOutcome>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var quotation = modelBuilder.Entity<Quotation>();
        quotation.ToTable("Quotation"); quotation.HasKey(x => x.Id); quotation.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        quotation.Property(x => x.CustomerId).HasColumnName("CustomerID"); quotation.Property(x => x.EmployeeId).HasColumnName("EmployeeID"); quotation.Property(x => x.InvoiceId).HasColumnName("InvoiceID"); quotation.Property(x => x.CurrencyId).HasColumnName("CurrencyID");
        quotation.Property(x => x.Fob).HasColumnName("FOB").HasMaxLength(256); quotation.Property(x => x.ShippedVia).HasMaxLength(256); quotation.Property(x => x.Terms).HasMaxLength(256);
        quotation.Property(x => x.ExpirationDate).HasColumnType("timestamp without time zone"); quotation.Property(x => x.Subtotal).HasPrecision(18, 2); quotation.Property(x => x.Vat).HasPrecision(18, 2); quotation.Property(x => x.Total).HasPrecision(18, 2); quotation.Property(x => x.WithholdingTax).HasPrecision(18, 2);
        quotation.Property(x => x.QuotedAmount).HasPrecision(18, 2).HasComputedColumnSql("(\"Total\" - \"WithholdingTax\")::numeric(18,2)", stored: true);
        quotation.Property(x => x.SourceRequestId).HasColumnName("SourceRequestID"); quotation.Property(x => x.SourceJourneyId).HasColumnName("SourceJourneyID"); quotation.Property(x => x.AcceptedUtc).HasColumnType("timestamp without time zone"); quotation.Property(x => x.AcceptanceOrigin).HasMaxLength(16);
        quotation.HasIndex(x => x.SourceRequestId); quotation.HasIndex(x => x.SourceJourneyId);
        Dates(quotation); quotation.Property(x => x.ModifiedDate).IsConcurrencyToken();

        var acceptedOutcome = modelBuilder.Entity<QuotationAcceptedOutcome>();
        acceptedOutcome.ToTable("QuotationAcceptedOutcome"); acceptedOutcome.HasKey(x => x.Id); acceptedOutcome.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); acceptedOutcome.Property(x => x.QuotationId).HasColumnName("QuotationID"); acceptedOutcome.Property(x => x.SourceRequestId).HasColumnName("SourceRequestID"); acceptedOutcome.Property(x => x.SourceJourneyId).HasColumnName("SourceJourneyID"); acceptedOutcome.Property(x => x.EventKey).HasMaxLength(128).IsRequired(); acceptedOutcome.Property(x => x.AcceptedUtc).HasColumnType("timestamp without time zone"); acceptedOutcome.Property(x => x.AcceptanceOrigin).HasMaxLength(16).IsRequired();
        acceptedOutcome.HasIndex(x => x.EventKey).IsUnique(); acceptedOutcome.HasIndex(x => x.QuotationId); acceptedOutcome.HasIndex(x => x.AcceptedUtc); acceptedOutcome.HasIndex(x => x.SourceRequestId); acceptedOutcome.HasIndex(x => x.SourceJourneyId);
        acceptedOutcome.HasOne(x => x.Quotation).WithMany(x => x.AcceptedOutcomes).HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_QuotationAcceptedOutcome_Quotation");

        var item = modelBuilder.Entity<QuotationOrderItem>();
        item.ToTable("OrderItem"); item.HasKey(x => x.Id); item.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); item.Property(x => x.QuotationId).HasColumnName("QuotationID"); item.Property(x => x.OrderId).HasColumnName("OrderID"); item.Property(x => x.UnitPrice).HasPrecision(18, 2); item.Property(x => x.Subtotal).HasPrecision(18, 2).HasComputedColumnSql("(\"UnitPrice\" * \"Quantity\")::numeric(18,2)", stored: true); Dates(item); item.Property(x => x.ModifiedDate).IsConcurrencyToken();
        item.HasOne(x => x.Quotation).WithMany(x => x.OrderItems).HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_OrderItem_Quotation");

        var file = modelBuilder.Entity<QuotationFile>();
        file.ToTable("QuotationFile"); file.HasKey(x => x.Id); file.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); file.Property(x => x.QuotationId).HasColumnName("QuotationID"); file.Property(x => x.Bucket).HasMaxLength(50).IsRequired(); file.Property(x => x.ObjectName).IsRequired(); Dates(file);
        file.HasOne(x => x.Quotation).WithMany(x => x.Files).HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_QuotationFile_Quotation");

        var link = modelBuilder.Entity<QuotationOrderLink>();
        link.ToTable("QuotationHasOrder"); link.HasKey(x => x.Id); link.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); link.Property(x => x.QuotationId).HasColumnName("QuotationID"); link.Property(x => x.OrderId).HasColumnName("OrderID"); Dates(link);
        link.HasOne(x => x.Quotation).WithMany(x => x.Orders).HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_QuotationHasOrder_Quotation");
    }

    private static void Dates<TEntity>(EntityTypeBuilder<TEntity> entity) where TEntity : class
    {
        entity.Property<DateTime?>("CreatedDate").HasColumnType("timestamp without time zone").HasDefaultValueSql("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'");
        entity.Property<DateTime?>("ModifiedDate").HasColumnType("timestamp without time zone").HasDefaultValueSql("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'");
    }
}

public sealed class QuotationRequestDbContext(DbContextOptions<QuotationRequestDbContext> options) : DbContext(options)
{
    public DbSet<QuotationRequest> Requests => Set<QuotationRequest>();
    public DbSet<QuotationRequestFile> Files => Set<QuotationRequestFile>();
    public DbSet<RequestCreateIdempotency> RequestCreateIdempotency => Set<RequestCreateIdempotency>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var request = modelBuilder.Entity<QuotationRequest>();
        request.ToTable("Request"); request.HasKey(x => x.Id); request.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); request.Property(x => x.Country).HasMaxLength(256); request.Property(x => x.TaxIdentification).HasMaxLength(256); request.Property(x => x.TelephoneNumber).HasMaxLength(256); Dates(request); request.Property(x => x.ModifiedDate).IsConcurrencyToken();
        var file = modelBuilder.Entity<QuotationRequestFile>();
        file.ToTable("RequestFile"); file.HasKey(x => x.Id); file.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd(); file.Property(x => x.RequestId).HasColumnName("RequestID"); file.Property(x => x.Bucket).HasMaxLength(50); Dates(file);
        var idempotency = modelBuilder.Entity<RequestCreateIdempotency>();
        idempotency.ToTable("RequestCreateIdempotency");
        idempotency.HasKey(x => x.KeyHash);
        idempotency.Property(x => x.KeyHash).HasMaxLength(64).IsFixedLength();
        idempotency.Property(x => x.Fingerprint).HasMaxLength(64).IsFixedLength();
        idempotency.Property(x => x.RequestId).HasColumnName("RequestID");
    }

    private static void Dates<TEntity>(EntityTypeBuilder<TEntity> entity) where TEntity : class
    {
        entity.Property<DateTime?>("CreatedDate").HasColumnType("timestamp without time zone").HasDefaultValueSql("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'");
        entity.Property<DateTime?>("ModifiedDate").HasColumnType("timestamp without time zone").HasDefaultValueSql("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'");
    }
}
