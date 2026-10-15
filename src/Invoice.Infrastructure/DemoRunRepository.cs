using System.Text.Json;
using D1.Invoice.Application;
using D1.Invoice.Contracts;
using Microsoft.EntityFrameworkCore;

namespace D1.Invoice.Infrastructure;

public sealed class DemoRunRepository(InvoiceDbContext dbContext) : IDemoRunRepository
{
    public async Task AddAsync(DemoRunSnapshot snapshot, CancellationToken cancellationToken)
    {
        await dbContext.DemoRuns.AddAsync(new DemoRunEntity
        {
            Id = snapshot.Id,
            CorrelationId = snapshot.CorrelationId,
            Mode = snapshot.Mode,
            Status = snapshot.Status,
            SafeSummaryJson = snapshot.SafeSummaryJson,
            StartedAtUtc = snapshot.StartedAtUtc,
            FinishedAtUtc = snapshot.FinishedAtUtc
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DemoRunSnapshot>> ListAsync(CancellationToken cancellationToken)
        => (await dbContext.DemoRuns.ToListAsync(cancellationToken))
            .OrderByDescending(run => run.StartedAtUtc)
            .Select(run => new DemoRunSnapshot(run.Id, run.CorrelationId, run.Mode, run.Status,
                run.SafeSummaryJson, run.StartedAtUtc, run.FinishedAtUtc))
            .ToArray();

    public async Task<DemoRunSnapshot?> FindAsync(string correlationId, CancellationToken cancellationToken)
        => await dbContext.DemoRuns.Where(run => run.CorrelationId == correlationId)
            .Select(run => new DemoRunSnapshot(run.Id, run.CorrelationId, run.Mode, run.Status,
                run.SafeSummaryJson, run.StartedAtUtc, run.FinishedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
}
