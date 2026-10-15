using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace D1.Invoice.Api.Tests;

public sealed class InvoiceApiFactory(string mode = "offline") : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"d1-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(mode == "offline" ? "Development" : "Testing");
        builder.UseSetting("DEMO_MODE", mode);
        builder.UseSetting("DATABASE_PROVIDER", "Sqlite");
        builder.UseSetting("DATABASE_CONNECTION_STRING", $"Data Source={_databasePath}");
        builder.UseSetting("TENANT_ID", "11111111-1111-1111-1111-111111111111");
        builder.UseSetting("INVOICE_API_CLIENT_ID", "22222222-2222-2222-2222-222222222222");
        builder.UseSetting("INVOICE_API_AUDIENCE", "api://22222222-2222-2222-2222-222222222222");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["DEMO_MODE"] = mode,
                ["DATABASE_PROVIDER"] = "Sqlite",
                ["DATABASE_CONNECTION_STRING"] = $"Data Source={_databasePath}",
                ["TENANT_ID"] = "11111111-1111-1111-1111-111111111111",
                ["INVOICE_API_CLIENT_ID"] = "22222222-2222-2222-2222-222222222222",
                ["INVOICE_API_AUDIENCE"] = "api://22222222-2222-2222-2222-222222222222"
            };
            configuration.AddInMemoryCollection(values);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
