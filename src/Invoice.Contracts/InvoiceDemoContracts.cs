namespace D1.Invoice.Contracts;

public sealed record InvoiceAgentExecution(
    string AgentName,
    string AgentClientId,
    string Operation,
    string Outcome,
    SafeIdentitySummary Claims,
    string? Detail);

public sealed record InvoiceDemoRunResult(
    Guid Id,
    string CorrelationId,
    string Stage,
    string Mode,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<InvoiceResponse> Invoices,
    IReadOnlyList<ApprovalDecisionResponse> Decisions,
    IReadOnlyList<InvoiceAgentExecution> Executions,
    string? Error);

public sealed record ApprovalDecisionResponse(
    Guid Id,
    Guid InvoiceId,
    string ActorIdentityId,
    string ActorIdentityName,
    string Disposition,
    string Reason,
    DateTimeOffset DecidedAtUtc,
    string CorrelationId);

public sealed record DemoRunSnapshot(
    Guid Id,
    string CorrelationId,
    string Mode,
    string Status,
    string SafeSummaryJson,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc);

public sealed record InteractiveInvoiceResult(
    InvoiceResponse Invoice,
    string CorrelationId,
    SafeIdentitySummary Claims,
    string Operation,
    string Result);
