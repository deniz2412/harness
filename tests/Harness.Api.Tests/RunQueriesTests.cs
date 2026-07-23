using Harness.Api.Ops;
using Harness.Audit;
using Harness.Contracts;
using Xunit;

namespace Harness.Api.Tests;

public sealed class RunQueriesTests : IDisposable
{
    private readonly InMemoryDbContextFactory _dbFactory = new();
    private readonly string _payloadRoot =
        Path.Combine(Path.GetTempPath(), "harness-api-tests", Guid.NewGuid().ToString("N"));

    private RunQueries NewQueries() =>
        new(_dbFactory, _payloadRoot, new AuditEmitter(_dbFactory, _payloadRoot));

    private Run SeedRun(string workflow, string repo, string initiator, RunStatus status,
        DateTimeOffset startedAt)
    {
        var run = new Run
        {
            Workflow = workflow, WorkflowSha = "sha", Initiator = initiator, Repo = repo,
            Status = status, StartedAt = startedAt
        };
        using var db = _dbFactory.CreateDbContext();
        db.Runs.Add(run);
        db.SaveChanges();
        return run;
    }

    private void SeedEvent(Guid runId, long seq, string type, string node,
        int tokensIn = 0, int tokensOut = 0, decimal costUsd = 0m, string? payload = null)
    {
        string? payloadRef = null;
        if (payload is not null)
        {
            var dir = Path.Combine(_payloadRoot, runId.ToString());
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{seq:D6}.json");
            File.WriteAllText(file, payload);
            payloadRef = $"file://{file}";
        }

        using var db = _dbFactory.CreateDbContext();
        db.Events.Add(new RunEvent
        {
            RunId = runId, Seq = seq, Type = type, Node = node, PayloadHash = "h",
            PayloadRef = payloadRef, TokensIn = tokensIn, TokensOut = tokensOut, CostUsd = costUsd
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ListRuns_returns_most_recent_first_with_event_counts_and_cost_sums()
    {
        var t0 = DateTimeOffset.UtcNow;
        var older = SeedRun("pr-review", "acme/one", "alice", RunStatus.Completed, t0.AddMinutes(-10));
        var newer = SeedRun("pr-review", "acme/two", "bob", RunStatus.Running, t0);

        SeedEvent(older.Id, 1, "node_start", "n1", costUsd: 0.01m);
        SeedEvent(older.Id, 2, "node_end", "n1", costUsd: 0.02m);
        SeedEvent(newer.Id, 1, "node_start", "n1", costUsd: 0.05m);

        var list = await NewQueries().ListRunsAsync(10);

        Assert.Equal(2, list.Count);
        // Most recent first.
        Assert.Equal(newer.Id, list[0].Id);
        Assert.Equal(older.Id, list[1].Id);

        Assert.Equal(1, list[0].EventCount);
        Assert.Equal(0.05m, list[0].CostUsd);
        Assert.Equal(2, list[1].EventCount);
        Assert.Equal(0.03m, list[1].CostUsd);
    }

    [Fact]
    public async Task ListRuns_honours_the_limit()
    {
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            SeedRun("pr-review", "acme/x", "alice", RunStatus.Completed, t0.AddMinutes(-i));

        var list = await NewQueries().ListRunsAsync(3);

        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task ListRuns_reports_zero_for_a_run_with_no_events()
    {
        var run = SeedRun("pr-review", "acme/x", "alice", RunStatus.Pending, DateTimeOffset.UtcNow);

        var item = Assert.Single(await NewQueries().ListRunsAsync(10));

        Assert.Equal(run.Id, item.Id);
        Assert.Equal(0, item.EventCount);
        Assert.Equal(0m, item.CostUsd);
    }

    [Fact]
    public async Task GetRun_aggregates_tokens_and_cost_and_orders_events_by_seq()
    {
        var run = SeedRun("pr-review", "acme/x", "alice", RunStatus.Completed, DateTimeOffset.UtcNow);
        // Insert out of seq order to prove the query orders, not the insertion.
        SeedEvent(run.Id, 3, "node_end", "n1", tokensIn: 5, tokensOut: 6, costUsd: 0.03m);
        SeedEvent(run.Id, 1, "node_start", "n1", tokensIn: 1, tokensOut: 2, costUsd: 0.01m);
        SeedEvent(run.Id, 2, "model_call", "n1", tokensIn: 3, tokensOut: 4, costUsd: 0.02m);

        var view = await NewQueries().GetRunAsync(run.Id);

        Assert.NotNull(view);
        Assert.Equal(run.Id, view!.Run.Id);
        Assert.Equal(new long[] { 1, 2, 3 }, view.Events.Select(e => e.Seq).ToArray());
        Assert.Equal(9, view.TokensIn);
        Assert.Equal(12, view.TokensOut);
        Assert.Equal(0.06m, view.CostUsd);
    }

    [Fact]
    public async Task GetRun_returns_gates_ordered_by_requested_at()
    {
        var run = SeedRun("issue-to-pr", "acme/x", "alice", RunStatus.AwaitingApproval,
            DateTimeOffset.UtcNow);
        var t0 = DateTimeOffset.UtcNow;
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Approvals.Add(new GateApproval { RunId = run.Id, Node = "b", RequestedAt = t0.AddMinutes(1) });
            db.Approvals.Add(new GateApproval { RunId = run.Id, Node = "a", RequestedAt = t0 });
            db.SaveChanges();
        }

        var view = await NewQueries().GetRunAsync(run.Id);

        Assert.NotNull(view);
        Assert.Equal(new[] { "a", "b" }, view!.Gates.Select(g => g.Node).ToArray());
    }

    [Fact]
    public async Task GetRun_returns_null_for_an_unknown_run()
    {
        Assert.Null(await NewQueries().GetRunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetEventPayload_returns_the_file_content()
    {
        var run = SeedRun("pr-review", "acme/x", "alice", RunStatus.Completed, DateTimeOffset.UtcNow);
        SeedEvent(run.Id, 1, "node_end", "n1", payload: "the payload body");

        var payload = await NewQueries().GetEventPayloadAsync(run.Id, 1);

        Assert.Equal("the payload body", payload);
    }

    [Fact]
    public async Task GetEventPayload_returns_null_for_an_unknown_event()
    {
        var run = SeedRun("pr-review", "acme/x", "alice", RunStatus.Completed, DateTimeOffset.UtcNow);
        SeedEvent(run.Id, 1, "node_end", "n1", payload: "body");

        Assert.Null(await NewQueries().GetEventPayloadAsync(run.Id, 99));
    }

    [Fact]
    public async Task GetEventPayload_returns_null_when_the_file_is_missing()
    {
        // Event exists but its payload file was never written (or was deleted).
        var run = SeedRun("pr-review", "acme/x", "alice", RunStatus.Completed, DateTimeOffset.UtcNow);
        var missingRef = $"file://{Path.Combine(_payloadRoot, run.Id.ToString(), "000001.json")}";
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Events.Add(new RunEvent
            {
                RunId = run.Id, Seq = 1, Type = "node_end", Node = "n1", PayloadHash = "h",
                PayloadRef = missingRef
            });
            db.SaveChanges();
        }

        Assert.Null(await NewQueries().GetEventPayloadAsync(run.Id, 1));
    }

    public void Dispose()
    {
        try { Directory.Delete(_payloadRoot, recursive: true); } catch { /* best effort */ }
    }
}
