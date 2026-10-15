using D1.Invoice.Contracts;
using D1.Invoice.Domain;
using InvoiceAggregate = D1.Invoice.Domain.Invoice;

namespace D1.Invoice.Api;

public static class InvoiceApiMapping
{
    public static InvoiceDraftData ToDraft(CreateInvoiceRequest request)
    {
        if (!FictionalSupplierCatalog.TryGet(request.SupplierId, out var supplier))
            throw new ArgumentException("Supplier is not in the fictional catalog.");
        var lines = request.Lines.Select(line => InvoiceLine.Create(line.Description, line.Quantity, line.UnitPrice, line.TaxRate)).ToArray();
        return new InvoiceDraftData(
            Guid.NewGuid(), request.InvoiceNumber, request.PurchaseOrderNumber, supplier,
            request.InvoiceDate, request.DueDate, request.Currency, request.Description,
            request.CostCenter, lines);
    }

    public static InvoiceResponse ToResponse(InvoiceAggregate invoice)
        => new(invoice.Id, invoice.InvoiceNumber, invoice.PurchaseOrderNumber, invoice.SupplierName,
            invoice.TotalAmount, invoice.Currency, invoice.Status.ToString(), invoice.Disposition.ToString(),
            invoice.ManualReviewReason, invoice.CorrelationId);
}
