using System.Security.Claims;
using D1.Invoice.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;

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
var offline = string.Equals(builder.Configuration["DEMO_MODE"], "offline", StringComparison.OrdinalIgnoreCase);
if (offline && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Offline demo sign-in is allowed only in Development.");

builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("invoice-interactive-api", client =>
{
    var baseUrl = builder.Configuration["INVOICE_INTERACTIVE_API_BASE_URL"] ?? "http://invoice-interactive:8080/";
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
});
builder.Services.AddHttpClient("invoice-worker-api", client =>
{
    var baseUrl = builder.Configuration["INVOICE_WORKER_BASE_URL"] ?? "http://invoice-worker:8080/";
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
});
builder.Services.AddScoped<IInvoiceWebApiClient, InvoiceWebApiClient>();

if (offline)
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "D1.Invoice.Web.Dev";
            options.LoginPath = "/dev/sign-in";
            options.LogoutPath = "/dev/sign-out";
        });
}
else
{
    var webClientId = builder.Configuration["WEB_CLIENT_ID"]
        ?? throw new InvalidOperationException("WEB_CLIENT_ID is required in live mode.");
    var webClientSecret = builder.Configuration["WEB_CLIENT_SECRET"];
    if (string.IsNullOrWhiteSpace(webClientSecret))
        throw new InvalidOperationException("WEB_CLIENT_SECRET must be provided through secure runtime configuration in live mode.");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AzureAd:ClientId"] = webClientId,
        ["AzureAd:ClientSecret"] = webClientSecret
    });
    var blueprintScope = builder.Configuration["INTERACTIVE_BLUEPRINT_ACCESS_SCOPE"]
        ?? throw new InvalidOperationException("INTERACTIVE_BLUEPRINT_ACCESS_SCOPE is required in live mode.");
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi([blueprintScope, builder.Configuration["INVOICE_CREATE_DELEGATED_SCOPE"]!])
        .AddInMemoryTokenCaches();
}

builder.Services.AddAuthorizationBuilder().AddPolicy("SignedInUser", policy => policy.RequireAuthenticatedUser());
var app = builder.Build();
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
var publicHost = builder.Configuration["WEB_PUBLIC_HOST"];
if (!string.IsNullOrWhiteSpace(publicHost))
{
    forwardedHeaders.KnownIPNetworks.Clear();
    forwardedHeaders.KnownProxies.Clear();
    forwardedHeaders.AllowedHosts.Add(publicHost);
}
app.UseForwardedHeaders(forwardedHeaders);
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

if (offline && app.Environment.IsDevelopment())
{
    app.MapGet("/dev/sign-in", async (HttpContext context, bool submitter = true) =>
    {
        var userId = submitter ? "offline-user-1001" : "offline-user-1002";
        var claims = new List<Claim>
        {
            new("sub", userId),
            new(ClaimTypes.NameIdentifier, userId),
            new("name", submitter ? "Demo InvoiceSubmitter" : "Demo User Without Submitter"),
            new("preferred_username", submitter ? "submitter@example.test" : "readonly@example.test")
        };
        if (submitter) claims.Add(new Claim("roles", "InvoiceSubmitter"));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", "roles");
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect("/");
    });
    app.MapGet("/dev/sign-out", async (HttpContext context) =>
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/");
    });
}

app.MapGet("/sign-in", (HttpContext context) => offline
    ? Results.Redirect("/dev/sign-in?submitter=true")
    : Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapGet("/sign-out", async (HttpContext context) =>
{
    if (offline)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/");
    }
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
});
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.MapStaticAssets();
app.MapRazorComponents<D1.Invoice.Web.Components.App>().AddInteractiveServerRenderMode();
await app.RunAsync();
return 0;

public partial class Program { }
