using System.Security.Claims;
using System.Text.Json;
using D1.Invoice.Application;
using D1.Invoice.Domain;

namespace D1.Invoice.Api;

public sealed class HttpInvoiceActorContext(IHttpContextAccessor accessor) : IActorContext
{
    public ActorContext Current
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
                throw new InvalidOperationException("An authenticated actor is required.");

            var subject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var delegated = HasScope(principal, "Invoice.Create.Delegated");
            var actingAgent = GetAgentActor(principal);
            var identityId = delegated ? actingAgent ?? "D1-Interactive-Invoice-Assistant" :
                principal.FindFirstValue("appid") ?? principal.FindFirstValue("azp") ?? subject;
            var identityName = delegated
                ? "D1-Interactive-Invoice-Assistant"
                : principal.FindFirstValue("name") ?? identityId;

            return new ActorContext(
                identityId,
                identityName,
                delegated ? ActorType.InteractiveAgent : ActorType.AutonomousAgent,
                delegated ? subject : null,
                delegated ? principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("upn") : null);
        }
    }

    public static bool HasScope(ClaimsPrincipal principal, string scope)
        => (principal.FindFirstValue("scp") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope, StringComparer.Ordinal);

    public static string? GetAgentActor(ClaimsPrincipal principal)
    {
        var actor = principal.FindFirstValue("act");
        if (string.IsNullOrWhiteSpace(actor)) actor = principal.FindFirstValue("xms_agent");
        if (!string.IsNullOrWhiteSpace(actor))
        {
            if (!actor.StartsWith('{')) return actor;
            try
            {
                using var document = JsonDocument.Parse(actor);
                var root = document.RootElement;
                return root.TryGetProperty("sub", out var sub) ? sub.GetString()
                    : root.TryGetProperty("oid", out var oid) ? oid.GetString()
                    : actor;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var isAgentIdentity = principal.FindAll("xms_act_fct")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains("11", StringComparer.Ordinal);
        if (!isAgentIdentity) return null;

        return principal.FindFirstValue("azp") ?? principal.FindFirstValue("appid");
    }
}
