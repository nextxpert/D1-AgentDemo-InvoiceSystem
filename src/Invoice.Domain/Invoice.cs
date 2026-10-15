namespace D1.Invoice.Domain;

public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice() { }

    public Guid Id { get; private set; }
    public string InvoiceNumber { get; private set; } = string.Empty;
    public string PurchaseOrderNumber { get; private set; } = string.Empty;
    public string SupplierId { get; private set; } = string.Empty;
    public string SupplierName { get; private set; } = string.Empty;
    public string SupplierCountry { get; private set; } = string.Empty;
    public DateOnly InvoiceDate { get; private set; }
    public DateOnly DueDate { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public decimal Subtotal { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string CostCenter { get; private set; } = string.Empty;
    public IReadOnlyCollection<InvoiceLine> Lines => _lines.AsReadOnly();
    public InvoiceStatus Status { get; private set; }
    public DecisionDisposition Disposition { get; private set; }
    public string? ManualReviewReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedByIdentityId { get; private set; } = string.Empty;
    public string CreatedByIdentityName { get; private set; } = string.Empty;
    public ActorType CreatedByIdentityType { get; private set; }
    public string? CreatedOnBehalfOfUserId { get; private set; }
    public string? CreatedOnBehalfOfUserName { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? ApprovedAtUtc { get; private set; }
    public string? ApprovedByIdentityId { get; private set; }
    public string? ApprovedByIdentityName { get; private set; }
    public DateTimeOffset? RejectedAtUtc { get; private set; }
    public string? RejectedByIdentityId { get; private set; }
    public string? RejectionReason { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public int Version { get; private set; }

    public static Invoice CreateDraft(
        Guid id,
        string invoiceNumber,
        string purchaseOrderNumber,
        FictionalSupplier supplier,
        DateOnly invoiceDate,
        DateOnly dueDate,
        string currency,
        string description,
        string costCenter,
        IEnumerable<InvoiceLine> lines,
        ActorContext actor,
        string correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(lines);
        if (id == Guid.Empty) throw new ArgumentException("Invoice ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(invoiceNumber)) throw new ArgumentException("Invoice number is required.", nameof(invoiceNumber));
        if (string.IsNullOrWhiteSpace(purchaseOrderNumber)) throw new ArgumentException("Purchase-order number is required.", nameof(purchaseOrderNumber));
        if (dueDate <= invoiceDate) throw new ArgumentException("Due date must be after invoice date.", nameof(dueDate));
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Description is required.", nameof(description));
        if (!supplier.CostCenters.Contains(costCenter, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Cost center is not valid for the supplier.", nameof(costCenter));

        var materializedLines = lines.ToArray();
        if (materializedLines.Length is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(lines), "Invoices require one to five lines.");
        var subtotal = materializedLines.Sum(x => x.LineSubtotal);
        var tax = materializedLines.Sum(x => x.LineTax);
        var total = materializedLines.Sum(x => x.LineTotal);
        if (total <= 0 || subtotal < 0 || tax < 0) throw new ArgumentException("Invoice totals must be positive and internally consistent.", nameof(lines));

        var invoice = new Invoice
        {
            Id = id,
            InvoiceNumber = invoiceNumber.Trim().ToUpperInvariant(),
            PurchaseOrderNumber = purchaseOrderNumber.Trim().ToUpperInvariant(),
            SupplierId = supplier.Id,
            SupplierName = supplier.Name,
            SupplierCountry = supplier.Country,
            InvoiceDate = invoiceDate,
            DueDate = dueDate,
            Currency = currency.Trim().ToUpperInvariant(),
            Subtotal = subtotal,
            TaxAmount = tax,
            TotalAmount = total,
            Description = description.Trim(),
            CostCenter = costCenter.Trim().ToUpperInvariant(),
            Status = InvoiceStatus.Draft,
            Disposition = DecisionDisposition.Pending,
            CreatedAtUtc = now,
            CreatedByIdentityId = actor.IdentityId,
            CreatedByIdentityName = actor.IdentityName,
            CreatedByIdentityType = actor.IdentityType,
            CreatedOnBehalfOfUserId = actor.UserId,
            CreatedOnBehalfOfUserName = actor.UserName,
            CorrelationId = correlationId
        };
        invoice._lines.AddRange(materializedLines);
        return invoice;
    }

    public void Submit(DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Draft) throw new InvalidOperationException("Only a draft invoice can be submitted.");
        Status = InvoiceStatus.PendingApproval;
        SubmittedAtUtc = now;
        Version++;
    }

    public void RequireManualReview(string reason)
    {
        EnsurePendingApproval();
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Manual review reason is required.", nameof(reason));
        Disposition = DecisionDisposition.ManualReviewRequired;
        ManualReviewReason = reason.Trim();
        Version++;
    }

    public void Approve(ActorContext actor, DateTimeOffset now)
    {
        EnsurePendingApproval();
        EnsureNotManualReview();
        EnsureDifferentAgent(actor);
        Status = InvoiceStatus.ApprovedForPayment;
        Disposition = DecisionDisposition.Approved;
        ApprovedAtUtc = now;
        ApprovedByIdentityId = actor.IdentityId;
        ApprovedByIdentityName = actor.IdentityName;
        ManualReviewReason = null;
        Version++;
    }

    public void Reject(ActorContext actor, string reason, DateTimeOffset now)
    {
        EnsurePendingApproval();
        EnsureNotManualReview();
        EnsureDifferentAgent(actor);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Rejection reason is required.", nameof(reason));
        Status = InvoiceStatus.Rejected;
        Disposition = DecisionDisposition.Rejected;
        RejectedAtUtc = now;
        RejectedByIdentityId = actor.IdentityId;
        RejectionReason = reason.Trim();
        Version++;
    }

    private void EnsurePendingApproval()
    {
        if (Status != InvoiceStatus.PendingApproval) throw new InvalidOperationException("Only PendingApproval invoices can be decided.");
    }

    private void EnsureNotManualReview()
    {
        if (Disposition == DecisionDisposition.ManualReviewRequired)
            throw new InvalidOperationException("Manual-review invoices cannot be decided by the autonomous agent.");
    }

    private void EnsureDifferentAgent(ActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (CreatedByIdentityType == ActorType.AutonomousAgent
            && actor.IdentityType == ActorType.AutonomousAgent
            && string.Equals(CreatedByIdentityId, actor.IdentityId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("An autonomous agent cannot approve or reject an invoice it created.");
        }
    }
}
