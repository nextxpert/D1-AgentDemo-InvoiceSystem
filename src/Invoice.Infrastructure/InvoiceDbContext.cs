using D1.Invoice.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using InvoiceAggregate = D1.Invoice.Domain.Invoice;

namespace D1.Invoice.Infrastructure;

public sealed class InvoiceDbContext(DbContextOptions<InvoiceDbContext> options) : DbContext(options)
{
    public DbSet<InvoiceAggregate> Invoices => Set<InvoiceAggregate>();
    public DbSet<InvoiceAuditEvent> AuditEvents => Set<InvoiceAuditEvent>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<InvoiceSupplierEntity> Suppliers => Set<InvoiceSupplierEntity>();
    public DbSet<DemoRunEntity> DemoRuns => Set<DemoRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureInvoice(modelBuilder.Entity<InvoiceAggregate>());
        ConfigureAudit(modelBuilder.Entity<InvoiceAuditEvent>());
        ConfigureDecision(modelBuilder.Entity<ApprovalDecision>());

        modelBuilder.Entity<InvoiceSupplierEntity>(entity =>
        {
            entity.ToTable("suppliers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(16);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Country).HasMaxLength(2).IsRequired();
            entity.Property(x => x.CostCentersCsv).HasMaxLength(512).IsRequired();
            entity.HasData(FictionalSupplierCatalog.All.Select(supplier => new InvoiceSupplierEntity
            {
                Id = supplier.Id,
                Name = supplier.Name,
                Country = supplier.Country,
                CostCentersCsv = string.Join(',', supplier.CostCenters)
            }));
        });

        modelBuilder.Entity<DemoRunEntity>(entity =>
        {
            entity.ToTable("demo_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.CorrelationId).IsUnique();
            entity.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SafeSummaryJson).IsRequired();
        });
    }

    private static void ConfigureInvoice(EntityTypeBuilder<InvoiceAggregate> entity)
    {
        entity.ToTable("invoices");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.InvoiceNumber).HasMaxLength(32).IsRequired();
        entity.Property(x => x.PurchaseOrderNumber).HasMaxLength(32).IsRequired();
        entity.HasIndex(x => x.InvoiceNumber).IsUnique();
        entity.HasIndex(x => x.PurchaseOrderNumber).IsUnique();
        entity.Property(x => x.SupplierId).HasMaxLength(16).IsRequired();
        entity.Property(x => x.SupplierName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.SupplierCountry).HasMaxLength(2).IsRequired();
        entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        entity.Property(x => x.Subtotal).HasPrecision(18, 2);
        entity.Property(x => x.TaxAmount).HasPrecision(18, 2);
        entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
        entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
        entity.Property(x => x.CostCenter).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.CreatedByIdentityId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.CreatedByIdentityName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.CreatedByIdentityType).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.CreatedOnBehalfOfUserId).HasMaxLength(128);
        entity.Property(x => x.CreatedOnBehalfOfUserName).HasMaxLength(160);
        entity.Property(x => x.ApprovedByIdentityId).HasMaxLength(128);
        entity.Property(x => x.ApprovedByIdentityName).HasMaxLength(160);
        entity.Property(x => x.RejectedByIdentityId).HasMaxLength(128);
        entity.Property(x => x.ManualReviewReason).HasMaxLength(512);
        entity.Property(x => x.RejectionReason).HasMaxLength(512);
        entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.Version).IsConcurrencyToken();
        entity.Ignore(x => x.Lines);

        entity.OwnsMany<InvoiceLine>("_lines", lines =>
        {
            lines.ToTable("invoice_lines");
            lines.WithOwner().HasForeignKey("InvoiceId");
            lines.HasKey(x => x.Id);
            lines.Property(x => x.Description).HasMaxLength(300).IsRequired();
            lines.Property(x => x.Quantity).HasPrecision(18, 3);
            lines.Property(x => x.UnitPrice).HasPrecision(18, 4);
            lines.Property(x => x.TaxRate).HasPrecision(8, 5);
            lines.Property(x => x.LineSubtotal).HasPrecision(18, 2);
            lines.Property(x => x.LineTax).HasPrecision(18, 2);
            lines.Property(x => x.LineTotal).HasPrecision(18, 2);
        });
        entity.Navigation("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureAudit(EntityTypeBuilder<InvoiceAuditEvent> entity)
    {
        entity.ToTable("audit_events");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ActorIdentityId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ActorIdentityName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.OnBehalfOfUserId).HasMaxLength(128);
        entity.Property(x => x.OnBehalfOfUserName).HasMaxLength(160);
        entity.Property(x => x.Operation).HasMaxLength(64).IsRequired();
        entity.Property(x => x.RequiredPermission).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Result).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Reason).HasMaxLength(512);
        entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        entity.HasIndex(x => x.InvoiceId);
        entity.HasIndex(x => x.CorrelationId);
    }

    private static void ConfigureDecision(EntityTypeBuilder<ApprovalDecision> entity)
    {
        entity.ToTable("approval_decisions");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ActorIdentityId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ActorIdentityName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.Reason).HasMaxLength(512).IsRequired();
        entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        entity.HasIndex(x => x.InvoiceId);
    }
}

public sealed class InvoiceSupplierEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string CostCentersCsv { get; set; } = string.Empty;
}

public sealed class DemoRunEntity
{
    public Guid Id { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SafeSummaryJson { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }
}
