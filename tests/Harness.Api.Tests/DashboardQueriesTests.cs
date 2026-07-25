using Harness.Api.Ops;
using Harness.Contracts;
using Xunit;

namespace Harness.Api.Tests;

/// <summary>
/// The F4 dashboard read model, over an in-memory runs/approvals/events store: run counts by status,
/// finished-run durations, decided-gate latency, per-workflow success rates, a continuous daily volume
/// window, and an honest zero spend (token/cost instrumentation is the tracked A7/F8 residual).
/// </summary>
public class DashboardQueriesTests
{
    private static (DashboardQueries q, InMemoryDbContextFactory f) New()
    {
        var f = new InMemoryDbContextFactory();
        return (new DashboardQueries(f), f);
    }

    private static Run Run(string wf, RunStatus status, DateTimeOffset started, DateTimeOffset? finished) =>
        new()
        {
            Workflow = wf, WorkflowSha = "x", Initiator = "t", Repo = "o/r",
            Status = status, StartedAt = started, FinishedAt = finished,
        };

    [Fact]
    public async Task Summary_counts_statuses_durations_gate_latency_and_zero_cost()
    {
        var (q, f) = New();
        var t0 = DateTimeOffset.UtcNow;
        using (var db = f.CreateDbContext())
        {
            db.Runs.AddRange(
                Run("pr-review", RunStatus.Completed, t0, t0.AddSeconds(20)),
                Run("pr-review", RunStatus.Completed, t0, t0.AddSeconds(40)),
                Run("pr-review", RunStatus.Failed, t0, t0.AddSeconds(10)),
                Run("issue-to-pr", RunStatus.Running, t0, null));
            var gated = Run("test-generation", RunStatus.AwaitingApproval, t0, null);
            db.Runs.Add(gated);
            db.Approvals.AddRange(
                new GateApproval { RunId = gated.Id, Node = "approve", RequestedAt = t0, DecidedAt = t0.AddSeconds(30) },
                new GateApproval { RunId = gated.Id, Node = "approve2", RequestedAt = t0, DecidedAt = null });
            db.SaveChanges();
        }

        var s = await q.GetSummaryAsync();

        Assert.Equal(5, s.TotalRuns);
        Assert.Equal(3, s.Finished);                                   // three have FinishedAt
        Assert.Equal((20 + 40 + 10) / 3.0, s.AvgDurationSec, 3);
        Assert.Equal(1, s.GatesDecided);                               // only the decided gate
        Assert.Equal(30, s.AvgGateLatencySec, 3);
        Assert.Equal(0m, s.TotalCostUsd);                              // honest zero
        Assert.Contains(s.ByStatus, x => x.Status == RunStatus.Completed && x.Count == 2);
        Assert.Contains(s.ByStatus, x => x.Status == RunStatus.Failed && x.Count == 1);
    }

    [Fact]
    public async Task ByWorkflow_groups_and_computes_success_rate()
    {
        var (q, f) = New();
        var t0 = DateTimeOffset.UtcNow;
        using (var db = f.CreateDbContext())
        {
            db.Runs.AddRange(
                Run("pr-review", RunStatus.Completed, t0, t0.AddSeconds(10)),
                Run("pr-review", RunStatus.Completed, t0, t0.AddSeconds(10)),
                Run("pr-review", RunStatus.Failed, t0, t0.AddSeconds(10)));
            db.SaveChanges();
        }

        var pr = Assert.Single(await q.ByWorkflowAsync());
        Assert.Equal("pr-review", pr.Workflow);
        Assert.Equal(3, pr.Total);
        Assert.Equal(2.0 / 3.0, pr.SuccessRate, 3);                    // 2 completed of 3 terminal
    }

    [Fact]
    public async Task RunVolumeByDay_fills_zero_days_and_counts_today()
    {
        var (q, f) = New();
        var now = DateTimeOffset.UtcNow;
        using (var db = f.CreateDbContext())
        {
            db.Runs.AddRange(
                Run("pr-review", RunStatus.Completed, now, now.AddSeconds(5)),
                Run("pr-review", RunStatus.Failed, now, now.AddSeconds(5)));
            db.SaveChanges();
        }

        var pts = await q.RunVolumeByDayAsync(7);

        Assert.Equal(7, pts.Count);                                    // continuous window
        Assert.Equal(2, pts[^1].Total);                               // today
        Assert.Equal(1, pts[^1].Completed);
        Assert.Equal(1, pts[^1].Failed);
        Assert.All(pts.Take(6), p => Assert.Equal(0, p.Total));       // earlier days empty
    }
}
