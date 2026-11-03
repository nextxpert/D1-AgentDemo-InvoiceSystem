using System.Security.Claims;
using System.Text;
using D1.Invoice.Identity;
using Microsoft.Identity.Abstractions;
using Xunit;

namespace D1.Invoice.Identity.Tests;

public sealed class InvoiceAgentTokenProviderTests
{
    [Fact]
    public async Task InteractiveAcquisitionUsesSignedInPrincipalAndAgentIdentityOptions()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-123")], "Bearer"));
        var headerProvider = new RecordingAuthorizationHeaderProvider();
        var provider = new InvoiceAgentTokenProvider(headerProvider);

        var result = await provider.GetInteractiveAsync("agent-client-id", "api://invoice-api/Invoice.Create.Delegated", principal, CancellationToken.None);

        Assert.Same(principal, headerProvider.UserPrincipal);
        Assert.NotNull(headerProvider.Options?.AcquireTokenOptions?.ExtraParameters);
        Assert.NotEmpty(headerProvider.Options.AcquireTokenOptions.ExtraParameters);
        Assert.Equal("user-123", result.Claims.Subject);
        Assert.Equal("Invoice.Create.Delegated", Assert.Single(result.Claims.Scopes));
    }

    [Fact]
    public void SafeIdentitySummaryUsesAgentIdentityFacetAndAuthorizedParty()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"azp\":\"interactive-agent-client-id\",\"xms_act_fct\":\"3 9 11\"}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var claims = SafeIdentityClaimReader.Read($"Bearer e30.{payload}.");

        Assert.Equal("interactive-agent-client-id", claims.Actor);
    }

    private sealed class RecordingAuthorizationHeaderProvider : IAuthorizationHeaderProvider
    {
        public ClaimsPrincipal? UserPrincipal { get; private set; }
        public AuthorizationHeaderProviderOptions? Options { get; private set; }

        public Task<string> CreateAuthorizationHeaderAsync(IEnumerable<string> scopes, AuthorizationHeaderProviderOptions? authorizationHeaderProviderOptions = null, ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default)
        {
            UserPrincipal = claimsPrincipal;
            Options = authorizationHeaderProviderOptions;
            return Task.FromResult(TestHeader);
        }

        public Task<string> CreateAuthorizationHeaderForAppAsync(string scopes, AuthorizationHeaderProviderOptions? downstreamApiOptions = null, CancellationToken cancellationToken = default)
        {
            Options = downstreamApiOptions;
            return Task.FromResult(TestHeader);
        }

        public Task<string> CreateAuthorizationHeaderForUserAsync(IEnumerable<string> scopes, AuthorizationHeaderProviderOptions? authorizationHeaderProviderOptions = null, ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default)
        {
            UserPrincipal = claimsPrincipal;
            Options = authorizationHeaderProviderOptions;
            return Task.FromResult(TestHeader);
        }

        private const string TestHeader = "Bearer eyJhbGciOiJub25lIn0.eyJzdWIiOiJ1c2VyLTEyMyIsImF1ZCI6ImFwaTovL2ludm9pY2UtYXBpIiwidGlkIjoidGVuYW50LTEyMyIsImF6cCI6ImFnZW50LWNsaWVudC1pZCIsInNjcCI6Ikludm9pY2UuQ3JlYXRlLkRlbGVnYXRlZCJ9.";
    }
}
