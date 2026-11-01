using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using D1.Invoice.Application;
using D1.Invoice.Contracts;
using D1.Invoice.Domain;
using D1.Invoice.Identity;

namespace D1.Invoice.Worker;

public sealed record InvoiceWorkerOptions(
    bool Offline,
    int Seed,
    int BatchSize,
    decimal MinimumAmount,
    decimal MaximumAmount,
    int RecentDateWindowDays,
    string EntryAgentClientId,
    string ApprovalAgentClientId);

public sealed record WorkerAgentAuthorization(string? BearerHeader, string OfflineActor, SafeIdentitySummary Claims);

public interface IWorkerAgentAuthenticator
{
    Task<WorkerAgentAuthorization> GetAsync(string agentClientId, string role, CancellationToken cancellationToken);
}

public sealed class LiveWorkerAgentAuthenticator(IInvoiceAgentTokenProvider tokens, IConfiguration configuration) : IWorkerAgentAuthenticator
{
    public async Task<WorkerAgentAuthorization> GetAsync(string agentClientId, string role, CancellationToken cancellationToken)
    {
        var audience = configuration["INVOICE_API_AUDIENCE"] ?? throw new InvalidOperationException("INVOICE_API_AUDIENCE is required in live mode.");
        var authorization = await tokens.GetAutonomousAsync(agentClientId, audience, cancellationToken);
        return new WorkerAgentAuthorization(authorization.AuthorizationHeader, string.Empty, authorization.Claims);
    }
}

public sealed class OfflineWorkerAgentAuthenticator : IWorkerAgentAuthenticator
{
    public Task<WorkerAgentAuthorization> GetAsync(string agentClientId, string role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isEntry = string.Equals(agentClientId, "offline-entry-agent", StringComparison.Ordinal);
        var claims = new SafeIdentitySummary(
            Subject: agentClientId,
            Actor: null,
            Audience: "api://offline-invoice-api",
            TenantId: "offline-tenant",
            ClientId: agentClientId,
            Roles: [role],
            Scopes: [],
            ParentBlueprintClientId: "offline-autonomous-blueprint",
            IssuedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30));
        return Task.FromResult(new WorkerAgentAuthorization(null, isEntry ? "entry" : "approval", claims));
    }
}

