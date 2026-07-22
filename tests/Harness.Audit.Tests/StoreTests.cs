using Harness.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harness.Audit.Tests;

public sealed class RunStoreTests
{
    [Fact]
    public async Task Round_trips_status_transitions()
    {
        using var h = new AuditTestHarness();
        var store = new EfRunStore(h);
        var run = new Run
        {
            Workflow = "pr-review", WorkflowSha = "abc123", Initiator = "tests",
            Repo = "deniz2412/test-repo-harness", PullRequest = 1
        };

        await store.SaveAsync(run);
        Assert.Equal(RunStatus.Pending, (await store.GetAsync(run.Id))!.Status);

        run.Status = RunStatus.Running;
        await store.SaveAsync(run);
        Assert.Equal(RunStatus.Running, (await store.GetAsync(run.Id))!.Status);

        run.Status = RunStatus.AwaitingApproval;
        await store.SaveAsync(run);
        Assert.Equal(RunStatus.AwaitingApproval, (await store.GetAsync(run.Id))!.Status);

        run.Status = RunStatus.Completed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(run);

        var final = await store.GetAsync(run.Id);
        Assert.Equal(RunStatus.Completed, final!.Status);
        Assert.NotNull(final.FinishedAt);
        Assert.Equal("abc123", final.WorkflowSha);
        Assert.Equal(1, final.PullRequest);

        // Saving repeatedly must not fan out into extra rows.
        await using var db = await h.CreateDbContextAsync();
        Assert.Equal(1, await db.Runs.CountAsync(r => r.Id == run.Id));
    }

    [Fact]
    public async Task Unknown_run_returns_null()
    {
        using var h = new AuditTestHarness();
        Assert.Null(await new EfRunStore(h).GetAsync(Guid.NewGuid()));
    }
}

public sealed class ApprovalStoreTests
{
    private static async Task<Run> SeedRunAsync(AuditTestHarness h)
    {
        var run = new Run
        {
            Workflow = "issue-to-pr", WorkflowSha = "test", Initiator = "tests",
            Repo = "deniz2412/test-repo-harness", Status = RunStatus.AwaitingApproval
        };
        await using var db = await h.CreateDbContextAsync();
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    [Fact]
    public async Task Request_then_decide_records_the_decision_and_audits_it()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        var store = new EfApprovalStore(h, h.Emitter);

        var requested = await store.RequestAsync(run.Id, "approve");
        Assert.Equal(GateDecision.Pending, requested.Decision);

        var decided = await store.DecideAsync(run.Id, "approve", GateDecision.Approved, "deniz", "looks fine");

        Assert.Equal(GateDecision.Approved, decided.Decision);
        Assert.Equal("deniz", decided.Approver);
        Assert.NotNull(decided.DecidedAt);
        Assert.Equal(GateDecision.Approved, (await store.GetAsync(run.Id, "approve"))!.Decision);

        // Invariant 5: the decision is on the audit trail, and the trail still verifies.
        await using var db = await h.CreateDbContextAsync();
        var evt = await db.Events.SingleAsync(e => e.RunId == run.Id && e.Type == "gate_decision");
        Assert.Equal("approve", evt.Node);
        Assert.True((await h.Verifier.VerifyAsync(run.Id)).Intact);
    }

    [Fact]
    public async Task A_decided_gate_cannot_be_decided_again()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        var store = new EfApprovalStore(h, h.Emitter);

        await store.RequestAsync(run.Id, "approve");
        await store.DecideAsync(run.Id, "approve", GateDecision.Rejected, "deniz", "no");

        var ex = await Assert.ThrowsAsync<GateAlreadyDecidedException>(() =>
            store.DecideAsync(run.Id, "approve", GateDecision.Approved, "someone-else", "yes please"));

        Assert.Equal(GateDecision.Rejected, ex.Decision);

        // Fail-closed: nothing was overwritten, and no second gate_decision event was written.
        var current = await store.GetAsync(run.Id, "approve");
        Assert.Equal(GateDecision.Rejected, current!.Decision);
        Assert.Equal("deniz", current.Approver);

        await using var db = await h.CreateDbContextAsync();
        Assert.Equal(1, await db.Events.CountAsync(e => e.RunId == run.Id && e.Type == "gate_decision"));
    }

    [Fact]
    public async Task Requesting_twice_returns_the_same_gate_and_never_resets_a_decision()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        var store = new EfApprovalStore(h, h.Emitter);

        var first = await store.RequestAsync(run.Id, "approve");
        await store.DecideAsync(run.Id, "approve", GateDecision.Approved, "deniz", null);

        var again = await store.RequestAsync(run.Id, "approve");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(GateDecision.Approved, again.Decision);
    }

    [Fact]
    public async Task Deciding_a_gate_nobody_requested_fails()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        var store = new EfApprovalStore(h, h.Emitter);

        await Assert.ThrowsAsync<GateNotFoundException>(() =>
            store.DecideAsync(run.Id, "approve", GateDecision.Approved, "deniz", null));
    }

    [Fact]
    public async Task Pending_is_not_a_decision()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        var store = new EfApprovalStore(h, h.Emitter);
        await store.RequestAsync(run.Id, "approve");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.DecideAsync(run.Id, "approve", GateDecision.Pending, "deniz", null));
    }

    [Fact]
    public async Task Unknown_gate_reads_as_null()
    {
        using var h = new AuditTestHarness();
        var store = new EfApprovalStore(h, h.Emitter);
        Assert.Null(await store.GetAsync(Guid.NewGuid(), "approve"));
    }
}
