using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace D1.Invoice.Api;

public sealed class OfflineDemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "D1OfflineDemo";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var actor = Request.Headers["X-Demo-Actor"].FirstOrDefault()?.Trim().ToLowerInvariant() ?? "entry";
        var claims = actor switch
        {
            "entry" => new[]
            {
                new Claim("sub", "offline-entry-agent"),
                new Claim("appid", "offline-entry-agent"),
                new Claim("name", "D1-Invoice-Entry-Agent"),
                new Claim("roles", "Invoice.Create.All")
            },
            "approval" => new[]
            {
                new Claim("sub", "offline-approval-agent"),
                new Claim("appid", "offline-approval-agent"),
                new Claim("name", "D1-Invoice-Approval-Agent"),
                new Claim("roles", "Invoice.Read.All"),
                new Claim("roles", "Invoice.Approve.All")
            },
            "interactive" => new[]
            {
                new Claim("sub", "offline-user-1001"),
                new Claim("preferred_username", "demo.user@example.test"),
                new Claim("name", "Demo Signed-In User"),
                new Claim("act", "offline-interactive-agent"),
                new Claim("scp", "Invoice.Create.Delegated"),
                new Claim("roles", "InvoiceSubmitter")
            },
            "interactive-create-only" => new[]
            {
                new Claim("sub", "offline-user-create-only"),
                new Claim("name", "Demo Create-Only User"),
                new Claim("act", "offline-interactive-agent"),
                new Claim("scp", "Invoice.Create.Delegated"),
                new Claim("roles", "InvoiceSubmitter")
            },
            "interactive-no-role" => new[]
            {
                new Claim("sub", "offline-user-1002"),
                new Claim("name", "Demo User Without Submitter Role"),
                new Claim("act", "offline-interactive-agent"),
                new Claim("scp", "Invoice.Create.Delegated")
            },
            "wrong-entry" => new[]
            {
                new Claim("sub", "offline-entry-agent"),
                new Claim("appid", "offline-entry-agent"),
                new Claim("name", "D1-Invoice-Entry-Agent"),
                new Claim("roles", "Invoice.Create.All")
            },
            _ => Array.Empty<Claim>()
        };

        if (claims.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown offline demo actor."));
        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
