using Harness.Api.Ops;
using Harness.Audit;
using Harness.Contracts;
using Harness.Engine;
using Harness.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harness.Api.Tests;

public sealed class RunCoordinatorTests : IDisposable
{
    private readonly TempWorkflow _wf = new();
    private readonly InMemoryDbContextFactory _dbFactory = new();
    private readonly string _payloadRoot =
        Path.Combine(Path.GetTempPath(), "harness-api-tests", Guid.NewGuid().ToString("N"));

    private const string AllowedRepo = "acme/repo";

    private RunCoordinator NewCoordinator(
        FakeRunStore runs, FakeApprovalStore approvals, FakeWorkflowRunner runner,
        WorkflowLoader? loader = null, RepoAllowlist? allowlist = null)
    {
        var audit = new AuditEmitter(_dbFactory, _payloadRoot);
        return new RunCoordinator(
            loader ?? _wf.Loader,
            runner,
            runs,
            audit,
            new PolicyPipeline(),
            allowlist ?? new RepoAllowlist(new[] { AllowedRepo }),
            approvals,
            NullLoggerFactory.Instance);
    }

    private Run AwaitingRun(string repo = AllowedRepo, string? sha = null) => new()
    {
        Workflow = _wf.Name,
        WorkflowSha = sha ?? _wf.Sha,
        Initiator = "alice",
        Repo = repo,
        Status = RunStatus.AwaitingApproval
    };

    // ---- StartAsync ----

