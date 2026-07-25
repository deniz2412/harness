using Harness.Audit;
using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Api.Ops;

/// <summary>
/// The read model behind the F4 suite dashboards: aggregates over the runs / approvals / events the
/// platform has recorded. Read-only, per-call <see cref="IDbContextFactory{T}"/>, <c>AsNoTracking</c> —
/// like <see cref="RunQueries"/>, it never writes and never reaches a model or tool. Cost/token totals
/// come straight from the events; they are currently 0 because token/cost instrumentation is a tracked
/// residual (A7/F8) — the dashboard renders that honestly rather than inventing numbers.
/// </summary>
public interface IDashboardQueries
{
    Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VolumePoint>> RunVolumeByDayAsync(int days, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowVolume>> ByWorkflowAsync(CancellationToken ct = default);
}

public sealed record StatusCount(RunStatus Status, int Count);

public sealed record DashboardSummary(
    int TotalRuns, IReadOnlyList<StatusCount> ByStatus, int Finished, double AvgDurationSec,
    int GatesDecided, double AvgGateLatencySec, double MedianGateLatencySec,
    decimal TotalCostUsd, int TotalTokens);

public sealed record VolumePoint(DateOnly Day, int Total, int Completed, int Failed);

public sealed record WorkflowVolume(
    string Workflow, int Total, int Completed, int Failed, double SuccessRate, double AvgDurationSec,
    DateTimeOffset? LastRunAt);

public sealed class DashboardQueries(IDbContextFactory<HarnessDbContext> dbFactory) : IDashboardQueries
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var runs = await db.Runs.AsNoTracking()
            .Select(r => new { r.Status, r.StartedAt, r.FinishedAt }).ToListAsync(ct);

        var byStatus = runs.GroupBy(r => r.Status)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .OrderBy(s => s.Status).ToList();

        var durations = runs.Where(r => r.FinishedAt is not null)
            .Select(r => (r.FinishedAt!.Value - r.StartedAt).TotalSeconds).ToList();

        var latencies = (await db.Approvals.AsNoTracking()
                .Where(a => a.DecidedAt != null)
                .Select(a => new { a.RequestedAt, a.DecidedAt }).ToListAsync(ct))
            .Select(g => (g.DecidedAt!.Value - g.RequestedAt).TotalSeconds)
            .OrderBy(x => x).ToList();

        var cost = await db.Events.AsNoTracking().SumAsync(e => (decimal?)e.CostUsd, ct) ?? 0m;
        var tokens = await db.Events.AsNoTracking().SumAsync(e => (int?)(e.TokensIn + e.TokensOut), ct) ?? 0;

        return new DashboardSummary(
            TotalRuns: runs.Count,
            ByStatus: byStatus,
            Finished: durations.Count,
            AvgDurationSec: durations.Count > 0 ? durations.Average() : 0,
            GatesDecided: latencies.Count,
            AvgGateLatencySec: latencies.Count > 0 ? latencies.Average() : 0,
            MedianGateLatencySec: latencies.Count > 0 ? latencies[latencies.Count / 2] : 0,
            TotalCostUsd: cost,
            TotalTokens: tokens);
    }

    public async Task<IReadOnlyList<VolumePoint>> RunVolumeByDayAsync(int days, CancellationToken ct = default)
    {
        if (days < 1) days = 1;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var since = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-(days - 1)), TimeSpan.Zero);
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.StartedAt >= since)
            .Select(r => new { r.StartedAt, r.Status }).ToListAsync(ct);

        var byDay = runs
            .GroupBy(r => DateOnly.FromDateTime(r.StartedAt.UtcDateTime.Date))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Fill every day in the window, zero where there were no runs, so the chart is continuous.
        var points = new List<VolumePoint>(days);
        for (var i = 0; i < days; i++)
        {
            var day = DateOnly.FromDateTime(since.AddDays(i).UtcDateTime.Date);
            if (byDay.TryGetValue(day, out var list))
                points.Add(new VolumePoint(day, list.Count,
                    list.Count(r => r.Status == RunStatus.Completed),
                    list.Count(r => r.Status == RunStatus.Failed)));
            else
                points.Add(new VolumePoint(day, 0, 0, 0));
        }
        return points;
    }

    public async Task<IReadOnlyList<WorkflowVolume>> ByWorkflowAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var runs = await db.Runs.AsNoTracking()
            .Select(r => new { r.Workflow, r.Status, r.StartedAt, r.FinishedAt }).ToListAsync(ct);

        return runs.GroupBy(r => r.Workflow).Select(g =>
        {
            var completed = g.Count(r => r.Status == RunStatus.Completed);
            var failed = g.Count(r => r.Status == RunStatus.Failed);
            var terminal = g.Count(r => r.Status is RunStatus.Completed or RunStatus.Failed
                or RunStatus.PolicyBlocked or RunStatus.Rejected);
            var durs = g.Where(r => r.FinishedAt is not null)
                .Select(r => (r.FinishedAt!.Value - r.StartedAt).TotalSeconds).ToList();
            return new WorkflowVolume(
                g.Key, g.Count(), completed, failed,
                terminal > 0 ? (double)completed / terminal : 0,
                durs.Count > 0 ? durs.Average() : 0,
                g.Max(r => (DateTimeOffset?)r.StartedAt));
        }).OrderByDescending(w => w.Total).ThenBy(w => w.Workflow, StringComparer.Ordinal).ToList();
    }
}
