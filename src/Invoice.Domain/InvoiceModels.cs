namespace D1.Invoice.Domain;

public enum InvoiceStatus
{
    Draft,
    PendingApproval,
    ApprovedForPayment,
    Rejected
}

public enum ActorType
{
    AutonomousAgent,
    InteractiveAgent,
    User
}

public enum DecisionDisposition
{
    Pending,
    Approved,
    Rejected,
    ManualReviewRequired
}

public sealed record InvoiceLine(
    Guid Id,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal LineSubtotal,
    decimal LineTax,
    decimal LineTotal)
{
    public static InvoiceLine Create(string description, decimal quantity, decimal unitPrice, decimal taxRate, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Line description is required.", nameof(description));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        if (unitPrice <= 0) throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price must be positive.");
        if (taxRate is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(taxRate), "Tax rate must be between 0 and 1.");

        var subtotal = decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
        var tax = decimal.Round(subtotal * taxRate, 2, MidpointRounding.AwayFromZero);
        return new InvoiceLine(id ?? Guid.NewGuid(), description.Trim(), quantity, unitPrice, taxRate, subtotal, tax, subtotal + tax);
    }
}

public sealed record ActorContext(
    string IdentityId,
    string IdentityName,
    ActorType IdentityType,
    string? UserId = null,
    string? UserName = null);

public sealed record ApprovalDecision(
    Guid Id,
    Guid InvoiceId,
    string ActorIdentityId,
    string ActorIdentityName,
    DecisionDisposition Disposition,
    string Reason,
    DateTimeOffset DecidedAtUtc,
    string CorrelationId);

public sealed record InvoiceAuditEvent(
    Guid Id,
    Guid InvoiceId,
    string ActorIdentityId,
    string ActorIdentityName,
    ActorType ActorType,
    string? OnBehalfOfUserId,
    string? OnBehalfOfUserName,
    string Operation,
    string RequiredPermission,
    string Result,
    string? Reason,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);
