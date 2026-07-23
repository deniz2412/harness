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
        FakeRunStore Store, FakeNodeExecutor Nodes, FakeRunnerFactory Runners);

    private static Fixture Harness(
        Func<NodeContext, NodeResult>? behaviour = null,
        Func<FakeAuditLog, IEnumerable<INodeExecutor>>? extra = null,
        FakeRunnerFactory? runnerFactory = null)
    {
        var audit = new FakeAuditLog();
        var approvals = new FakeApprovalStore();
        var store = new FakeRunStore();
        var nodes = new FakeNodeExecutor("agent", behaviour);
        var runners = runnerFactory ?? new FakeRunnerFactory();
        var executors = new List<INodeExecutor> { nodes };
        if (extra is not null) executors.AddRange(extra(audit));
        return new Fixture(
            new DagExecutor(executors, audit, approvals, store, runners),
            audit, approvals, store, nodes, runners);
    }

    /// <summary>A write-capable workflow: its ceiling declares repo: write-worktree, which is the
    /// signal the executor uses to create a runner sandbox for the run.</summary>
    private static WorkflowDefinition WriteWf(params NodeDefinition[] nodes) =>
        new() { Name = "w", Permissions = new() { ["repo"] = "write-worktree" }, Nodes = [.. nodes] };

    private static NodeDefinition Bash(string id, string? run, params string[] deps) =>
        new() { Id = id, Kind = "bash", Run = run, DependsOn = [.. deps] };

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

    // --- runner session lifecycle -------------------------------------------------------------

    [Fact]
    public async Task Read_only_workflow_never_creates_a_runner_session()
    {
        var f = Harness();

        // pr-review-shaped: no repo: write-worktree in the ceiling.
        await f.Exec.ExecuteAsync(NewRun(), Wf(N("gather"), N("review", "gather")), CancellationToken.None);

        Assert.Empty(f.Runners.Created);                       // the factory was never called
        Assert.Null(f.Nodes.RunnerSeen["gather"]);             // nodes saw a null sandbox
        Assert.Null(f.Nodes.RunnerSeen["review"]);
    }

    [Fact]
    public async Task Write_capable_workflow_creates_one_session_at_HEAD_and_disposes_it()
    {
        var f = Harness();

        await f.Exec.ExecuteAsync(NewRun(), WriteWf(N("plan")), CancellationToken.None);

        var created = Assert.Single(f.Runners.Created);
        Assert.Equal("HEAD", created.BaseRef);                 // sentinel: runner resolves default branch
        Assert.NotNull(f.Nodes.RunnerSeen["plan"]);            // the node received the sandbox
        Assert.True(Assert.Single(f.Runners.Sessions).Disposed);
    }

    [Fact]
    public async Task Paused_write_run_keeps_its_worktree_and_resume_reuses_it_then_disposes()
    {
        var f = Harness();
        var wf = WriteWf(N("plan"), Gated("approve", "human", "plan"));
        var run = NewRun();

        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        // The worktree must survive the pause: the work done before the gate has to be there to push
        // after approval. Disposing here (deleting the worktree) is exactly the bug the live run hit.
        Assert.False(f.Runners.Sessions[0].Disposed);

        await f.Approvals.DecideAsync(run.Id, "approve", GateDecision.Approved, "deniz", null);
        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        // Resume asks the factory for the run's worktree again (the real factory reuses the same
        // directory, keyed by run id — proven in the runner tests); on completion it is disposed.
        Assert.Equal(2, f.Runners.Created.Count);
        Assert.True(f.Runners.Sessions[^1].Disposed);          // torn down only now, at the end
    }

    [Fact]
    public async Task Rejected_write_run_disposes_its_worktree()
    {
        var f = Harness();
        var wf = WriteWf(N("plan"), Gated("approve", "human", "plan"));
        var run = NewRun();

        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);   // pauses
        await f.Approvals.DecideAsync(run.Id, "approve", GateDecision.Rejected, "deniz", "no");
        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);   // resumes → rejected

        Assert.Equal(RunStatus.Rejected, run.Status);
        Assert.True(f.Runners.Sessions[^1].Disposed);          // a terminated run cleans up its sandbox
    }

    // --- bash node ----------------------------------------------------------------------------

    [Fact]
    public async Task Bash_node_succeeds_on_exit_zero_and_records_the_command_and_output()
    {
        var runners = new FakeRunnerFactory(cmd => new CommandResult(0, "42 passed", ""));
        var f = Harness(extra: audit => [new BashNodeExecutor(audit)], runnerFactory: runners);

        var run = NewRun();
        await f.Exec.ExecuteAsync(run, WriteWf(Bash("validate", "dotnet test")), CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("dotnet test", Assert.Single(runners.Sessions[0].Commands));
        Assert.Contains("bash: dotnet test", f.Audit.PayloadOf("tool_call", "validate"));
        Assert.Contains("42 passed", f.Audit.PayloadOf("tool_result", "validate"));
    }

    [Fact]
    public async Task Bash_node_non_zero_exit_fails_the_run_and_surfaces_the_output()
    {
        var runners = new FakeRunnerFactory(cmd => new CommandResult(1, "", "1 failed"));
        var f = Harness(extra: audit => [new BashNodeExecutor(audit)], runnerFactory: runners);

        var run = NewRun();
        await f.Exec.ExecuteAsync(run, WriteWf(Bash("validate", "dotnet test")), CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("exit=1", f.Audit.PayloadOf("node_end", "validate"));   // failure reason is visible
        Assert.Contains("1 failed", f.Audit.PayloadOf("node_end", "validate"));
    }

    [Fact]
    public async Task Bash_node_without_a_runner_session_fails_closed()
    {
        // A bash node placed in a read-only workflow: the executor is registered but no sandbox is
        // created, so the node has nowhere isolated to run and must fail rather than shell out.
        var f = Harness(extra: audit => [new BashNodeExecutor(audit)]);

        var run = NewRun();
        await f.Exec.ExecuteAsync(run, Wf(Bash("validate", "dotnet test")), CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Empty(f.Runners.Created);
        Assert.Contains("needs a runner session", f.Audit.PayloadOf("node_end", "validate"));
    }

    [Fact]
    public async Task Bash_node_with_no_command_fails_closed()
    {
        var f = Harness(extra: audit => [new BashNodeExecutor(audit)]);

        var run = NewRun();
        await f.Exec.ExecuteAsync(run, WriteWf(Bash("validate", run: null)), CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("no 'run' command", f.Audit.PayloadOf("node_end", "validate"));
    }

    // --- gate node ----------------------------------------------------------------------------

    [Fact]
    public async Task Gate_node_pauses_undecided_then_passes_through_once_approved()
    {
        var f = Harness(extra: _ => [new GateNodeExecutor()]);
        var wf = WriteWf(N("plan"), GateNode("approve", "plan"));
        var run = NewRun();

        // Undecided: the DagExecutor mechanic pauses before the gate node ever executes.
        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Contains("gate_request", f.Audit.TypesFor("approve"));
        Assert.DoesNotContain("node_start", f.Audit.TypesFor("approve"));

        // Approved out of band: on resume the gate node runs as a clean pass-through.
        await f.Approvals.DecideAsync(run.Id, "approve", GateDecision.Approved, "deniz", null);
        await f.Exec.ExecuteAsync(run, wf, CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("gate cleared", f.Audit.PayloadOf("node_end", "approve"));
    }

    private static NodeDefinition GateNode(string id, params string[] deps) =>
        new() { Id = id, Kind = "gate", Gate = "human", DependsOn = [.. deps] };

    [Fact]
    public async Task Unknown_node_kind_throws_rather_than_being_skipped()
    {
        var f = Harness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Exec.ExecuteAsync(
            NewRun(), Wf(new NodeDefinition { Id = "b", Kind = "bash" }), CancellationToken.None));
    }
}
