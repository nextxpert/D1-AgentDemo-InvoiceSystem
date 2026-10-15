using System.Security.Claims;
using D1.Invoice.Application;
using D1.Invoice.Identity;
using D1.Invoice.Infrastructure;
using D1.Invoice.Worker;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
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
    throw new InvalidOperationException("Offline worker endpoints are permitted only in Development.");

var options = new InvoiceWorkerOptions(
    offline,
    int.TryParse(builder.Configuration["DEMO_RANDOM_SEED"], out var seed) ? seed : 20261014,
    int.TryParse(builder.Configuration["DEMO_BATCH_SIZE"], out var batchSize) ? batchSize : 5,
    decimal.TryParse(builder.Configuration["DEMO_MIN_AMOUNT"], out var minAmount) ? minAmount : 100m,
    decimal.TryParse(builder.Configuration["DEMO_MAX_AMOUNT"], out var maxAmount) ? maxAmount : 15000m,
    int.TryParse(builder.Configuration["DEMO_RECENT_DATE_WINDOW_DAYS"], out var days) ? days : 90,
    builder.Configuration["ENTRY_AGENT_CLIENT_ID"] ?? "offline-entry-agent",
    builder.Configuration["APPROVAL_AGENT_CLIENT_ID"] ?? "offline-approval-agent");

builder.Logging.AddJsonConsole(log => log.IncludeScopes = true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddInvoiceDatabase(builder.Configuration);
builder.Services.AddHttpClient<InvoiceWorkerApiClient>(client =>
{
    var baseUrl = builder.Configuration["INVOICE_API_BASE_URL"] ?? "http://invoice-api:8080/";
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<InvoiceDemoOrchestrator>();

if (offline)
{
    builder.Services.AddSingleton<IWorkerAgentAuthenticator, OfflineWorkerAgentAuthenticator>();
}
else
{
    var tenantId = builder.Configuration["TENANT_ID"] ?? throw new InvalidOperationException("TENANT_ID is required in live mode.");
    var blueprintClientId = builder.Configuration["AUTONOMOUS_BLUEPRINT_CLIENT_ID"] ?? throw new InvalidOperationException("AUTONOMOUS_BLUEPRINT_CLIENT_ID is required in live mode.");
    var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"] ?? throw new InvalidOperationException("AZURE_CLIENT_ID is required in live mode.");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
        ["AzureAd:TenantId"] = tenantId,
        ["AzureAd:ClientId"] = blueprintClientId,
        ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
        ["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = managedIdentityClientId
    });
    builder.Services.Configure<MicrosoftIdentityApplicationOptions>(builder.Configuration.GetSection("AzureAd"));
    builder.Services.AddTokenAcquisition(false);
    builder.Services.AddInMemoryTokenCaches();
    builder.Services.AddAgentIdentities();
    builder.Services.AddScoped<IInvoiceAgentTokenProvider, InvoiceAgentTokenProvider>();
    builder.Services.AddScoped<IWorkerAgentAuthenticator, LiveWorkerAgentAuthenticator>();
}

if (!offline)
{
    var tenantId = builder.Configuration["TENANT_ID"]!;
    var clientId = builder.Configuration["INVOICE_API_CLIENT_ID"] ?? throw new InvalidOperationException("INVOICE_API_CLIENT_ID is required in live mode.");
    var audience = builder.Configuration["INVOICE_API_AUDIENCE"] ?? $"api://{clientId}";
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(jwt =>
        {
            jwt.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidAudiences = [audience, clientId, $"api://{clientId}"],
                ValidIssuers = [$"https://login.microsoftonline.com/{tenantId}/v2.0", $"https://sts.windows.net/{tenantId}/"],
                RoleClaimType = "roles"
            };
        });
}

    builder.Services.AddAuthorizationBuilder().AddPolicy("DemoTrigger", policy =>
    policy.RequireAuthenticatedUser().RequireAssertion(context =>
        offline ||
        (context.User.FindAll("roles").Any(role => role.Value == "InvoiceSubmitter") &&
         (context.User.FindFirstValue("scp") ?? string.Empty).Split(' ').Contains("Invoice.Create.Delegated", StringComparer.Ordinal))));

var app = builder.Build();
if (!offline)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>().Database;
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) await database.EnsureCreatedAsync();
    else await database.MigrateAsync();
}
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/readyz", async (InvoiceDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
var configEndpoint = app.MapGet("/api/demo/config", () => Results.Ok(new
{
    mode = offline ? "offline" : "live",
    entryAgentConfigured = !string.IsNullOrWhiteSpace(options.EntryAgentClientId),
    approvalAgentConfigured = !string.IsNullOrWhiteSpace(options.ApprovalAgentClientId),
    apiConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["INVOICE_API_BASE_URL"])
}));
var runListEndpoint = app.MapGet("/api/demo/runs", async (IDemoRunRepository runs, CancellationToken ct) =>
    Results.Ok(await runs.ListAsync(ct)));
var runLookupEndpoint = app.MapGet("/api/demo/runs/{correlationId}", async (string correlationId, IDemoRunRepository runs, CancellationToken ct) =>
    await runs.FindAsync(correlationId, ct) is { } run ? Results.Ok(run) : Results.NotFound());
var generateEndpoint = app.MapPost("/api/demo/invoices/generate", async (int? count, InvoiceDemoOrchestrator orchestrator, CancellationToken ct) =>
    await orchestrator.GenerateInvoicesAsync(count ?? options.BatchSize, ct));
var approvePendingEndpoint = app.MapPost("/api/demo/invoices/approve-pending", (InvoiceDemoOrchestrator orchestrator, CancellationToken ct) =>
    orchestrator.ApprovePendingAsync(ct));
var autonomousRun = app.MapPost("/api/demo/runs/autonomous", (InvoiceDemoOrchestrator orchestrator, CancellationToken ct) => orchestrator.RunAutonomousAsync(ct));
var negativeRun = app.MapPost("/api/demo/runs/negative-tests", (InvoiceDemoOrchestrator orchestrator, CancellationToken ct) => orchestrator.RunNegativeTestsAsync(ct));
if (!offline)
{
    configEndpoint.RequireAuthorization("DemoTrigger");
    runListEndpoint.RequireAuthorization("DemoTrigger");
    runLookupEndpoint.RequireAuthorization("DemoTrigger");
    generateEndpoint.RequireAuthorization("DemoTrigger");
    approvePendingEndpoint.RequireAuthorization("DemoTrigger");
    autonomousRun.RequireAuthorization("DemoTrigger");
    negativeRun.RequireAuthorization("DemoTrigger");
}
await app.RunAsync();
return 0;

public partial class Program { }
