using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace D1.Invoice.Interactive;

public sealed class OfflineInteractiveAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "D1OfflineInteractive";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var actor = Request.Headers["X-Demo-Actor"].FirstOrDefault();
        if (actor is not ("interactive" or "interactive-no-role"))
            return Task.FromResult(AuthenticateResult.NoResult());
        var submitter = actor == "interactive";
        var subject = submitter ? "offline-user-1001" : "offline-user-1002";
        var claims = new List<Claim>
        {
            new("sub", subject),
            new(ClaimTypes.NameIdentifier, subject),
            new("name", submitter ? "Demo InvoiceSubmitter" : "Demo Read-Only User"),
            new("preferred_username", submitter ? "invoice.submitter@example.test" : "readonly@example.test"),
            new("scp", "access_as_user")
        };
        if (submitter) claims.Add(new Claim("roles", "InvoiceSubmitter"));
        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
