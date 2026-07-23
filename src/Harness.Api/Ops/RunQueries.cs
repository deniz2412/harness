using Harness.Audit;
using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Api.Ops;

/// <summary>
/// The read model behind the operations console (F1). Every method is a read over the runs /
/// run_events / approvals the platform has already recorded — this service never writes and never
/// reaches a model or a tool. Contexts are created per call via <see cref="IDbContextFactory{T}"/>
/// and read <c>AsNoTracking</c>: the console is a pure consumer of the audit trail.
/// </summary>
public sealed class RunQueries(
    IDbContextFactory<HarnessDbContext> dbFactory, string payloadRoot, AuditEmitter audit)
    : IRunQueries
{
    // Same directory AuditEmitter writes payloads into (Paths:AuditPayloads). Passed to
    // PayloadPath.Resolve as the root override so a stored ref is re-based on this machine's
    // volume and only its file name is trusted — a doctored ref cannot walk out of the root.
    private readonly string _payloadRoot = payloadRoot;

    public async Task<IReadOnlyList<RunListItem>> ListRunsAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Most-recent first, capped. Only the page we will show is materialised.
        var runs = await db.Runs.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .Select(r => new
            {
                r.Id, r.Workflow, r.Repo, r.Initiator, r.Status, r.StartedAt, r.FinishedAt
            })
            .ToListAsync(ct);

        var ids = runs.Select(r => r.Id).ToList();

        // One grouped query for the whole page — never load every event just to count it.
        var aggregates = await db.Events.AsNoTracking()
            .Where(e => ids.Contains(e.RunId))
            .GroupBy(e => e.RunId)
            .Select(g => new { RunId = g.Key, Count = g.Count(), Cost = g.Sum(x => x.CostUsd) })
            .ToListAsync(ct);
        var byRun = aggregates.ToDictionary(a => a.RunId);

        return runs.Select(r =>
        {
            byRun.TryGetValue(r.Id, out var agg);
            return new RunListItem(
                r.Id, r.Workflow, r.Repo, r.Initiator, r.Status, r.StartedAt, r.FinishedAt,
                agg?.Count ?? 0, agg?.Cost ?? 0m);
        }).ToList();
    }

    public async Task<RunView?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return null;

        var events = await db.Events.AsNoTracking()
            .Where(e => e.RunId == runId).OrderBy(e => e.Seq).ToListAsync(ct);
        var gates = await db.Approvals.AsNoTracking()
            .Where(a => a.RunId == runId).OrderBy(a => a.RequestedAt).ToListAsync(ct);

        // Totals are summed from the events we already loaded for the detail view — no extra query.
        var tokensIn = events.Sum(e => e.TokensIn);
        var tokensOut = events.Sum(e => e.TokensOut);
        var cost = events.Sum(e => e.CostUsd);

        return new RunView(run, events, gates, tokensIn, tokensOut, cost);
    }

    public async Task<string?> GetEventPayloadAsync(Guid runId, long seq, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var ev = await db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.RunId == runId && e.Seq == seq, ct);
        if (ev is null) return null;

        var path = PayloadPath.Resolve(ev.PayloadRef, runId, _payloadRoot);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            // Untrusted content (invariant 4): returned verbatim as data for the UI to display,
            // never executed.
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Delegates to the one chain-verification definition (<see cref="AuditEmitter"/> →
    /// <c>ChainVerifier</c>), so the console's Verify button, the CLI and <c>GET /verify</c> all
    /// return the identical verdict.</summary>
    public Task<ChainVerificationResult> VerifyAsync(Guid runId, CancellationToken ct = default) =>
        audit.VerifyAsync(runId, ct);
}
