using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using D1.Invoice.Contracts;
using D1.Invoice.Identity;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace D1.Invoice.Interactive;

public interface IInteractiveInvoiceService
{
    Task<InteractiveInvoiceResult> PreviewAsync(CreateInvoiceRequest request, ClaimsPrincipal signedInUser, CancellationToken cancellationToken);
    Task<InteractiveInvoiceResult> SubmitAsync(CreateInvoiceRequest request, ClaimsPrincipal signedInUser, CancellationToken cancellationToken);
    Task<IReadOnlyList<InvoiceResponse>> ListMineAsync(ClaimsPrincipal signedInUser, CancellationToken cancellationToken);
}

public sealed class InteractiveInvoiceService(
    IHttpClientFactory httpClientFactory,
    IAuthorizationHeaderProvider authorizationHeaderProvider,
    IConfiguration configuration,
    ILogger<InteractiveInvoiceService> logger) : IInteractiveInvoiceService
{
    public async Task<InteractiveInvoiceResult> PreviewAsync(CreateInvoiceRequest request, ClaimsPrincipal signedInUser, CancellationToken cancellationToken)
    {
        var userId = RequireSignedInUser(signedInUser);
        var agentId = configuration["INTERACTIVE_AGENT_CLIENT_ID"] ?? throw new InvalidOperationException("INTERACTIVE_AGENT_CLIENT_ID is missing.");
        var scope = configuration["INVOICE_CREATE_DELEGATED_SCOPE"] ?? throw new InvalidOperationException("INVOICE_CREATE_DELEGATED_SCOPE is missing.");
        var authorization = await GetUserAuthorizationAsync(agentId, scope, signedInUser, cancellationToken);
        var claims = authorization.Claims;
        EnsureUserIdentity(signedInUser, claims);

        var correlationId = ActivityTraceId.CreateRandom().ToString();
        using var previewRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/invoices/preview")
        {
            Content = JsonContent.Create(request)
        };
        AddHeaders(previewRequest, authorization.Header, authorization.OfflineActor, correlationId);
        using var response = await CreateClient().SendAsync(previewRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Invoice API preview failed ({(int)response.StatusCode}).", null, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<InvoiceResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Invoice API returned an empty preview response.");
        logger.LogInformation("Interactive invoice preview UserId={UserId} AgentClientId={AgentClientId} Permission={Permission} CorrelationId={CorrelationId} Result=Previewed",
            userId, agentId, scope, correlationId);
        return new InteractiveInvoiceResult(preview, correlationId, claims, scope, "Previewed");
    }

    public async Task<InteractiveInvoiceResult> SubmitAsync(CreateInvoiceRequest request, ClaimsPrincipal signedInUser, CancellationToken cancellationToken)
    {
        var userId = RequireSignedInUser(signedInUser);
        var agentId = configuration["INTERACTIVE_AGENT_CLIENT_ID"] ?? throw new InvalidOperationException("INTERACTIVE_AGENT_CLIENT_ID is missing.");
        var createScope = configuration["INVOICE_CREATE_DELEGATED_SCOPE"] ?? throw new InvalidOperationException("INVOICE_CREATE_DELEGATED_SCOPE is missing.");
        var authorization = await GetUserAuthorizationAsync(agentId, createScope, signedInUser, cancellationToken);
        var claims = authorization.Claims;
        EnsureUserIdentity(signedInUser, claims);

        var correlationId = ActivityTraceId.CreateRandom().ToString();
        var client = CreateClient();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/invoices")
        {
            Content = JsonContent.Create(request)
        };
        AddHeaders(createRequest, authorization.Header, authorization.OfflineActor, correlationId);
        using var createResponse = await client.SendAsync(createRequest, cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"Invoice API create failed ({(int)createResponse.StatusCode}).", null, createResponse.StatusCode);
        var draft = await createResponse.Content.ReadFromJsonAsync<InvoiceResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Invoice API returned an empty response.");

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/invoices/{draft.Id}/submit");
        AddHeaders(submitRequest, authorization.Header, authorization.OfflineActor, correlationId);
        using var submitResponse = await client.SendAsync(submitRequest, cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"Invoice API submit failed ({(int)submitResponse.StatusCode}).", null, submitResponse.StatusCode);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<InvoiceResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Invoice API returned an empty submission response.");

        logger.LogInformation("Interactive invoice submitted UserId={UserId} AgentClientId={AgentClientId} InvoiceId={InvoiceId} Permission={Permission} CorrelationId={CorrelationId} Result=Succeeded",
            userId, agentId, submitted.Id, "Invoice.Create.Delegated", correlationId);
        return new InteractiveInvoiceResult(submitted, correlationId, claims, "Invoice.Create.Delegated", "Succeeded");
    }

    public async Task<IReadOnlyList<InvoiceResponse>> ListMineAsync(ClaimsPrincipal signedInUser, CancellationToken cancellationToken)
    {
        var userId = RequireSignedInUser(signedInUser);
        var agentId = configuration["INTERACTIVE_AGENT_CLIENT_ID"] ?? throw new InvalidOperationException("INTERACTIVE_AGENT_CLIENT_ID is missing.");
        var readScope = configuration["INVOICE_CREATE_DELEGATED_SCOPE"] ?? throw new InvalidOperationException("INVOICE_CREATE_DELEGATED_SCOPE is missing.");
        var authorization = await GetUserAuthorizationAsync(agentId, readScope, signedInUser, cancellationToken);
        EnsureUserIdentity(signedInUser, authorization.Claims);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/invoices");
        AddHeaders(request, authorization.Header, authorization.OfflineActor, ActivityTraceId.CreateRandom().ToString());
        using var response = await CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Invoice API read failed ({(int)response.StatusCode}).", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<InvoiceResponse>>(cancellationToken: cancellationToken) ?? [];
    }

    private async Task<(string? Header, string OfflineActor, SafeIdentitySummary Claims)> GetUserAuthorizationAsync(
        string agentId, string scope, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (string.Equals(configuration["DEMO_MODE"], "offline", StringComparison.OrdinalIgnoreCase))
        {
            var userId = RequireSignedInUser(user);
            var actor = user.FindAll("roles").Any(role => role.Value == "InvoiceSubmitter") ? "interactive" : "interactive-no-role";
            return (null, actor, new SafeIdentitySummary(userId, "offline-interactive-agent", "api://offline-invoice-api",
                "offline-tenant", agentId, [], [scope], "offline-interactive-blueprint",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30)));
        }
        var options = new AuthorizationHeaderProviderOptions().WithAgentIdentity(agentId);
        var header = await authorizationHeaderProvider.CreateAuthorizationHeaderForUserAsync([scope], options, user, cancellationToken);
        return (header, string.Empty, SafeIdentityClaimReader.Read(header));
    }

    private HttpClient CreateClient()
    {
        var baseUrl = configuration["INVOICE_API_BASE_URL"] ?? throw new InvalidOperationException("INVOICE_API_BASE_URL is missing.");
        var client = httpClientFactory.CreateClient("invoice-api");
        client.BaseAddress ??= new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        return client;
    }

    private static string RequireSignedInUser(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("A signed-in user is required.");
        return principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("The signed-in user has no stable subject identifier.");
    }

    private void EnsureUserIdentity(ClaimsPrincipal user, SafeIdentitySummary claims)
    {
        if (string.Equals(configuration["DEMO_MODE"], "offline", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(claims.Subject, RequireSignedInUser(user), StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Offline token subject does not match the signed-in user.");
            return;
        }

        var objectId = user.FindFirst("oid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        var tenantId = user.FindFirst("tid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        if (!Guid.TryParse(objectId, out var userObjectId) || userObjectId == Guid.Empty ||
            !Guid.TryParse(tenantId, out var userTenantId) || userTenantId == Guid.Empty ||
            !Guid.TryParse(claims.ObjectId, out var tokenObjectId) || tokenObjectId != userObjectId ||
            !Guid.TryParse(claims.TenantId, out var tokenTenantId) || tokenTenantId != userTenantId)
            throw new UnauthorizedAccessException("OBO token user or tenant does not match the signed-in user.");
    }

    private static void AddHeaders(HttpRequestMessage request, string? authorizationHeader, string offlineActor, string correlationId)
    {
        if (authorizationHeader is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorizationHeader);
        else
            request.Headers.TryAddWithoutValidation("X-Demo-Actor", offlineActor);
        request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
    }
}