public sealed class InvoiceWorkerApiClient(HttpClient http, IWorkerAgentAuthenticator authenticator, InvoiceWorkerOptions options)
{
    public async Task<(InvoiceResponse? Invoice, SafeIdentitySummary Claims, HttpStatusCode Status)> CreateAndSubmitAsync(
        InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken)
    {
        var authorization = await authenticator.GetAsync(options.EntryAgentClientId, "Invoice.Create.All", cancellationToken);
        using var createRequest = Request(HttpMethod.Post, "/v1/invoices", authorization, correlationId);
        createRequest.Content = JsonContent.Create(new CreateInvoiceRequest(
            draft.InvoiceNumber, draft.PurchaseOrderNumber, draft.Supplier.Id, draft.InvoiceDate, draft.DueDate,
            draft.Currency, draft.Description, draft.CostCenter,
            draft.Lines.Select(line => new InvoiceLineRequest(line.Description, line.Quantity, line.UnitPrice, line.TaxRate)).ToArray()));
        using var createResponse = await http.SendAsync(createRequest, cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            return (null, authorization.Claims, createResponse.StatusCode);
        var invoice = await createResponse.Content.ReadFromJsonAsync<InvoiceResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Invoice API returned an empty create response.");

        using var submitRequest = Request(HttpMethod.Post, $"/v1/invoices/{invoice.Id}/submit", authorization, correlationId);
        using var submitResponse = await http.SendAsync(submitRequest, cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            return (invoice, authorization.Claims, submitResponse.StatusCode);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<InvoiceResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Invoice API returned an empty submit response.");
        return (submitted, authorization.Claims, submitResponse.StatusCode);
    }

    public async Task<(IReadOnlyList<InvoiceResponse> Invoices, SafeIdentitySummary Claims, HttpStatusCode Status)> ListPendingAsync(
        string correlationId, CancellationToken cancellationToken)
    {
        var authorization = await authenticator.GetAsync(options.ApprovalAgentClientId, "Invoice.Read.All", cancellationToken);
        using var request = Request(HttpMethod.Get, "/v1/invoices?status=PendingApproval", authorization, correlationId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return ([], authorization.Claims, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<InvoiceResponse>>(cancellationToken: cancellationToken) ?? [];
        return (items, authorization.Claims, response.StatusCode);
    }

    public async Task<(ApprovalDecisionResponse? Decision, SafeIdentitySummary Claims, HttpStatusCode Status)> ApproveAsync(
        Guid invoiceId, string correlationId, CancellationToken cancellationToken)
    {
        var authorization = await authenticator.GetAsync(options.ApprovalAgentClientId, "Invoice.Approve.All", cancellationToken);
        using var request = Request(HttpMethod.Post, $"/v1/invoices/{invoiceId}/approve", authorization, correlationId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return (null, authorization.Claims, response.StatusCode);
        var decision = await response.Content.ReadFromJsonAsync<ApprovalDecisionResponse>(cancellationToken: cancellationToken);
        return (decision, authorization.Claims, response.StatusCode);
    }

    public async Task<(HttpStatusCode Status, SafeIdentitySummary Claims)> TryCreateWithApprovalAgentAsync(
        InvoiceDraftData draft, string correlationId, CancellationToken cancellationToken)
    {
        var authorization = await authenticator.GetAsync(options.ApprovalAgentClientId, "Invoice.Read.All", cancellationToken);
        using var request = Request(HttpMethod.Post, "/v1/invoices", authorization, correlationId);
        request.Content = JsonContent.Create(new CreateInvoiceRequest(
            draft.InvoiceNumber, draft.PurchaseOrderNumber, draft.Supplier.Id, draft.InvoiceDate, draft.DueDate,
            draft.Currency, draft.Description, draft.CostCenter,
            draft.Lines.Select(line => new InvoiceLineRequest(line.Description, line.Quantity, line.UnitPrice, line.TaxRate)).ToArray()));
        using var response = await http.SendAsync(request, cancellationToken);
        return (response.StatusCode, authorization.Claims);
    }

    public async Task<(HttpStatusCode Status, SafeIdentitySummary Claims)> TryApproveWithEntryAgentAsync(
        Guid invoiceId, string correlationId, CancellationToken cancellationToken)
    {
        var authorization = await authenticator.GetAsync(options.EntryAgentClientId, "Invoice.Create.All", cancellationToken);
        using var request = Request(HttpMethod.Post, $"/v1/invoices/{invoiceId}/approve", authorization, correlationId);
        using var response = await http.SendAsync(request, cancellationToken);
        return (response.StatusCode, authorization.Claims);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, WorkerAgentAuthorization authorization, string correlationId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        if (authorization.BearerHeader is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization.BearerHeader);
        else
            request.Headers.TryAddWithoutValidation("X-Demo-Actor", authorization.OfflineActor);
        return request;
    }
}

public sealed class InvoiceDemoOrchestrator(
    InvoiceWorkerApiClient api,
    IDemoRunRepository runs,
    InvoiceWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<InvoiceDemoOrchestrator> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<InvoiceDemoRunResult> GenerateInvoicesAsync(int count, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count), "Count must be between 1 and 100.");
        var id = Guid.NewGuid();
        var correlationId = System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
        var started = timeProvider.GetUtcNow();
        var runOrdinal = (await runs.ListAsync(cancellationToken)).Count;
        var generator = new DeterministicInvoiceGenerator(new InvoiceGenerationOptions(
            options.Seed + runOrdinal, count, options.MinimumAmount, options.MaximumAmount, options.RecentDateWindowDays));
        var invoices = new List<InvoiceResponse>();
        var executions = new List<InvoiceAgentExecution>();
        string status = "Succeeded";
        string? error = null;
        foreach (var draft in generator.GenerateBatch(DateOnly.FromDateTime(started.UtcDateTime)))
        {
            var result = await api.CreateAndSubmitAsync(draft, correlationId, cancellationToken);
            executions.Add(new InvoiceAgentExecution("D1-Invoice-Entry-Agent", result.Claims.ClientId,
                "Invoice.Create.All", result.Status is HttpStatusCode.OK or HttpStatusCode.Created ? "Succeeded" : "Failed",
                result.Claims, result.Status.ToString()));
            if (result.Invoice is not null) invoices.Add(result.Invoice);
            if (result.Status is not (HttpStatusCode.OK or HttpStatusCode.Created))
            {
                status = "Failed";
                error = $"Entry operation failed ({result.Status}).";
                break;
            }
        }
        return await PersistRunAsync(id, correlationId, "GenerateInvoices", status, started, invoices, [], executions, error, cancellationToken);
    }

    public async Task<InvoiceDemoRunResult> ApprovePendingAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var correlationId = System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
        var started = timeProvider.GetUtcNow();
        var pending = await api.ListPendingAsync(correlationId, cancellationToken);
        var executions = new List<InvoiceAgentExecution>
        {
            new("D1-Invoice-Approval-Agent", pending.Claims.ClientId, "Invoice.Read.All",
                pending.Status == HttpStatusCode.OK ? "Succeeded" : "Failed", pending.Claims, pending.Status.ToString())
        };
        var decisions = new List<ApprovalDecisionResponse>();
        var status = pending.Status == HttpStatusCode.OK ? "Succeeded" : "Failed";
        string? error = pending.Status == HttpStatusCode.OK ? null : $"Pending invoice list failed ({pending.Status}).";
        foreach (var invoice in pending.Invoices.Where(item => item.Disposition != "ManualReviewRequired"))
        {
            var result = await api.ApproveAsync(invoice.Id, correlationId, cancellationToken);
            executions.Add(new InvoiceAgentExecution("D1-Invoice-Approval-Agent", result.Claims.ClientId,
                "Invoice.Approve.All", result.Decision is not null ? "Succeeded" : "Failed", result.Claims, result.Status.ToString()));
            if (result.Decision is not null) decisions.Add(result.Decision);
            if (result.Status != HttpStatusCode.OK)
            {
                status = "Failed";
                error = $"Approval operation failed ({result.Status}).";
                break;
            }
        }
        return await PersistRunAsync(id, correlationId, "ApprovePending", status, started, pending.Invoices, decisions, executions, error, cancellationToken);
    }

    public async Task<InvoiceDemoRunResult> RunAutonomousAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var correlationId = System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
        var started = timeProvider.GetUtcNow();
        var invoices = new List<InvoiceResponse>();
        var decisions = new List<ApprovalDecisionResponse>();
        var executions = new List<InvoiceAgentExecution>();
        string status = "Succeeded";
        string? error = null;
        var runOrdinal = (await runs.ListAsync(cancellationToken)).Count;
        var generator = new DeterministicInvoiceGenerator(new InvoiceGenerationOptions(
            options.Seed + runOrdinal, options.BatchSize, options.MinimumAmount, options.MaximumAmount, options.RecentDateWindowDays));

        try
        {
            foreach (var draft in generator.GenerateBatch(DateOnly.FromDateTime(started.UtcDateTime)))
            {
                var result = await api.CreateAndSubmitAsync(draft, correlationId, cancellationToken);
                executions.Add(new InvoiceAgentExecution("D1-Invoice-Entry-Agent", result.Claims.ClientId,
                    "Invoice.Create.All", result.Status is HttpStatusCode.OK or HttpStatusCode.Created ? "Succeeded" : "Failed",
                    result.Claims, result.Status.ToString()));
                if (result.Invoice is not null) invoices.Add(result.Invoice);
                if (result.Status is not (HttpStatusCode.OK or HttpStatusCode.Created))
                    throw new InvalidOperationException($"Entry operation failed ({result.Status}).");
            }

            var pending = await api.ListPendingAsync(correlationId, cancellationToken);
            executions.Add(new InvoiceAgentExecution("D1-Invoice-Approval-Agent", pending.Claims.ClientId,
                "Invoice.Read.All", pending.Status == HttpStatusCode.OK ? "Succeeded" : "Failed", pending.Claims, pending.Status.ToString()));
            if (pending.Status != HttpStatusCode.OK) throw new InvalidOperationException($"Approval list operation failed ({pending.Status}).");

            var generatedIds = invoices.Select(invoice => invoice.Id).ToHashSet();
            foreach (var invoice in pending.Invoices.Where(invoice => generatedIds.Contains(invoice.Id)))
            {
                var result = await api.ApproveAsync(invoice.Id, correlationId, cancellationToken);
                executions.Add(new InvoiceAgentExecution("D1-Invoice-Approval-Agent", result.Claims.ClientId,
                    "Invoice.Approve.All", result.Decision is not null ? "Succeeded" : "Failed", result.Claims, result.Status.ToString()));
                if (result.Decision is not null) decisions.Add(result.Decision);
                if (result.Status != HttpStatusCode.OK) throw new InvalidOperationException($"Approval operation failed ({result.Status}).");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            status = "Failed";
            error = exception.Message;
            logger.LogError(exception, "Autonomous invoice run failed CorrelationId={CorrelationId}", correlationId);
        }

        var finished = timeProvider.GetUtcNow();
        var resultRun = new InvoiceDemoRunResult(id, correlationId, "Autonomous", options.Offline ? "offline" : "live",
            status, started, finished, ApplyDecisions(invoices, decisions), decisions, executions, error);
        var safeSummary = JsonSerializer.Serialize(resultRun, JsonOptions);
        await runs.AddAsync(new DemoRunSnapshot(id, correlationId, resultRun.Mode, status, safeSummary, started, finished), cancellationToken);
        return resultRun;
    }

    public async Task<InvoiceDemoRunResult> RunNegativeTestsAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var correlationId = System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
        var started = timeProvider.GetUtcNow();
        var executions = new List<InvoiceAgentExecution>();
        var runOrdinal = (await runs.ListAsync(cancellationToken)).Count;
        var generator = new DeterministicInvoiceGenerator(new(options.Seed + runOrdinal, 1, options.MinimumAmount, options.MaximumAmount));
        var draft = generator.GenerateOne(DateOnly.FromDateTime(started.UtcDateTime));
        var deniedCreate = await api.TryCreateWithApprovalAgentAsync(draft, correlationId, cancellationToken);
        executions.Add(new InvoiceAgentExecution("D1-Invoice-Approval-Agent", deniedCreate.Claims.ClientId,
            "Invoice.Create.All (attempted)", deniedCreate.Status == HttpStatusCode.Forbidden ? "Denied" : "Unexpected", deniedCreate.Claims, deniedCreate.Status.ToString()));

        var created = await api.CreateAndSubmitAsync(draft, correlationId, cancellationToken);
        if (created.Invoice is not null)
        {
            var deniedApprove = await api.TryApproveWithEntryAgentAsync(created.Invoice.Id, correlationId, cancellationToken);
            executions.Add(new InvoiceAgentExecution("D1-Invoice-Entry-Agent", deniedApprove.Claims.ClientId,
                "Invoice.Approve.All (attempted)", deniedApprove.Status == HttpStatusCode.Forbidden ? "Denied" : "Unexpected", deniedApprove.Claims, deniedApprove.Status.ToString()));
        }

        var status = executions.All(execution => execution.Outcome == "Denied") ? "Succeeded" : "Failed";
        var finished = timeProvider.GetUtcNow();
        var resultRun = new InvoiceDemoRunResult(id, correlationId, "NegativeTests", options.Offline ? "offline" : "live",
            status, started, finished, created.Invoice is null ? [] : [created.Invoice], [], executions,
            status == "Succeeded" ? null : "One or more wrong-role operations were not denied.");
        return await PersistRunAsync(id, correlationId, "NegativeTests", status, started,
            created.Invoice is null ? [] : [created.Invoice], [], executions,
            status == "Succeeded" ? null : "One or more wrong-role operations were not denied.", cancellationToken);
    }

    private static IReadOnlyList<InvoiceResponse> ApplyDecisions(
        IReadOnlyList<InvoiceResponse> invoices, IReadOnlyList<ApprovalDecisionResponse> decisions)
        => invoices.Select(invoice => decisions.LastOrDefault(decision => decision.InvoiceId == invoice.Id) is { } decision
            ? invoice with
            {
                Status = decision.Disposition switch
                {
                    "Approved" => "ApprovedForPayment",
                    "Rejected" => "Rejected",
                    _ => invoice.Status
                },
                Disposition = decision.Disposition,
                ManualReviewReason = decision.Disposition == "ManualReviewRequired" ? decision.Reason : invoice.ManualReviewReason
            }
            : invoice).ToArray();

    private async Task<InvoiceDemoRunResult> PersistRunAsync(
        Guid id,
        string correlationId,
        string stage,
        string status,
        DateTimeOffset started,
        IReadOnlyList<InvoiceResponse> invoices,
        IReadOnlyList<ApprovalDecisionResponse> decisions,
        IReadOnlyList<InvoiceAgentExecution> executions,
        string? error,
        CancellationToken cancellationToken)
    {
        var finished = timeProvider.GetUtcNow();
        var result = new InvoiceDemoRunResult(id, correlationId, stage, options.Offline ? "offline" : "live",
            status, started, finished, ApplyDecisions(invoices, decisions), decisions, executions, error);
        var safeSummary = JsonSerializer.Serialize(result, JsonOptions);
        await runs.AddAsync(new DemoRunSnapshot(id, correlationId, result.Mode, status, safeSummary, started, finished), cancellationToken);
        return result;
    }
}
