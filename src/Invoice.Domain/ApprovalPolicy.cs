namespace D1.Invoice.Domain;

public sealed record ApprovalPolicyOptions(decimal AutonomousApprovalLimit = 10000m);

public sealed record ApprovalEvaluation(DecisionDisposition Disposition, string Reason);

public sealed class ApprovalPolicy(ApprovalPolicyOptions options)
{
    public ApprovalEvaluation Evaluate(Invoice invoice, bool invoiceNumberIsUnique, bool purchaseOrderIsUnique)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (invoice.Status != InvoiceStatus.PendingApproval)
            return new(DecisionDisposition.Rejected, "Invoice is not in PendingApproval state.");
        if (invoice.TotalAmount <= 0)
            return new(DecisionDisposition.Rejected, "Invoice total must be positive.");
        if (!invoiceNumberIsUnique)
            return new(DecisionDisposition.Rejected, "Invoice number is duplicated.");
        if (!purchaseOrderIsUnique)
            return new(DecisionDisposition.Rejected, "Purchase-order number is duplicated.");
        if (!FictionalSupplierCatalog.TryGet(invoice.SupplierId, out _))
            return new(DecisionDisposition.Rejected, "Supplier is not in the fictional supplier catalog.");
        if (invoice.DueDate <= invoice.InvoiceDate)
            return new(DecisionDisposition.Rejected, "Due date must be after invoice date.");
        if (invoice.Lines.Sum(x => x.LineTotal) != invoice.TotalAmount)
            return new(DecisionDisposition.Rejected, "Line calculations do not equal invoice total.");
        if (invoice.Currency is not ("EUR" or "USD" or "GBP"))
            return new(DecisionDisposition.Rejected, "Invoice currency is unsupported.");
        if (invoice.Lines.Any(x => x.Quantity <= 0 || x.UnitPrice <= 0 || x.TaxRate is < 0 or > 1))
            return new(DecisionDisposition.Rejected, "An invoice line is invalid.");
        if (invoice.ManualReviewReason is not null)
            return new(DecisionDisposition.ManualReviewRequired, invoice.ManualReviewReason);
        if (invoice.TotalAmount > options.AutonomousApprovalLimit)
            return new(DecisionDisposition.ManualReviewRequired, $"Total exceeds autonomous approval limit ({options.AutonomousApprovalLimit:0.00}).");

        return new(DecisionDisposition.Approved, "Invoice passed deterministic approval rules.");
    }
}
