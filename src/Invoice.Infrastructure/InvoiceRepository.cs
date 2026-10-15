using D1.Invoice.Application;
using D1.Invoice.Domain;
using Microsoft.EntityFrameworkCore;
using InvoiceAggregate = D1.Invoice.Domain.Invoice;

namespace D1.Invoice.Infrastructure;

public sealed class InvoiceRepository(InvoiceDbContext dbContext) : IInvoiceRepository
{
    public Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken)
        => dbContext.Invoices.AnyAsync(x => x.InvoiceNumber == invoiceNumber, cancellationToken);

    public Task<bool> PurchaseOrderNumberExistsAsync(string purchaseOrderNumber, CancellationToken cancellationToken)
        => dbContext.Invoices.AnyAsync(x => x.PurchaseOrderNumber == purchaseOrderNumber, cancellationToken);

    public async Task AddAsync(InvoiceAggregate invoice, CancellationToken cancellationToken)
    {
        await dbContext.Invoices.AddAsync(invoice, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<InvoiceAggregate?> GetByIdAsync(Guid invoiceId, CancellationToken cancellationToken)
        => dbContext.Invoices.SingleOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);

    public async Task<IReadOnlyList<InvoiceAggregate>> ListPendingApprovalAsync(CancellationToken cancellationToken)
        => (await dbContext.Invoices
                .Where(x => x.Status == InvoiceStatus.PendingApproval)
                .ToListAsync(cancellationToken))
            .OrderBy(x => x.SubmittedAtUtc)
            .ToArray();

    public async Task<IReadOnlyList<InvoiceAggregate>> ListAsync(CancellationToken cancellationToken)
        => (await dbContext.Invoices.ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToArray();

    public Task SaveChangesAsync(CancellationToken cancellationToken)
        => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class InvoiceAuditRepository(InvoiceDbContext dbContext) : IInvoiceAuditRepository
{
    public async Task AddAuditAsync(InvoiceAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await dbContext.AuditEvents.AddAsync(auditEvent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddDecisionAsync(ApprovalDecision decision, CancellationToken cancellationToken)
    {
        await dbContext.ApprovalDecisions.AddAsync(decision, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceAuditEvent>> FindAuditAsync(Guid? invoiceId, string? correlationId, CancellationToken cancellationToken)
    {
        var query = dbContext.AuditEvents.AsQueryable();
        if (invoiceId.HasValue) query = query.Where(x => x.InvoiceId == invoiceId.Value);
        if (!string.IsNullOrWhiteSpace(correlationId)) query = query.Where(x => x.CorrelationId == correlationId);
        return await query.OrderBy(x => x.OccurredAtUtc).ToListAsync(cancellationToken);
    }
}
