using System.Text.Json;
using System.Text.Json.Serialization;
using D1.Invoice.Contracts;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace D1.Invoice.Identity;

public sealed class AgentAuthorization
{
    public AgentAuthorization(string authorizationHeader, SafeIdentitySummary claims)
    {
        AuthorizationHeader = authorizationHeader;
        Claims = claims;
    }

    [JsonIgnore]
    public string AuthorizationHeader { get; }

    public SafeIdentitySummary Claims { get; }
}

public interface IInvoiceAgentTokenProvider
{
    Task<AgentAuthorization> GetAutonomousAsync(string agentClientId, string audience, CancellationToken cancellationToken);
    Task<AgentAuthorization> GetInteractiveAsync(string agentClientId, string scope, System.Security.Claims.ClaimsPrincipal signedInUser, CancellationToken cancellationToken);
}

public sealed class InvoiceAgentTokenProvider(IAuthorizationHeaderProvider authorizationHeaderProvider) : IInvoiceAgentTokenProvider
{
    public async Task<AgentAuthorization> GetAutonomousAsync(string agentClientId, string audience, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        var options = new AuthorizationHeaderProviderOptions().WithAgentIdentity(agentClientId);
        var header = await authorizationHeaderProvider.CreateAuthorizationHeaderForAppAsync(
            $"{audience.TrimEnd('/')}/.default", options, cancellationToken).ConfigureAwait(false);
        return new AgentAuthorization(header, SafeIdentityClaimReader.Read(header));
    }

    public async Task<AgentAuthorization> GetInteractiveAsync(
        string agentClientId,
        string scope,
        System.Security.Claims.ClaimsPrincipal signedInUser,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(signedInUser);
        var options = new AuthorizationHeaderProviderOptions().WithAgentIdentity(agentClientId);
        var header = await authorizationHeaderProvider.CreateAuthorizationHeaderForUserAsync(
            [scope], options, signedInUser, cancellationToken).ConfigureAwait(false);
        return new AgentAuthorization(header, SafeIdentityClaimReader.Read(header));
    }
}

public static class SafeIdentityClaimReader
{
    private const string BearerPrefix = "Bearer ";

    public static SafeIdentitySummary Read(string authorizationHeader)
    {
        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Token provider did not return a Bearer authorization header.");
        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        var segments = token.Split('.');
        if (segments.Length < 2) throw new InvalidOperationException("Token provider returned a malformed JWT.");
        var payload = DecodeBase64Url(segments[1]);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return new SafeIdentitySummary(
            Subject: String(root, "sub") ?? String(root, "oid") ?? "unknown",
            Actor: Actor(root),
            Audience: Audience(root),
            TenantId: String(root, "tid") ?? "unknown",
            ClientId: String(root, "azp") ?? String(root, "appid") ?? "unknown",
            Roles: StringArray(root, "roles"),
            Scopes: (String(root, "scp") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ParentBlueprintClientId: String(root, "xms_par_app_azp"),
            IssuedAtUtc: UnixTime(root, "iat"),
            ExpiresAtUtc: UnixTime(root, "exp"));
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static string? String(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Audience(JsonElement root)
    {
        if (!root.TryGetProperty("aud", out var value)) return "unknown";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "unknown",
            JsonValueKind.Array => string.Join(',', value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
            _ => "unknown"
        };
    }

    private static IReadOnlyList<string> StringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return [];
        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString() ?? string.Empty],
            JsonValueKind.Array => value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).ToArray(),
            _ => []
        };
    }

    private static string? Actor(JsonElement root)
    {
        if (!root.TryGetProperty("act", out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object) return String(value, "sub") ?? String(value, "oid");
        return null;
    }

    private static DateTimeOffset? UnixTime(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
