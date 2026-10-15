using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using D1.Invoice.Contracts;
using D1.Invoice.Domain;
using D1.Invoice.Worker;
using Xunit;

namespace D1.Invoice.Worker.Tests;

public sealed class InvoiceWorkerClientTests
{
    [Fact]
    public async Task OfflineEntryUsesActorHeaderAndNeverAddsAuthorizationToken()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var options = new InvoiceWorkerOptions(true, 20261014, 1, 100, 15000, 90, "offline-entry-agent", "offline-approval-agent");
        var client = new InvoiceWorkerApiClient(http, new OfflineWorkerAgentAuthenticator(), options);
        var draft = new DeterministicInvoiceGenerator(new(Seed: 41, BatchSize: 1)).GenerateOne(new DateOnly(2026, 10, 14));

        var result = await client.CreateAndSubmitAsync(draft, "corr-test", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.NotNull(result.Invoice);
        Assert.Equal("entry", handler.Requests[0].Actor);
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("entry", handler.Requests[1].Actor);
        Assert.Null(handler.Requests[1].Authorization);
        Assert.Contains("Invoice.Create.All", result.Claims.Roles);
    }

    [Fact]
    public async Task OfflineApprovalUsesSeparateActorAndRoleSummary()
    {
        var authenticator = new OfflineWorkerAgentAuthenticator();
        var auth = await authenticator.GetAsync("offline-approval-agent", "Invoice.Approve.All", CancellationToken.None);

        Assert.Equal("approval", auth.OfflineActor);
        Assert.Null(auth.BearerHeader);
        Assert.Equal("offline-approval-agent", auth.Claims.ClientId);
        Assert.Equal("Invoice.Approve.All", Assert.Single(auth.Claims.Roles));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        private int _sequence;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Headers.TryGetValues("X-Demo-Actor", out var values) ? values.Single() : null,
                request.Headers.Authorization?.ToString()));
            if (request.RequestUri!.AbsolutePath == "/v1/invoices")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                var id = Guid.NewGuid();
                var response = new InvoiceResponse(id, document.RootElement.GetProperty("invoiceNumber").GetString()!,
                    document.RootElement.GetProperty("purchaseOrderNumber").GetString()!, "Northwind Office Supplies",
                    120m, "EUR", "Draft", "Pending", null, "corr-test");
                return Json(HttpStatusCode.Created, response);
            }
            Interlocked.Increment(ref _sequence);
            return Json(HttpStatusCode.OK, new InvoiceResponse(
                Guid.NewGuid(), "INV-NWS-TEST1", "PO-TEST-001", "Northwind Office Supplies",
                120m, "EUR", "PendingApproval", "Pending", null, "corr-test"));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, InvoiceResponse response)
            => new(status) { Content = System.Net.Http.Json.JsonContent.Create(response) };
    }

    private sealed record CapturedRequest(string? Actor, string? Authorization);
}
