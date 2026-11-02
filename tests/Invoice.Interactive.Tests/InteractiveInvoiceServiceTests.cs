using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using D1.Invoice.Contracts;
using D1.Invoice.Identity;
using D1.Invoice.Interactive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Abstractions;
using Xunit;

namespace D1.Invoice.Interactive.Tests;

public sealed class InteractiveInvoiceServiceTests
{
    private const string UserObjectId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task SubmitRequiresAuthenticatedUser()
    {
        var auth = new RecordingAuthorizationHeaderProvider(CreateHeader("user-1"));
        var service = CreateService(auth, new ResponseHandler());
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SubmitAsync(Request(), user, CancellationToken.None));
        Assert.Null(auth.UserPrincipal);
    }

    [Fact]
    public async Task SubmitUsesSignedInPrincipalForOBOAndReturnsOnlySafeClaims()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "blueprint-user-subject"),
            new Claim("oid", UserObjectId),
            new Claim("tid", TenantId),
            new Claim("name", "Demo User"),
            new Claim("roles", "InvoiceSubmitter")
        ], "test"));
        var auth = new RecordingAuthorizationHeaderProvider(CreateHeader("user-1001"));
        var handler = new ResponseHandler();
        var service = CreateService(auth, handler);

        var result = await service.SubmitAsync(Request(), user, CancellationToken.None);

        Assert.Same(user, auth.UserPrincipal);
        Assert.Equal("api://invoice-api/Invoice.Create.Delegated", Assert.Single(auth.Scopes));
        Assert.Equal("user-1001", result.Claims.Subject);
        Assert.Equal(UserObjectId, result.Claims.ObjectId);
        Assert.Equal(TenantId, result.Claims.TenantId);
        Assert.Equal("interactive-agent-1", result.Claims.Actor);
        Assert.Equal("Succeeded", result.Result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.Authorization?.Scheme));
    }

    [Theory]
    [InlineData("oid", "tid")]
    [InlineData("http://schemas.microsoft.com/identity/claims/objectidentifier", "http://schemas.microsoft.com/identity/claims/tenantid")]
    public async Task ListAllowsAudienceSpecificSubjectsForSameTenantUser(string objectClaim, string tenantClaim)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "blueprint-user-subject"), new Claim(objectClaim, UserObjectId), new Claim(tenantClaim, TenantId)
        ], "test"));
        var auth = new RecordingAuthorizationHeaderProvider(CreateHeader("invoice-api-user-subject"));
        var handler = new ResponseHandler();
        var service = CreateService(auth, handler);

        var result = await service.ListMineAsync(user, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("/v1/invoices", Assert.Single(handler.Requests).Path);
    }

    [Theory]
    [InlineData("", TenantId)]
    [InlineData(UserObjectId, "")]
    public async Task MissingSignedInTenantUserIsDenied(string objectId, string tenantId)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "same-subject"), new Claim("oid", objectId), new Claim("tid", tenantId)
        ], "test"));
        var auth = new RecordingAuthorizationHeaderProvider(CreateHeader("same-subject"));
        var handler = new ResponseHandler();
        var service = CreateService(auth, handler);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListMineAsync(user, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("33333333-3333-3333-3333-333333333333", TenantId)]
    [InlineData(UserObjectId, "33333333-3333-3333-3333-333333333333")]
    [InlineData("", TenantId)]
    [InlineData(UserObjectId, "")]
    public async Task MismatchedOrMissingOBOIdentityIsDeniedBeforeApiCalls(string objectId, string tenantId)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "same-subject"), new Claim("oid", UserObjectId), new Claim("tid", TenantId)
        ], "test"));
        var auth = new RecordingAuthorizationHeaderProvider(CreateHeader("same-subject", objectId, tenantId));
        var handler = new ResponseHandler();
        var service = CreateService(auth, handler);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(Request(), user, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SubmitAsync(Request(), user, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListMineAsync(user, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private static IInteractiveInvoiceService CreateService(RecordingAuthorizationHeaderProvider auth, HttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://invoice-api.example.test/") });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["INTERACTIVE_AGENT_CLIENT_ID"] = "interactive-agent-1",
            ["INVOICE_CREATE_DELEGATED_SCOPE"] = "api://invoice-api/Invoice.Create.Delegated",
            ["INVOICE_API_BASE_URL"] = "https://invoice-api.example.test/"
        }).Build();
        return new InteractiveInvoiceService(factory, auth, configuration, NullLogger<InteractiveInvoiceService>.Instance);
    }

    private static CreateInvoiceRequest Request()
        => new("INV-NWS-INTER1", "PO-INT-001", "NWS", new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1),
            "EUR", "Fictional invoice", "FIN-100", [new InvoiceLineRequest("Paper", 1, 50, .2m)]);

    private static string CreateHeader(string subject, string objectId = UserObjectId, string tenantId = TenantId)
    {
        var payload = $$"""{"sub":"{{subject}}","oid":"{{objectId}}","act":{"sub":"interactive-agent-1"},"aud":"api://invoice-api","tid":"{{tenantId}}","azp":"interactive-agent-1","scp":"Invoice.Create.Delegated"}""";
        return $"Bearer eyJhbGciOiJub25lIn0.{Base64Url(Encoding.UTF8.GetBytes(payload))}.";
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingAuthorizationHeaderProvider(string header) : IAuthorizationHeaderProvider
    {
        public ClaimsPrincipal? UserPrincipal { get; private set; }
        public AuthorizationHeaderProviderOptions? Options { get; private set; }
        public IReadOnlyList<string> Scopes { get; private set; } = [];

        public Task<string> CreateAuthorizationHeaderForUserAsync(IEnumerable<string> scopes,
            AuthorizationHeaderProviderOptions? authorizationHeaderProviderOptions = null,
            ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default)
        {
            Scopes = scopes.ToArray();
            Options = authorizationHeaderProviderOptions;
            UserPrincipal = claimsPrincipal;
            return Task.FromResult(header);
        }

        public Task<string> CreateAuthorizationHeaderAsync(IEnumerable<string> scopes,
            AuthorizationHeaderProviderOptions? authorizationHeaderProviderOptions = null,
            ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default)
            => CreateAuthorizationHeaderForUserAsync(scopes, authorizationHeaderProviderOptions, claimsPrincipal, cancellationToken);

        public Task<string> CreateAuthorizationHeaderForAppAsync(string scopes,
            AuthorizationHeaderProviderOptions? downstreamApiOptions = null, CancellationToken cancellationToken = default)
            => Task.FromResult(header);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri!.AbsolutePath, request.Headers.Authorization));
            var invoice = new InvoiceResponse(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "INV-NWS-INTER1", "PO-INT-001", "Northwind Office Supplies", 60m, "EUR",
                request.RequestUri.AbsolutePath.EndsWith("submit", StringComparison.Ordinal) ? "PendingApproval" : "Draft",
                "Pending", null, "correlation-test");
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new[] { invoice })
                });
            return Task.FromResult(new HttpResponseMessage(request.RequestUri.AbsolutePath.EndsWith("submit", StringComparison.Ordinal)
                ? HttpStatusCode.OK
                : HttpStatusCode.Created)
            {
                Content = System.Net.Http.Json.JsonContent.Create(invoice)
            });
        }
    }

    private sealed record CapturedRequest(string Path, AuthenticationHeaderValue? Authorization);
}
