namespace D1.Invoice.Contracts;

public sealed record InvoiceLineRequest(
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate);

public sealed record CreateInvoiceRequest(
    string InvoiceNumber,
    string PurchaseOrderNumber,
    string SupplierId,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string Currency,
    string Description,
    string CostCenter,
    IReadOnlyList<InvoiceLineRequest> Lines);

public sealed record InvoiceResponse(
    Guid Id,
    string InvoiceNumber,
    string PurchaseOrderNumber,
    string SupplierName,
    decimal TotalAmount,
    string Currency,
    string Status,
    string Disposition,
    string? ManualReviewReason,
    string CorrelationId);

public sealed record SafeIdentitySummary(
    string Subject,
    string? Actor,
    string Audience,
    string TenantId,
    string ClientId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    string? ParentBlueprintClientId,
    DateTimeOffset? IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc);
