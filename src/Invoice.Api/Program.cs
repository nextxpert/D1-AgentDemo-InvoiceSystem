using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Serialization;
using D1.Invoice.Api;
using D1.Invoice.Application;
using D1.Invoice.Contracts;
using D1.Invoice.Domain;
using D1.Invoice.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using InvoiceAggregate = D1.Invoice.Domain.Invoice;

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
var demoMode = builder.Configuration["DEMO_MODE"] ?? "offline";
var tenantId = builder.Configuration["TENANT_ID"];
var apiClientId = builder.Configuration["INVOICE_API_CLIENT_ID"];
var audience = builder.Configuration["INVOICE_API_AUDIENCE"] ?? (string.IsNullOrWhiteSpace(apiClientId) ? null : $"api://{apiClientId}");
var offlineMode = string.Equals(demoMode, "offline", StringComparison.OrdinalIgnoreCase);
if (offlineMode && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Offline demo authentication is permitted only in Development.");

builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddInvoiceDatabase(builder.Configuration);
builder.Services.AddScoped<IActorContext, HttpInvoiceActorContext>();
builder.Services.AddScoped<IInvoiceCreationService>(services => new InvoiceCreationService(
    services.GetRequiredService<IInvoiceRepository>(),
    services.GetRequiredService<IActorContext>(),
    services.GetRequiredService<TimeProvider>(),
    decimal.TryParse(builder.Configuration["DEMO_MAX_AMOUNT"], out var maxAmount) ? maxAmount : 15000m));
builder.Services.AddSingleton(new ApprovalPolicy(new ApprovalPolicyOptions(
    decimal.TryParse(builder.Configuration["AUTONOMOUS_APPROVAL_LIMIT"], out var limit) ? limit : 10000m)));
builder.Services.AddScoped<IInvoiceApprovalService, InvoiceApprovalService>();

if (offlineMode)
{
    builder.Services.AddAuthentication(OfflineDemoAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, OfflineDemoAuthenticationHandler>(OfflineDemoAuthenticationHandler.SchemeName, _ => { });
}
else
{
    if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(apiClientId) || string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("TENANT_ID, INVOICE_API_CLIENT_ID, and INVOICE_API_AUDIENCE are required in live mode.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidAudiences = new[] { audience, apiClientId, $"api://{apiClientId}" },
                ValidIssuers = new[]
                {
                    $"https://login.microsoftonline.com/{tenantId}/v2.0",
                    $"https://sts.windows.net/{tenantId}/"
                },
                RoleClaimType = "roles",
                NameClaimType = "name"
            };
        });
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("InvoiceCreate", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        HasRole(context.User, "Invoice.Create.All") ||
        (HttpInvoiceActorContext.HasScope(context.User, "Invoice.Create.Delegated") &&
         HasRole(context.User, "InvoiceSubmitter") &&
         HttpInvoiceActorContext.GetAgentActor(context.User) is not null)))
    .AddPolicy("InvoiceRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        HasRole(context.User, "Invoice.Read.All") || HasRole(context.User, "Invoice.Create.All") ||
        (HttpInvoiceActorContext.HasScope(context.User, "Invoice.Create.Delegated") && HasRole(context.User, "InvoiceSubmitter"))))
    .AddPolicy("InvoiceApprove", policy => policy.RequireAuthenticatedUser().RequireRole("Invoice.Approve.All"));

var app = builder.Build();
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["x-correlation-id"].FirstOrDefault();
    var correlationId = !string.IsNullOrWhiteSpace(supplied) && supplied.Length <= 128
        ? supplied
        : ActivityTraceId.CreateRandom().ToString();
    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["x-correlation-id"] = correlationId;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
    await next();
});
app.UseAuthentication();
app.Use(async (context, next) =>
{
    await next();
    if (context.Response.StatusCode != StatusCodes.Status403Forbidden ||
        context.Request.Method != HttpMethods.Post ||
        (context.Request.Path != "/v1/invoices/preview" && context.Request.Path != "/v1/invoices"))
        return;

    var user = context.User;
    var hasAgentIdentityFacet = user.FindAll("xms_act_fct")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Contains("11", StringComparer.Ordinal);
    app.Logger.LogWarning(
        "Invoice create authorization denied HasDelegatedScope={HasDelegatedScope} HasSubmitterRole={HasSubmitterRole} HasAgentActor={HasAgentActor} HasAgentIdentityFacet={HasAgentIdentityFacet} HasAuthorizedParty={HasAuthorizedParty}",
        HttpInvoiceActorContext.HasScope(user, "Invoice.Create.Delegated"),
        HasRole(user, "InvoiceSubmitter"),
        HttpInvoiceActorContext.GetAgentActor(user) is not null,
        hasAgentIdentityFacet,
        !string.IsNullOrWhiteSpace(user.FindFirstValue("azp") ?? user.FindFirstValue("appid")));
});
app.UseAuthorization();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<InvoiceDbContext>().Database.EnsureCreatedAsync();
}
else
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<InvoiceDbContext>().Database.MigrateAsync();
}

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/readyz", async (InvoiceDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/v1/suppliers", () => Results.Ok(FictionalSupplierCatalog.All));

