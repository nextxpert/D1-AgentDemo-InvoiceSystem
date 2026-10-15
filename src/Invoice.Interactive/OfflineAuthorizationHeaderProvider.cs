using System.Security.Claims;
using Microsoft.Identity.Abstractions;

namespace D1.Invoice.Interactive;

public sealed class OfflineAuthorizationHeaderProvider : IAuthorizationHeaderProvider
{
    public Task<string> CreateAuthorizationHeaderAsync(IEnumerable<string> scopes, AuthorizationHeaderProviderOptions? authorizationHeaderProviderOptions = null, ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new InvalidOperationException("Offline mode never acquires access tokens."));

    public Task<string> CreateAuthorizationHeaderForAppAsync(string scopes, AuthorizationHeaderProviderOptions? downstreamApiOptions = null, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new InvalidOperationException("Offline mode never acquires access tokens."));

    public Task<string> CreateAuthorizationHeaderForUserAsync(IEnumerable<string> scopes, AuthorizationHeaderProviderOptions? authorizationHeaderProviderOptions = null, ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new InvalidOperationException("Offline mode never acquires access tokens."));
}
