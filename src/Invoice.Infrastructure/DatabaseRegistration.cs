using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using D1.Invoice.Application;

namespace D1.Invoice.Infrastructure;

public static class DatabaseRegistration
{
    public static IServiceCollection AddInvoiceDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["DATABASE_PROVIDER"] ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("InvoiceDatabase")
            ?? configuration["DATABASE_CONNECTION_STRING"]
            ?? "Data Source=invoice-demo.db";

        services.AddDbContext<InvoiceDbContext>(options =>
        {
            if (string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
                options.UseNpgsql(connectionString);
            else if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                options.UseSqlite(connectionString);
            else
                throw new InvalidOperationException("DATABASE_PROVIDER must be Sqlite or PostgreSQL.");
        });
        services.AddScoped<InvoiceRepository>();
        services.AddScoped<IInvoiceRepository>(sp => sp.GetRequiredService<InvoiceRepository>());
        services.AddScoped<InvoiceAuditRepository>();
        services.AddScoped<IInvoiceAuditRepository>(sp => sp.GetRequiredService<InvoiceAuditRepository>());
        services.AddScoped<DemoRunRepository>();
        services.AddScoped<D1.Invoice.Application.IDemoRunRepository>(sp => sp.GetRequiredService<DemoRunRepository>());
        return services;
    }
}
