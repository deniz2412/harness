using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Audit;

/// <summary>
/// EF-backed <see cref="IRunStore"/>. Uses a fresh context per call (the
/// <see cref="IDbContextFactory{T}"/> pattern the rest of the platform uses), so a long-lived
/// <see cref="Run"/> object owned by the executor can be saved repeatedly as its status changes
/// without dragging a tracking graph around with it.
/// </summary>
public sealed class EfRunStore(IDbContextFactory<HarnessDbContext> dbFactory) : IRunStore
{
    public async Task SaveAsync(Run run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Runs.FirstOrDefaultAsync(r => r.Id == run.Id, ct);
        if (existing is null)
        {
            // First write: HeadSeq/HeadHash default to 0/null — correct, no events emitted yet.
            db.Runs.Add(run);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(run);
            // The chain-head anchor is owned by AuditEmitter, which advances it with each event.
            // The executor's Run object does not track it, so writing it back here would revert the
            // anchor to a stale value and mask a deletion. Exclude both columns: emitter and store
            // own disjoint columns of Runs and must not clobber each other.
            db.Entry(existing).Property(nameof(Run.HeadSeq)).IsModified = false;
            db.Entry(existing).Property(nameof(Run.HeadHash)).IsModified = false;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<Run?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
    }
}
