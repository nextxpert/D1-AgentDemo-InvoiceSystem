using D1.Invoice.Domain;
using InvoiceAggregate = D1.Invoice.Domain.Invoice;

namespace D1.Invoice.Application;

public interface IInvoiceRepository
{
    Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken);
    Task<bool> PurchaseOrderNumberExistsAsync(string purchaseOrderNumber, CancellationToken cancellationToken);
    Task AddAsync(InvoiceAggregate invoice, CancellationToken cancellationToken);
    Task<InvoiceAggregate?> GetByIdAsync(Guid invoiceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InvoiceAggregate>> ListPendingApprovalAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<InvoiceAggregate>> ListAsync(CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IActorContext
{
    ActorContext Current { get; }
}

public interface IInvoiceAuditRepository
{
    Task AddAuditAsync(InvoiceAuditEvent auditEvent, CancellationToken cancellationToken);
    Task AddDecisionAsync(ApprovalDecision decision, CancellationToken cancellationToken);
    Task<IReadOnlyList<InvoiceAuditEvent>> FindAuditAsync(Guid? invoiceId, string? correlationId, CancellationToken cancellationToken);
}

public interface IDemoRunRepository
{
    Task AddAsync(D1.Invoice.Contracts.DemoRunSnapshot snapshot, CancellationToken cancellationToken);
    Task<IReadOnlyList<D1.Invoice.Contracts.DemoRunSnapshot>> ListAsync(CancellationToken cancellationToken);
    Task<D1.Invoice.Contracts.DemoRunSnapshot?> FindAsync(string correlationId, CancellationToken cancellationToken);
}

public interface IInvoiceCreationService
{
    Task<InvoiceAggregate> PreviewAsync(InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken);
    Task<InvoiceAggregate> CreateDraftAsync(InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken);
    Task<InvoiceAggregate> CreateAndSubmitAsync(InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken);
}

public sealed class InvoiceCreationService(
    IInvoiceRepository repository,
    IActorContext actorContext,
    TimeProvider timeProvider,
    decimal maximumInvoiceAmount) : IInvoiceCreationService
{
    private static readonly HashSet<string> SupportedCurrencies = new(StringComparer.OrdinalIgnoreCase) { "EUR", "USD", "GBP" };

    public async Task<InvoiceAggregate> PreviewAsync(InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken)
    {
        return await BuildAsync(draft, correlationId, cancellationToken);
    }

    public async Task<InvoiceAggregate> CreateDraftAsync(InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await BuildAsync(draft, correlationId, cancellationToken);
        await repository.AddAsync(invoice, cancellationToken);
        return invoice;
    }

    public async Task<InvoiceAggregate> CreateAndSubmitAsync(InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await CreateDraftAsync(draft, correlationId, cancellationToken);
        invoice.Submit(timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);
        return invoice;
    }

    private async Task<InvoiceAggregate> BuildAsync(InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var actor = actorContext.Current ?? throw new InvalidOperationException("Actor context is unavailable.");
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("Correlation ID is required.", nameof(correlationId));
        if (!SupportedCurrencies.Contains(draft.Currency)) throw new ArgumentException("Currency must be EUR, USD, or GBP.", nameof(draft.Currency));
        if (draft.DueDate <= draft.InvoiceDate) throw new ArgumentException("Due date must be after invoice date.", nameof(draft.DueDate));
        if (!FictionalSupplierCatalog.TryGet(draft.Supplier.Id, out var supplier)) throw new ArgumentException("Unknown fictional supplier.", nameof(draft));
        if (await repository.InvoiceNumberExistsAsync(draft.InvoiceNumber, cancellationToken)) throw new InvalidOperationException("Invoice number is duplicated.");
        if (await repository.PurchaseOrderNumberExistsAsync(draft.PurchaseOrderNumber, cancellationToken)) throw new InvalidOperationException("Purchase-order number is duplicated.");

        var calculatedLines = draft.Lines.Select(line => InvoiceLine.Create(
            line.Description, line.Quantity, line.UnitPrice, line.TaxRate, line.Id)).ToArray();
        if (calculatedLines.Sum(line => line.LineTotal) > maximumInvoiceAmount)
            throw new InvalidOperationException("Invoice exceeds the configured demo amount limit.");

        return InvoiceAggregate.CreateDraft(
            draft.Id, draft.InvoiceNumber, draft.PurchaseOrderNumber, supplier, draft.InvoiceDate, draft.DueDate,
            draft.Currency, draft.Description, draft.CostCenter, calculatedLines, actor, correlationId, timeProvider.GetUtcNow());
    }
}