    [Fact]
    public async Task StartAsync_refuses_a_non_allowlisted_repo()
    {
        var coordinator = NewCoordinator(new FakeRunStore(), new FakeApprovalStore(),
            new FakeWorkflowRunner());

        var outcome = await coordinator.StartAsync(_wf.Name, "evil/repo", null, null, "alice");

        Assert.Equal(StartStatus.RepoNotAllowlisted, outcome.Status);
        Assert.Null(outcome.Run);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task StartAsync_refuses_a_bad_workflow()
    {
        var coordinator = NewCoordinator(new FakeRunStore(), new FakeApprovalStore(),
            new FakeWorkflowRunner());

        var outcome = await coordinator.StartAsync("no-such-workflow", AllowedRepo, null, null, "alice");

        Assert.Equal(StartStatus.BadWorkflow, outcome.Status);
        Assert.Null(outcome.Run);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task StartAsync_creates_persists_and_runs_a_valid_request()
    {
        var runs = new FakeRunStore();
        var runner = new FakeWorkflowRunner();
        var coordinator = NewCoordinator(runs, new FakeApprovalStore(), runner);

        var outcome = await coordinator.StartAsync(_wf.Name, AllowedRepo, 7, null, null);

        Assert.Equal(StartStatus.Started, outcome.Status);
        Assert.NotNull(outcome.Run);
        Assert.Equal(_wf.Sha, outcome.Run!.WorkflowSha);   // pinned to the loaded definition
        Assert.Equal("local", outcome.Run.Initiator);      // null initiator defaults to "local"
        Assert.Equal(7, outcome.Run.PullRequest);
        Assert.True(runs.Contains(outcome.Run.Id));

        await runner.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- DecideAsync guards ----

    [Fact]
    public async Task DecideAsync_returns_RunNotFound_for_an_unknown_run()
    {
        var coordinator = NewCoordinator(new FakeRunStore(), new FakeApprovalStore(),
            new FakeWorkflowRunner());

        var outcome = await coordinator.DecideAsync(Guid.NewGuid(), "gate", true, "alice", null);

        Assert.Equal(GateOutcome.RunNotFound, outcome);
    }

    [Fact]
    public async Task DecideAsync_returns_NotAwaitingApproval_when_the_run_is_not_paused()
    {
        var runs = new FakeRunStore();
        var run = AwaitingRun();
        run.Status = RunStatus.Running;
        await runs.SaveAsync(run);
        var coordinator = NewCoordinator(runs, new FakeApprovalStore(), new FakeWorkflowRunner());

        var outcome = await coordinator.DecideAsync(run.Id, "gate", true, "alice", null);

        Assert.Equal(GateOutcome.NotAwaitingApproval, outcome);
    }

    [Fact]
    public async Task DecideAsync_returns_RepoNoLongerAllowlisted_when_the_repo_was_removed()
    {
        var runs = new FakeRunStore();
        var run = AwaitingRun(repo: "removed/repo");
        await runs.SaveAsync(run);
        // Allowlist no longer contains the run's repo.
        var coordinator = NewCoordinator(runs, new FakeApprovalStore(), new FakeWorkflowRunner());

        var outcome = await coordinator.DecideAsync(run.Id, "gate", true, "alice", null);

        Assert.Equal(GateOutcome.RepoNoLongerAllowlisted, outcome);
    }

    [Fact]
    public async Task DecideAsync_returns_DefinitionChanged_when_the_sha_no_longer_matches()
    {
        var runs = new FakeRunStore();
        var run = AwaitingRun(sha: "a-stale-sha-that-does-not-match");
        await runs.SaveAsync(run);
        var coordinator = NewCoordinator(runs, new FakeApprovalStore(), new FakeWorkflowRunner());

        var outcome = await coordinator.DecideAsync(run.Id, "gate", true, "alice", null);

        Assert.Equal(GateOutcome.DefinitionChanged, outcome);
    }

    [Fact]
    public async Task DecideAsync_returns_GateNotFound_when_no_gate_was_requested()
    {
        var runs = new FakeRunStore();
        var run = AwaitingRun();
        await runs.SaveAsync(run);
        var approvals = new FakeApprovalStore { ThrowNotFound = true };
        var coordinator = NewCoordinator(runs, approvals, new FakeWorkflowRunner());

        var outcome = await coordinator.DecideAsync(run.Id, "gate", true, "alice", null);

        Assert.Equal(GateOutcome.GateNotFound, outcome);
    }

    [Fact]
    public async Task DecideAsync_returns_AlreadyDecided_when_the_gate_was_already_decided()
    {
        var runs = new FakeRunStore();
        var run = AwaitingRun();
        await runs.SaveAsync(run);
        var approvals = new FakeApprovalStore { ThrowAlreadyDecided = true };
        var coordinator = NewCoordinator(runs, approvals, new FakeWorkflowRunner());

        var outcome = await coordinator.DecideAsync(run.Id, "gate", true, "alice", null);

        Assert.Equal(GateOutcome.AlreadyDecided, outcome);
    }

    [Fact]
    public async Task DecideAsync_records_the_decision_before_resuming_the_run()
    {
        var runs = new FakeRunStore();
        var run = AwaitingRun();
        await runs.SaveAsync(run);
        var approvals = new FakeApprovalStore();

        // Capture what the approval store had recorded at the instant the runner was invoked.
        GateDecision? decisionSeenAtResume = null;
        var runner = new FakeWorkflowRunner((_, _) =>
        {
            decisionSeenAtResume = approvals.LastDecision;
            return Task.CompletedTask;
        });

        var coordinator = NewCoordinator(runs, approvals, runner);

        var outcome = await coordinator.DecideAsync(run.Id, "gate", approve: true, "carol", "looks good");

        Assert.Equal(GateOutcome.Accepted, outcome);
        await runner.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The decision was recorded (with approver + reason) strictly before the run resumed.
        Assert.Equal(GateDecision.Approved, decisionSeenAtResume);
        Assert.Equal("carol", approvals.LastApprover);
        Assert.Equal("looks good", approvals.LastReason);
    }

    [Fact]
    public async Task DecideAsync_defaults_a_blank_approver_to_the_run_initiator()
    {
        var runs = new FakeRunStore();
        var run = AwaitingRun();   // Initiator = "alice"
        await runs.SaveAsync(run);
        var approvals = new FakeApprovalStore();
        var runner = new FakeWorkflowRunner();
        var coordinator = NewCoordinator(runs, approvals, runner);

        var outcome = await coordinator.DecideAsync(run.Id, "gate", approve: false, "  ", null);

        Assert.Equal(GateOutcome.Accepted, outcome);
        await runner.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GateDecision.Rejected, approvals.LastDecision);
        Assert.Equal("alice", approvals.LastApprover);
    }

    public void Dispose()
    {
        _wf.Dispose();
        try { Directory.Delete(_payloadRoot, recursive: true); } catch { /* best effort */ }
    }
}