app.MapPost("/v1/invoices/preview", async (
    CreateInvoiceRequest request,
    IInvoiceCreationService creation,
    HttpContext http,
    CancellationToken ct) =>
{
    if (request.Lines is null || request.Lines.Count is < 1 or > 5)
        return (IResult)Results.BadRequest(new { error = "Invoices require one to five lines." });
    try
    {
        var preview = await creation.PreviewAsync(InvoiceApiMapping.ToDraft(request), Correlation(http), ct);
        return Results.Ok(InvoiceApiMapping.ToResponse(preview));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireAuthorization("InvoiceCreate");

app.MapPost("/v1/invoices", async (
    CreateInvoiceRequest request,
    IInvoiceCreationService creation,
    IInvoiceAuditRepository audit,
    IActorContext actor,
    HttpContext http,
    CancellationToken ct) =>
{
    if (request.Lines is null || request.Lines.Count is < 1 or > 5)
        return (IResult)Results.BadRequest(new { error = "Invoices require one to five lines." });
    try
    {
        var correlationId = Correlation(http);
        var invoice = await creation.CreateDraftAsync(InvoiceApiMapping.ToDraft(request), correlationId, ct);
        await audit.AddAuditAsync(Audit(invoice, actor.Current, "invoice.create", RequiredPermission(actor.Current), "Succeeded", correlationId), ct);
        return Results.Created($"/v1/invoices/{invoice.Id}", InvoiceApiMapping.ToResponse(invoice));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Microsoft.EntityFrameworkCore.DbUpdateException) { return Results.Conflict(new { error = "Invoice or purchase-order number is already in use." }); }
}).RequireAuthorization("InvoiceCreate");

app.MapPost("/v1/invoices/{id:guid}/submit", async (
    Guid id,
    IInvoiceRepository invoices,
    IInvoiceAuditRepository audit,
    IActorContext actor,
    HttpContext http,
    CancellationToken ct) =>
{
    var invoice = await invoices.GetByIdAsync(id, ct);
    if (invoice is null) return Results.NotFound();
    if (!CanActOnInvoice(actor.Current, invoice)) return Results.Forbid();
    try
    {
        invoice.Submit(DateTimeOffset.UtcNow);
        await invoices.SaveChangesAsync(ct);
        var correlationId = Correlation(http);
        await audit.AddAuditAsync(Audit(invoice, actor.Current, "invoice.submit", RequiredPermission(actor.Current), "Succeeded", correlationId), ct);
        return Results.Ok(InvoiceApiMapping.ToResponse(invoice));
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireAuthorization("InvoiceCreate");

app.MapGet("/v1/invoices", async (
    string? status,
    IInvoiceRepository invoices,
    IActorContext actor,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(status) && !Enum.TryParse<InvoiceStatus>(status, true, out _))
        return Results.BadRequest(new { error = "Unknown invoice status." });
    var all = string.Equals(status, nameof(InvoiceStatus.PendingApproval), StringComparison.OrdinalIgnoreCase)
        ? await invoices.ListPendingApprovalAsync(ct)
        : await invoices.ListAsync(ct);
    if (!string.IsNullOrWhiteSpace(status))
        all = all.Where(invoice => string.Equals(invoice.Status.ToString(), status, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (!HasRole(user, "Invoice.Read.All"))
        all = all.Where(invoice => CanActOnInvoice(actor.Current, invoice)).ToArray();
    return Results.Ok(all.Select(InvoiceApiMapping.ToResponse));
}).RequireAuthorization("InvoiceRead");

app.MapGet("/v1/invoices/{id:guid}", async (Guid id, IInvoiceRepository invoices, IActorContext actor, ClaimsPrincipal user, CancellationToken ct) =>
{
    var invoice = await invoices.GetByIdAsync(id, ct);
    if (invoice is null) return Results.NotFound();
    if (!HasRole(user, "Invoice.Read.All") && !CanActOnInvoice(actor.Current, invoice)) return Results.Forbid();
    return Results.Ok(InvoiceApiMapping.ToResponse(invoice));
}).RequireAuthorization("InvoiceRead");

app.MapPost("/v1/invoices/{id:guid}/approve", async (
    Guid id,
    IInvoiceApprovalService approval,
    HttpContext http,
    CancellationToken ct) =>
{
    try { return (IResult)Results.Ok(await approval.EvaluateAsync(id, Correlation(http), ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireAuthorization("InvoiceApprove");

app.MapPost("/v1/invoices/{id:guid}/reject", async (
    Guid id,
    RejectInvoiceRequest request,
    IInvoiceRepository invoices,
    IInvoiceAuditRepository audit,
    IActorContext actor,
    HttpContext http,
    CancellationToken ct) =>
{
    var invoice = await invoices.GetByIdAsync(id, ct);
    if (invoice is null) return Results.NotFound();
    try
    {
        var now = DateTimeOffset.UtcNow;
        invoice.Reject(actor.Current, request.Reason, now);
        await invoices.SaveChangesAsync(ct);
        var correlationId = Correlation(http);
        var decision = new ApprovalDecision(Guid.NewGuid(), id, actor.Current.IdentityId, actor.Current.IdentityName,
            DecisionDisposition.Rejected, request.Reason, now, correlationId);
        await audit.AddDecisionAsync(decision, ct);
        await audit.AddAuditAsync(Audit(invoice, actor.Current, "invoice.reject", "Invoice.Approve.All", "Succeeded", correlationId, request.Reason), ct);
        return Results.Ok(decision);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireAuthorization("InvoiceApprove");

app.MapGet("/v1/audit", async (Guid? invoiceId, string? correlationId, IInvoiceAuditRepository audit, IActorContext actor, ClaimsPrincipal user, CancellationToken ct) =>
{
    var events = await audit.FindAuditAsync(invoiceId, correlationId, ct);
    if (!HasRole(user, "Invoice.Read.All"))
        events = events.Where(item => item.ActorIdentityId == actor.Current.IdentityId ||
            (actor.Current.UserId is not null && item.OnBehalfOfUserId == actor.Current.UserId)).ToArray();
    return Results.Ok(events);
})
    .RequireAuthorization("InvoiceRead");

app.MapGet("/api/demo/config", () => Results.Ok(new { mode = demoMode, offline = offlineMode }));

app.MapGet("/api/demo/runs", async (IDemoRunRepository runs, CancellationToken ct) =>
    Results.Ok(await runs.ListAsync(ct))).RequireAuthorization("InvoiceRead");

app.MapGet("/api/demo/runs/{correlationId}", async (string correlationId, IDemoRunRepository runs, CancellationToken ct) =>
    await runs.FindAsync(correlationId, ct) is { } run
        ? Results.Ok(run)
        : Results.NotFound(new { error = "Demo run not found." }))
    .RequireAuthorization("InvoiceRead");

await app.RunAsync();
return 0;

static bool HasRole(ClaimsPrincipal user, string role)
    => user.FindAll("roles").Any(claim => string.Equals(claim.Value, role, StringComparison.Ordinal));

static bool CanActOnInvoice(ActorContext actor, InvoiceAggregate invoice)
    => string.Equals(actor.IdentityId, invoice.CreatedByIdentityId, StringComparison.OrdinalIgnoreCase)
       || (actor.UserId is not null && string.Equals(actor.UserId, invoice.CreatedOnBehalfOfUserId, StringComparison.OrdinalIgnoreCase));

static string RequiredPermission(ActorContext actor)
    => actor.IdentityType == ActorType.InteractiveAgent ? "Invoice.Create.Delegated" : "Invoice.Create.All";

static string Correlation(HttpContext context) => context.Items["CorrelationId"]?.ToString() ?? "unknown";

static InvoiceAuditEvent Audit(InvoiceAggregate invoice, ActorContext actor, string operation, string permission, string result, string correlationId, string? reason = null)
    => new(Guid.NewGuid(), invoice.Id, actor.IdentityId, actor.IdentityName, actor.IdentityType,
        actor.UserId, actor.UserName, operation, permission, result, reason, correlationId, DateTimeOffset.UtcNow);

public sealed record RejectInvoiceRequest(string Reason);

public partial class Program;
