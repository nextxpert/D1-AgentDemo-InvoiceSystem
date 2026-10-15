using D1.Invoice.Domain;
using Xunit;

namespace D1.Invoice.Domain.Tests;

public sealed class DomainTests
{
    private static readonly ActorContext Entry = new("entry-1", "D1-Invoice-Entry-Agent", ActorType.AutonomousAgent);
    private static readonly ActorContext Approval = new("approval-1", "D1-Invoice-Approval-Agent", ActorType.AutonomousAgent);

    [Fact]
    public void GeneratorIsDeterministicForSameSeed()
    {
        var today = new DateOnly(2026, 10, 14);
        var first = new DeterministicInvoiceGenerator(new()).GenerateBatch(today);
        var second = new DeterministicInvoiceGenerator(new()).GenerateBatch(today);

        Assert.Equal(first.Select(x => new { x.Id, x.InvoiceNumber, x.PurchaseOrderNumber, x.Supplier, x.InvoiceDate, x.DueDate, x.Currency, x.CostCenter }),
            second.Select(x => new { x.Id, x.InvoiceNumber, x.PurchaseOrderNumber, x.Supplier, x.InvoiceDate, x.DueDate, x.Currency, x.CostCenter }));
        Assert.Equal(first.SelectMany(x => x.Lines.Select(line => new { line.Id, line.Description, line.Quantity, line.UnitPrice, line.LineTotal })),
            second.SelectMany(x => x.Lines.Select(line => new { line.Id, line.Description, line.Quantity, line.UnitPrice, line.LineTotal })));
        Assert.All(first, invoice => Assert.Matches("^INV-[A-Z]{3}-[A-HJ-NP-Z2-9]{5}$", invoice.InvoiceNumber));
        Assert.All(first, invoice => Assert.Matches("^PO-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{3}$", invoice.PurchaseOrderNumber));
    }

    [Fact]
    public void GeneratorProducesUniqueInvoiceAndPurchaseOrderNumbers()
    {
        var generator = new DeterministicInvoiceGenerator(new(Seed: 42, BatchSize: 50));
        var invoices = generator.GenerateBatch(new DateOnly(2026, 10, 14));

        Assert.Equal(invoices.Count, invoices.Select(x => x.InvoiceNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(invoices.Count, invoices.Select(x => x.PurchaseOrderNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void GeneratorKeepsAmountsWithinConfiguredLimitsAndDueDateAfterInvoiceDate()
    {
        var invoices = new DeterministicInvoiceGenerator(new(Seed: 81, BatchSize: 30, MinimumAmount: 250, MaximumAmount: 1000))
            .GenerateBatch(new DateOnly(2026, 10, 14));

        Assert.All(invoices, invoice =>
        {
            Assert.InRange(invoice.Lines.Sum(line => line.LineTotal), 250m, 1000m);
            Assert.True(invoice.DueDate > invoice.InvoiceDate);
            Assert.InRange(invoice.Lines.Count, 1, 5);
        });
    }

    [Fact]
    public void InvoiceLineRejectsInvalidQuantityAndTaxRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceLine.Create("Invalid", 0, 20, .2m));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceLine.Create("Invalid", 1, 20, 1.1m));
    }

    [Fact]
    public void SameAutonomousAgentCannotApproveOwnInvoice()
    {
        var invoice = CreateSubmittedInvoice();
        var act = () => invoice.Approve(Entry, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void EntryCreatedInvoiceCanBeApprovedByDistinctApprovalAgent()
    {
        var invoice = CreateSubmittedInvoice();
        invoice.Approve(Approval, DateTimeOffset.UtcNow);

        Assert.Equal(InvoiceStatus.ApprovedForPayment, invoice.Status);
        Assert.DoesNotContain("Paid", Enum.GetNames<InvoiceStatus>());
    }

    [Fact]
    public void ManualReviewKeepsInvoiceInPendingApproval()
    {
        var invoice = CreateSubmittedInvoice();
        invoice.RequireManualReview("Amount requires human review.");

        Assert.Equal(InvoiceStatus.PendingApproval, invoice.Status);
        Assert.Equal(DecisionDisposition.ManualReviewRequired, invoice.Disposition);
        Assert.Throws<InvalidOperationException>(() => invoice.Approve(Approval, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ApprovalLimitProducesManualReviewDisposition()
    {
        var invoice = CreateSubmittedInvoice();
        var evaluation = new ApprovalPolicy(new ApprovalPolicyOptions(100m))
            .Evaluate(invoice, invoiceNumberIsUnique: true, purchaseOrderIsUnique: true);

        Assert.Equal(DecisionDisposition.ManualReviewRequired, evaluation.Disposition);
    }

    [Fact]
    public void DecidedInvoiceCannotBeApprovedAgain()
    {
        var invoice = CreateSubmittedInvoice();
        invoice.Approve(Approval, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => invoice.Approve(Approval, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => invoice.Reject(Approval, "No", DateTimeOffset.UtcNow));
    }

    private static Invoice CreateSubmittedInvoice()
    {
        var supplier = FictionalSupplierCatalog.All[0];
        var line = InvoiceLine.Create("Fictional office supplies", 2, 50, .2m);
        var invoice = Invoice.CreateDraft(
            Guid.NewGuid(), "INV-NWS-7K29Q", "PO-4Z8P-193", supplier,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "EUR",
            "Fictional test invoice", supplier.CostCenters[0], [line], Entry,
            "correlation-test", DateTimeOffset.UtcNow);
        invoice.Submit(DateTimeOffset.UtcNow);
        return invoice;
    }
}
