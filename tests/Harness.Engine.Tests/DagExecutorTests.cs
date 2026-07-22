using Harness.Contracts;
using Xunit;

namespace Harness.Engine.Tests;

public class DagExecutorTests
{
    private static WorkflowDefinition Wf(params NodeDefinition[] nodes) =>
        new() { Name = "t", Nodes = [.. nodes] };

    private static NodeDefinition N(string id, params string[] deps) =>
        new() { Id = id, Kind = "agent", DependsOn = [.. deps] };

    private static NodeDefinition Gated(string id, string gate, params string[] deps) =>
        new() { Id = id, Kind = "agent", Gate = gate, DependsOn = [.. deps] };

    private static Run NewRun() => new()
    {
        Workflow = "t", WorkflowSha = "sha", Initiator = "deniz", Repo = "org/repo", PullRequest = 1
    };

    private sealed record Fixture(
        DagExecutor Exec, FakeAuditLog Audit, FakeApprovalStore Approvals,
        FakeRunStore Store, FakeNodeExecutor Nodes);

    private static Fixture Harness(Func<NodeContext, NodeResult>? behaviour = null)
    {
        var audit = new FakeAuditLog();
        var approvals = new FakeApprovalStore();
        var store = new FakeRunStore();
        var nodes = new FakeNodeExecutor("agent", behaviour);
        return new Fixture(new DagExecutor([nodes], audit, approvals, store), audit, approvals, store, nodes);
    }

    [Fact]
    public void TopologicalOrder_respects_dependencies()
    {
        var order = DagExecutor.TopologicalOrder(Wf(N("post", "review"), N("gather"), N("review", "gather")))
            .Select(n => n.Id).ToList();
        Assert.True(order.IndexOf("gather") < order.IndexOf("review"));
        Assert.True(order.IndexOf("review") < order.IndexOf("post"));
    }

    [Fact]
    public void TopologicalOrder_detects_cycles()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DagExecutor.TopologicalOrder(Wf(N("a", "b"), N("b", "a"))));
    }

    [Fact]
    public async Task Human_gate_without_a_decision_pauses_the_run_and_does_not_execute_the_node()
    {
        var f = Harness();
        var run = NewRun();

        await f.Exec.ExecuteAsync(run, Wf(N("gather"), Gated("post", "human", "gather")), CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Null(run.FinishedAt);                          // paused, not finished
        Assert.Equal(["gather"], f.Nodes.Executed);           // the gated node never ran
        Assert.Contains("gate_request", f.Audit.TypesFor("post"));
        Assert.DoesNotContain("node_start", f.Audit.TypesFor("post"));
        var pending = Assert.Single(f.Approvals.All);
        Assert.Equal("post", pending.Node);
        Assert.Equal(GateDecision.Pending, pending.Decision);
        Assert.Equal([RunStatus.Running, RunStatus.AwaitingApproval], f.Store.Transitions);
    }

    [Fact]
    public async Task Approved_human_gate_proceeds()
    {
        var f = Harness();
        var run = NewRun();
        f.Approvals.With(run.Id, "post", GateDecision.Approved, "deniz");

        await f.Exec.ExecuteAsync(run, Wf(N("gather"), Gated("post", "human", "gather")), CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(["gather", "post"], f.Nodes.Executed);
        Assert.Contains("approved by deniz", f.Audit.PayloadOf("gate_decision", "post"));
        Assert.Equal([RunStatus.Running, RunStatus.Completed], f.Store.Transitions);
    }

    [Fact]
    public async Task Rejected_human_gate_stops_the_run()
    {
        var f = Harness();
        var run = NewRun();
        f.Approvals.With(run.Id, "post", GateDecision.Rejected, "deniz");

        await f.Exec.ExecuteAsync(run, Wf(N("gather"), Gated("post", "human", "gather")), CancellationToken.None);

        Assert.Equal(RunStatus.Rejected, run.Status);
        Assert.NotNull(run.FinishedAt);
        Assert.DoesNotContain("post", f.Nodes.Executed);
        Assert.Contains("rejected by deniz", f.Audit.PayloadOf("gate_decision", "post"));
        Assert.Equal([RunStatus.Running, RunStatus.Rejected], f.Store.Transitions);
    }

    [Fact]
    public async Task Auto_gate_is_not_a_human_gate()
    {
        var f = Harness();

        await f.Exec.ExecuteAsync(NewRun(), Wf(Gated("post", "auto")), CancellationToken.None);

        Assert.Equal(["post"], f.Nodes.Executed);
        Assert.Empty(f.Approvals.All);
        Assert.DoesNotContain("gate_request", f.Audit.TypesFor("post"));
    }

    [Fact]
    public async Task Node_end_payload_carries_the_node_output()
    {
        var f = Harness();

        await f.Exec.ExecuteAsync(NewRun(), Wf(N("review")), CancellationToken.None);

        Assert.Equal("review-output", f.Audit.PayloadOf("node_end", "review"));
    }

    [Fact]
    public async Task Resume_skips_completed_nodes_and_restores_their_outputs()
    {
        var f = Harness();
        var wf = Wf(N("gather"), N("review", "gather"), Gated("post", "human", "review"));
        var run = NewRun();

        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(["gather", "review"], f.Nodes.Executed);

        // A human approves out of band; the same run is handed back to the executor.
        await f.Approvals.DecideAsync(run.Id, "post", GateDecision.Approved, "deniz", "looks fine");
        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(["gather", "review", "post"], f.Nodes.Executed);       // no node ran twice
        Assert.Equal(new Dictionary<string, string>
        {
            ["gather"] = "gather-output", ["review"] = "review-output"      // rebuilt from node_end payloads
        }, f.Nodes.UpstreamSeen["post"]);
        Assert.Equal(
            [RunStatus.Running, RunStatus.AwaitingApproval, RunStatus.Running, RunStatus.Completed],
            f.Store.Transitions);
    }

    [Fact]
    public async Task Resume_re_pauses_when_the_gate_is_still_undecided()
    {
        var f = Harness();
        var wf = Wf(N("gather"), Gated("post", "human", "gather"));
        var run = NewRun();

        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);
        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(["gather"], f.Nodes.Executed);
    }

    [Fact]
    public async Task Terminal_runs_are_never_re_executed()
    {
        var f = Harness();
        var run = NewRun();
        run.Status = RunStatus.Rejected;

        await f.Exec.ExecuteAsync(run, Wf(N("gather")), CancellationToken.None);

        Assert.Empty(f.Nodes.Executed);
        Assert.Empty(f.Audit.Events);
    }

    [Fact]
    public async Task Node_failure_halts_the_run_and_is_persisted()
    {
        var f = Harness(ctx => ctx.Node.Id == "gather"
            ? new NodeResult(false, "boom")
            : new NodeResult(true, "x"));
        var run = NewRun();

        await f.Exec.ExecuteAsync(run, Wf(N("gather"), N("review", "gather")), CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(["gather"], f.Nodes.Executed);
        Assert.Equal("failed: boom", f.Audit.PayloadOf("node_end", "gather"));
        Assert.Equal([RunStatus.Running, RunStatus.Failed], f.Store.Transitions);
    }

    [Fact]
    public async Task Unknown_node_kind_throws_rather_than_being_skipped()
    {
        var f = Harness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Exec.ExecuteAsync(
            NewRun(), Wf(new NodeDefinition { Id = "b", Kind = "bash" }), CancellationToken.None));
    }
}
