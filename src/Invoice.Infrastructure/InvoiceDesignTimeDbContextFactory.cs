using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace D1.Invoice.Infrastructure;

public sealed class InvoiceDesignTimeDbContextFactory : IDesignTimeDbContextFactory<InvoiceDbContext>
{
    public InvoiceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql("Host=localhost;Database=d1_invoice_design;Username=design;Password=design")
            .Options;
        return new InvoiceDbContext(options);
    }
}
