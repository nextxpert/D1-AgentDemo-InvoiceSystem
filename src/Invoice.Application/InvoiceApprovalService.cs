using D1.Invoice.Domain;
using InvoiceAggregate = D1.Invoice.Domain.Invoice;

namespace D1.Invoice.Application;

public interface IInvoiceApprovalService
{
    Task<ApprovalDecision> EvaluateAsync(Guid invoiceId, string correlationId, CancellationToken cancellationToken);
}

public sealed class InvoiceApprovalService(
    IInvoiceRepository invoices,
    IInvoiceAuditRepository audit,
    IActorContext actorContext,
    ApprovalPolicy policy,
    TimeProvider timeProvider) : IInvoiceApprovalService
{
    public async Task<ApprovalDecision> EvaluateAsync(Guid invoiceId, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetByIdAsync(invoiceId, cancellationToken)
            ?? throw new KeyNotFoundException("Invoice was not found.");
        var actor = actorContext.Current;
        if (actor.IdentityType != ActorType.AutonomousAgent)
            throw new InvalidOperationException("Only the autonomous Approval Agent may evaluate invoices.");

        var now = timeProvider.GetUtcNow();
        var evaluation = policy.Evaluate(invoice, invoiceNumberIsUnique: true, purchaseOrderIsUnique: true);
        switch (evaluation.Disposition)
        {
            case DecisionDisposition.Approved:
                invoice.Approve(actor, now);
                break;
            case DecisionDisposition.Rejected:
                invoice.Reject(actor, evaluation.Reason, now);
                break;
            case DecisionDisposition.ManualReviewRequired:
                invoice.RequireManualReview(evaluation.Reason);
                break;
            default:
                throw new InvalidOperationException("Approval policy returned an unsupported disposition.");
        }

        var decision = new ApprovalDecision(
            Guid.NewGuid(), invoice.Id, actor.IdentityId, actor.IdentityName,
            evaluation.Disposition, evaluation.Reason, now, correlationId);
        await invoices.SaveChangesAsync(cancellationToken);
        await audit.AddDecisionAsync(decision, cancellationToken);
        await audit.AddAuditAsync(new InvoiceAuditEvent(
            Guid.NewGuid(), invoice.Id, actor.IdentityId, actor.IdentityName, actor.IdentityType,
            actor.UserId, actor.UserName, "invoice.evaluate", "Invoice.Read.All+Invoice.Approve.All",
            evaluation.Disposition.ToString(), evaluation.Reason, correlationId, now), cancellationToken);
        return decision;
    }
}
