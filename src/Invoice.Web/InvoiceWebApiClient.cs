using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using D1.Invoice.Contracts;
using Microsoft.Identity.Web;

namespace D1.Invoice.Web;

public interface IInvoiceWebApiClient
{
    Task<InteractiveInvoiceResult> PreviewAsync(CreateInvoiceRequest request, ClaimsPrincipal user, CancellationToken cancellationToken);
    Task<InteractiveInvoiceResult> SubmitAsync(CreateInvoiceRequest request, ClaimsPrincipal user, CancellationToken cancellationToken);
    Task<IReadOnlyList<InvoiceResponse>> ListMineAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
    Task<InvoiceDemoRunResult> RunAutonomousAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
    Task<InvoiceDemoRunResult> ApprovePendingAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
    Task<InvoiceDemoRunResult> RunNegativeTestsAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

public sealed class InvoiceWebApiClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ITokenAcquisition? tokenAcquisition = null) : IInvoiceWebApiClient
{
    public Task<InteractiveInvoiceResult> PreviewAsync(CreateInvoiceRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
        => SendInteractiveAsync<InteractiveInvoiceResult>(HttpMethod.Post, "/interactive/invoices/preview", request, user,
            "INTERACTIVE_BLUEPRINT_ACCESS_SCOPE", cancellationToken);

    public Task<InteractiveInvoiceResult> SubmitAsync(CreateInvoiceRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
        => SendInteractiveAsync<InteractiveInvoiceResult>(HttpMethod.Post, "/interactive/invoices/submit", request, user,
            "INTERACTIVE_BLUEPRINT_ACCESS_SCOPE", cancellationToken);

    public async Task<IReadOnlyList<InvoiceResponse>> ListMineAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => await SendInteractiveAsync<IReadOnlyList<InvoiceResponse>>(HttpMethod.Get, "/interactive/invoices/mine", null, user,
            "INTERACTIVE_BLUEPRINT_ACCESS_SCOPE", cancellationToken) ?? [];

    public Task<InvoiceDemoRunResult> RunAutonomousAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => SendWorkerAsync<InvoiceDemoRunResult>("/api/demo/runs/autonomous", user, cancellationToken);

    public Task<InvoiceDemoRunResult> ApprovePendingAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => SendWorkerAsync<InvoiceDemoRunResult>("/api/demo/invoices/approve-pending", user, cancellationToken);

    public Task<InvoiceDemoRunResult> RunNegativeTestsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => SendWorkerAsync<InvoiceDemoRunResult>("/api/demo/runs/negative-tests", user, cancellationToken);

    private async Task<T> SendInteractiveAsync<T>(HttpMethod method, string path, object? body, ClaimsPrincipal user, string scopeSetting, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("invoice-interactive-api");
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        await AddUserAuthenticationAsync(request, user, scopeSetting, cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Interactive invoice API returned {(int)response.StatusCode}.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Interactive invoice API returned an empty response.");
    }

    private async Task<T> SendWorkerAsync<T>(string path, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("invoice-worker-api");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        await AddUserAuthenticationAsync(request, user, "INVOICE_CREATE_DELEGATED_SCOPE", cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Invoice worker returned {(int)response.StatusCode}.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Invoice worker returned an empty response.");
    }

    private async Task AddUserAuthenticationAsync(HttpRequestMessage request, ClaimsPrincipal user, string scopeSetting, CancellationToken cancellationToken)
    {
        var userId = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "offline-user-1001";
        if (environment.IsDevelopment() && string.Equals(configuration["DEMO_MODE"], "offline", StringComparison.OrdinalIgnoreCase))
        {
            var submitter = user.FindAll("roles").Any(role => role.Value == "InvoiceSubmitter");
            request.Headers.TryAddWithoutValidation("X-Demo-Actor", submitter ? "interactive" : "interactive-no-role");
            request.Headers.TryAddWithoutValidation("x-correlation-id", Guid.NewGuid().ToString("N"));
            return;
        }

        var scope = configuration[scopeSetting] ?? throw new InvalidOperationException($"{scopeSetting} is missing.");
        var tokenProvider = tokenAcquisition ?? throw new InvalidOperationException("Live user token acquisition is not configured.");
        var accessToken = await tokenProvider.GetAccessTokenForUserAsync([scope], user: user);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("x-correlation-id", Guid.NewGuid().ToString("N"));
    }
}
