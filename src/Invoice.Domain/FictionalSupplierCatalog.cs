namespace D1.Invoice.Domain;

public sealed record FictionalSupplier(string Id, string Name, string Country, IReadOnlyList<string> CostCenters);

public static class FictionalSupplierCatalog
{
    private static readonly FictionalSupplier[] Suppliers =
    [
        new("NWS", "Northwind Office Supplies", "NL", ["FIN-100", "OPS-210"]),
        new("CFS", "Contoso Facilities Services", "DE", ["FAC-120", "OPS-210"]),
        new("FCC", "Fabrikam Cloud Consulting", "IE", ["IT-310", "SEC-320"]),
        new("AWL", "Adventure Works Logistics", "BE", ["LOG-410", "OPS-210"]),
        new("TCS", "Tailspin Coffee Services", "NL", ["FAC-120", "PEO-510"]),
        new("LST", "Litware Security Training", "DK", ["SEC-320", "PEO-510"]),
        new("ASH", "Alpine Ski House Events", "AT", ["MKT-610", "PEO-510"]),
        new("PHS", "Proseware Hardware Services", "SE", ["IT-310", "OPS-210"]),
        new("WWI", "Wide World Importers Catering", "FR", ["FAC-120", "MKT-610"]),
        new("BYT", "Blue Yonder Travel Services", "ES", ["TRV-710", "MKT-610"])
    ];

    public static IReadOnlyList<FictionalSupplier> All => Suppliers;

    public static bool TryGet(string supplierId, out FictionalSupplier supplier)
    {
        supplier = Suppliers.FirstOrDefault(x => string.Equals(x.Id, supplierId, StringComparison.OrdinalIgnoreCase))!;
        return supplier is not null;
    }
}
