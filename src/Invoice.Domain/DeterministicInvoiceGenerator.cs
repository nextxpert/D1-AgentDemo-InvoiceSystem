namespace D1.Invoice.Domain;

public sealed record InvoiceGenerationOptions(
    int Seed = 20261014,
    int BatchSize = 5,
    decimal MinimumAmount = 100m,
    decimal MaximumAmount = 15000m,
    int RecentDateWindowDays = 90);

public sealed class DeterministicInvoiceGenerator
{
    private static readonly string[] Currencies = ["EUR", "USD", "GBP"];
    private static readonly string[] Descriptions = [
        "Office supplies replenishment", "Quarterly facilities service", "Cloud migration consulting",
        "Warehouse delivery service", "Staff training session", "Security awareness workshop",
        "Hardware replacement units", "Conference catering service", "Travel booking service"];
    private static readonly string[] Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Select(c => c.ToString()).ToArray();
    private readonly Random _random;
    private readonly InvoiceGenerationOptions _options;
    private readonly HashSet<string> _invoiceNumbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _purchaseOrders = new(StringComparer.OrdinalIgnoreCase);

    public DeterministicInvoiceGenerator(InvoiceGenerationOptions options)
    {
        if (options.BatchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options.BatchSize));
        if (options.MinimumAmount <= 0 || options.MaximumAmount < options.MinimumAmount) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.RecentDateWindowDays < 1) throw new ArgumentOutOfRangeException(nameof(options.RecentDateWindowDays));
        _options = options;
        _random = new Random(options.Seed);
    }

    public IReadOnlyList<InvoiceDraftData> GenerateBatch(DateOnly today)
    {
        var result = new List<InvoiceDraftData>(_options.BatchSize);
        for (var i = 0; i < _options.BatchSize; i++) result.Add(GenerateOne(today));
        return result;
    }

    public InvoiceDraftData GenerateOne(DateOnly today)
    {
        var supplier = FictionalSupplierCatalog.All[_random.Next(FictionalSupplierCatalog.All.Count)];
        var invoiceNumber = UniqueCode($"INV-{supplier.Id}", _invoiceNumbers, 5);
        var purchaseOrder = UniqueCode("PO", _purchaseOrders, 4, 3);
        var date = today.AddDays(-_random.Next(_options.RecentDateWindowDays));
        var lineCount = _random.Next(1, 6);
        var minimumGross = _options.MinimumAmount + 2m;
        var maximumGross = Math.Max(minimumGross, _options.MaximumAmount - 2m);
        var targetGross = decimal.Round(minimumGross + (decimal)_random.NextDouble() * (maximumGross - minimumGross), 2);
        var remainingSubtotal = decimal.Round(targetGross / 1.2m, 2, MidpointRounding.AwayFromZero);
        var lines = new List<InvoiceLine>(lineCount);

        for (var index = 0; index < lineCount; index++)
        {
            var quantity = _random.Next(1, 8);
            var lineSubtotal = index == lineCount - 1
                ? remainingSubtotal
                : decimal.Round(remainingSubtotal / (lineCount - index), 2, MidpointRounding.AwayFromZero);
            remainingSubtotal -= lineSubtotal;
            var unitPrice = decimal.Round(lineSubtotal / quantity, 4, MidpointRounding.AwayFromZero);
            if (unitPrice <= 0) unitPrice = 1m;
            lines.Add(InvoiceLine.Create(Descriptions[_random.Next(Descriptions.Length)], quantity, unitPrice, .2m, DeterministicGuid()));
        }

        return new InvoiceDraftData(
            DeterministicGuid(), invoiceNumber, purchaseOrder, supplier, date, date.AddDays(_random.Next(14, 61)),
            Currencies[_random.Next(Currencies.Length)], "Fictional demo invoice", supplier.CostCenters[_random.Next(supplier.CostCenters.Count)], lines);
    }

    private Guid DeterministicGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        _random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private string UniqueCode(string prefix, HashSet<string> used, int firstSegmentLength, int secondSegmentLength = 0)
    {
        string candidate;
        do
        {
            var first = RandomText(firstSegmentLength);
            candidate = secondSegmentLength == 0
                ? $"{prefix}-{first}"
                : $"{prefix}-{first}-{RandomText(secondSegmentLength)}";
        } while (!used.Add(candidate));
        return candidate;
    }

    private string RandomText(int length) => string.Concat(Enumerable.Range(0, length).Select(_ => Alphabet[_random.Next(Alphabet.Length)]));
}

public sealed record InvoiceDraftData(
    Guid Id,
    string InvoiceNumber,
    string PurchaseOrderNumber,
    FictionalSupplier Supplier,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string Currency,
    string Description,
    string CostCenter,
    IReadOnlyList<InvoiceLine> Lines);
