using System.Security.Claims;
using D1.Invoice.Contracts;
using D1.Invoice.Identity;
using D1.Invoice.Interactive;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;
using Microsoft.IdentityModel.Tokens;

if (args.Length == 2 && args[0] == "--healthcheck")
{
    using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        using var response = await healthClient.GetAsync(args[1]);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
var mode = builder.Configuration["DEMO_MODE"] ?? "offline";
var offline = string.Equals(mode, "offline", StringComparison.OrdinalIgnoreCase);
if (offline && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Offline interactive authentication is allowed only in Development.");

builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddAntiforgery();
builder.Services.AddHttpClient("invoice-api", client =>
{
    var baseUrl = builder.Configuration["INVOICE_API_BASE_URL"] ?? "http://invoice-api:8080/";
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IInteractiveInvoiceService, InteractiveInvoiceService>();

if (offline)
{
    builder.Services.AddSingleton<IAuthorizationHeaderProvider, OfflineAuthorizationHeaderProvider>();
    builder.Services.AddAuthentication(OfflineInteractiveAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, OfflineInteractiveAuthenticationHandler>(OfflineInteractiveAuthenticationHandler.SchemeName, _ => { });
}
else
{
    var tenantId = builder.Configuration["TENANT_ID"] ?? throw new InvalidOperationException("TENANT_ID is required in live mode.");
    var blueprintId = builder.Configuration["INTERACTIVE_BLUEPRINT_CLIENT_ID"] ?? throw new InvalidOperationException("INTERACTIVE_BLUEPRINT_CLIENT_ID is required in live mode.");
    var managedIdentityId = builder.Configuration["INTERACTIVE_AZURE_CLIENT_ID"] ?? throw new InvalidOperationException("INTERACTIVE_AZURE_CLIENT_ID is required in live mode.");
    var blueprintAudience = builder.Configuration["INTERACTIVE_BLUEPRINT_AUDIENCE"] ?? $"api://{blueprintId}";

    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
        ["AzureAd:TenantId"] = tenantId,
        ["AzureAd:ClientId"] = blueprintId,
        ["AzureAd:Audience"] = blueprintAudience,
        ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
        ["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = managedIdentityId
    });
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddInMemoryTokenCaches();
    builder.Services.AddAgentIdentities();
    builder.Services.AddScoped<IInvoiceAgentTokenProvider, InvoiceAgentTokenProvider>();
    builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.MapInboundClaims = false;
        options.SaveToken = true;
        options.TokenValidationParameters.ValidAudiences = [blueprintAudience, blueprintId, $"api://{blueprintId}"];
        options.TokenValidationParameters.ValidIssuers = [$"https://login.microsoftonline.com/{tenantId}/v2.0", $"https://sts.windows.net/{tenantId}/"];
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.NameClaimType = "name";
    });
}

builder.Services.AddAuthorizationBuilder().AddPolicy("BlueprintUser", policy =>
    policy.RequireAuthenticatedUser().RequireAssertion(context =>
        offline || (context.User.FindFirstValue("scp") ?? string.Empty).Split(' ').Contains("access_as_user", StringComparer.Ordinal)));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

app.MapPost("/interactive/invoices/preview", async (
    CreateInvoiceRequest request,
    ClaimsPrincipal user,
    IInteractiveInvoiceService invoices,
    CancellationToken ct) =>
{
    try { return (IResult)Results.Ok(await invoices.PreviewAsync(request, user, ct)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.StatusCode((int?)ex.StatusCode ?? 502); }
}).RequireAuthorization("BlueprintUser");

app.MapPost("/interactive/invoices/submit", async (
    CreateInvoiceRequest request,
    ClaimsPrincipal user,
    IInteractiveInvoiceService invoices,
    CancellationToken ct) =>
{
    try { return (IResult)Results.Ok(await invoices.SubmitAsync(request, user, ct)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.StatusCode((int?)ex.StatusCode ?? 502); }
}).RequireAuthorization("BlueprintUser");

app.MapPost("/api/demo/runs/interactive", async (
    CreateInvoiceRequest request,
    ClaimsPrincipal user,
    IInteractiveInvoiceService invoices,
    CancellationToken ct) =>
{
    try { return (IResult)Results.Ok(await invoices.SubmitAsync(request, user, ct)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.StatusCode((int?)ex.StatusCode ?? 502); }
}).RequireAuthorization("BlueprintUser");

app.MapGet("/interactive/invoices/mine", async (
    ClaimsPrincipal user,
    IInteractiveInvoiceService invoices,
    CancellationToken ct) =>
{
    try { return Results.Ok(await invoices.ListMineAsync(user, ct)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    catch (HttpRequestException ex) { return Results.StatusCode((int?)ex.StatusCode ?? 502); }
}).RequireAuthorization("BlueprintUser");

await app.RunAsync();
return 0;

public partial class Program { }
