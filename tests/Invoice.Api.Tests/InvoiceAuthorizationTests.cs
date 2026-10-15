using System.Net;
using System.Net.Http.Json;
using D1.Invoice.Contracts;
using Xunit;

namespace D1.Invoice.Api.Tests;

public sealed class InvoiceAuthorizationTests
{
    [Fact]
    public async Task EntryCanCreateAndSubmitButCannotApprove()
    {
        using var factory = new InvoiceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "entry");
        var response = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-ENTRY1", "PO-ENTRY-001"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = await response.Content.ReadFromJsonAsync<InvoiceResponse>();
        Assert.NotNull(invoice);

        var submit = await client.PostAsync($"/v1/invoices/{invoice.Id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var approve = await client.PostAsync($"/v1/invoices/{invoice.Id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }

    [Fact]
    public async Task ApprovalAgentCannotCreateAndCanApprovePendingInvoice()
    {
        using var factory = new InvoiceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "approval");
        var denied = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-APPR1", "PO-APPR-001"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        client.DefaultRequestHeaders.Remove("X-Demo-Actor");
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "entry");
        var created = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-APPR2", "PO-APPR-002"));
        var invoice = await created.Content.ReadFromJsonAsync<InvoiceResponse>();
        Assert.NotNull(invoice);
        await client.PostAsync($"/v1/invoices/{invoice.Id}/submit", null);

        client.DefaultRequestHeaders.Remove("X-Demo-Actor");
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "approval");
        var approved = await client.PostAsync($"/v1/invoices/{invoice.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var decision = await approved.Content.ReadFromJsonAsync<ApprovalDecisionResponse>();
        Assert.NotNull(decision);
        Assert.Equal("Approved", decision.Disposition);
    }

    [Fact]
    public async Task InteractiveSubmitterCanCreateButUserWithoutBusinessRoleCannot()
    {
        using var factory = new InvoiceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "interactive-no-role");
        var denied = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-NOROL", "PO-NOROL-001"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        client.DefaultRequestHeaders.Remove("X-Demo-Actor");
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "interactive");
        var created = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-USER1", "PO-USER-001"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task InteractiveCreateScopeListsOnlyInvoicesSubmittedByThatUser()
    {
        using var factory = new InvoiceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "entry");
        var entryCreated = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-OWNR1", "PO-OWNR-001"));
        var invoice = await entryCreated.Content.ReadFromJsonAsync<InvoiceResponse>();
        Assert.NotNull(invoice);
        await client.PostAsync($"/v1/invoices/{invoice.Id}/submit", null);

        client.DefaultRequestHeaders.Remove("X-Demo-Actor");
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "interactive-create-only");
        var response = await client.GetAsync("/v1/invoices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var visible = await response.Content.ReadFromJsonAsync<IReadOnlyList<InvoiceResponse>>();
        Assert.Empty(visible!);
    }

    [Fact]
    public async Task PreviewDoesNotPersistAndDuplicateInvoiceOrPurchaseOrderIsRejected()
    {
        using var factory = new InvoiceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "entry");
        var firstRequest = Request("INV-NWS-DUP01", "PO-DUP-001");

        var preview = await client.PostAsJsonAsync("/v1/invoices/preview", firstRequest);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var created = await client.PostAsJsonAsync("/v1/invoices", firstRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var duplicateInvoice = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-DUP01", "PO-DUP-002"));
        Assert.Equal(HttpStatusCode.Conflict, duplicateInvoice.StatusCode);
        var duplicatePurchaseOrder = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-DUP02", "PO-DUP-001"));
        Assert.Equal(HttpStatusCode.Conflict, duplicatePurchaseOrder.StatusCode);
    }

    [Fact]
    public async Task UnsupportedCurrencyIsRejected()
    {
        using var factory = new InvoiceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Actor", "entry");
        var request = Request("INV-NWS-CUR01", "PO-CUR-001") with { Currency = "JPY" };

        var response = await client.PostAsJsonAsync("/v1/invoices", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LiveApiRejectsMissingBearerTokenWith401()
    {
        using var factory = new InvoiceApiFactory("live");
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/invoices", Request("INV-NWS-NOTOK", "PO-NOTOK-001"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static CreateInvoiceRequest Request(string invoiceNumber, string purchaseOrderNumber)
        => new(invoiceNumber, purchaseOrderNumber, "NWS", new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1),
            "EUR", "Fictional invoice", "FIN-100", [new InvoiceLineRequest("Office paper", 2, 50, .2m)]);

    private sealed record ApprovalDecisionResponse(string Disposition, string Reason);
}
