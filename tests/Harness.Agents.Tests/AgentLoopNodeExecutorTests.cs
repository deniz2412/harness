using Harness.Agents;
using Harness.Contracts;
using Harness.Engine;
using Xunit;

namespace Harness.Agents.Tests;

public class AgentLoopNodeExecutorTests
{
    private static Run NewRun() => new()
    {
        Workflow = "issue-to-pr", WorkflowSha = "sha", Initiator = "deniz",
        Repo = "org/repo", Issue = 7
    };

    private static WorkflowDefinition Wf() =>
        new() { Name = "issue-to-pr", Permissions = new() { ["repo"] = "write-worktree" }, Nodes = [] };

    private static NodeDefinition LoopNode(
        string? until = "validation_pass", string? run = "dotnet test",
        int maxIterations = 5, bool freshContext = true) =>
        new()
        {
            Id = "implement", Kind = "agent-loop",
            Until = until, Run = run, MaxIterations = maxIterations, FreshContext = freshContext
        };

    private static NodeContext Ctx(NodeDefinition node, IRunnerSession? runner) =>
        new(NewRun(), Wf(), node, new Dictionary<string, string>(), runner);

    [Fact]
    public async Task Passes_on_the_iteration_validation_succeeds_and_stops_early()
    {
        var agent = new FakeAgentIteration();
        var audit = new FakeAuditLog();
        var exec = new AgentLoopNodeExecutor(agent, audit);
        var runner = FakeRunnerSession.Always(0);   // validation passes immediately

        var result = await exec.ExecuteAsync(Ctx(LoopNode(maxIterations: 5), runner), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("iteration 1 of 5", result.Output);
        Assert.Equal(1, agent.Calls);                       // stopped after the first pass
        Assert.Single(runner.Commands);
        Assert.Equal("dotnet test", runner.Commands[0]);
    }

    [Fact]
    public async Task Validation_failure_is_fed_back_to_the_next_iteration()
    {
        var agent = new FakeAgentIteration();
        var audit = new FakeAuditLog();
        var exec = new AgentLoopNodeExecutor(agent, audit);
        // First dotnet test fails with a specific compiler error, then passes.
        var runner = FakeRunnerSession.FailsOnceWith("error CS0103: The name 'Discount' does not exist");

        var result = await exec.ExecuteAsync(Ctx(LoopNode(maxIterations: 5), runner), CancellationToken.None);

        Assert.True(result.Success);
        // The first iteration gets no feedback; the second must carry the first's failure output —
        // this is what was missing and made the loop reproduce the same failure every attempt.
        Assert.Null(agent.FeedbackSeen[0]);
        Assert.Contains("error CS0103", agent.FeedbackSeen[1]);
        Assert.Contains("exit 1", agent.FeedbackSeen[1]);
    }

    [Fact]
    public async Task Iterates_until_validation_passes_then_succeeds()
    {
        var agent = new FakeAgentIteration();
        var audit = new FakeAuditLog();
        var exec = new AgentLoopNodeExecutor(agent, audit);
        var runner = FakeRunnerSession.FailsThenPasses(failures: 2);   // passes on the 3rd

        var result = await exec.ExecuteAsync(Ctx(LoopNode(maxIterations: 5), runner), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("iteration 3 of 5", result.Output);
        Assert.Equal(3, agent.Calls);
        Assert.Equal(3, audit.CountOf("iteration_start"));
        Assert.Equal(3, audit.CountOf("tool_result"));      // one validation event per attempt
    }

    [Fact]
    public async Task Exhausting_max_iterations_without_a_pass_fails_the_node()
    {
        var agent = new FakeAgentIteration();
        var audit = new FakeAuditLog();
        var exec = new AgentLoopNodeExecutor(agent, audit);
        var runner = FakeRunnerSession.Always(1);   // validation never passes

        var result = await exec.ExecuteAsync(Ctx(LoopNode(maxIterations: 3), runner), CancellationToken.None);

        Assert.False(result.Success);                       // fail-closed, never "good enough"
        Assert.Contains("exhausted 3 iterations", result.Output);
        Assert.Equal(3, agent.Calls);                       // it did use its whole budget
    }

    [Fact]
    public async Task Unknown_until_predicate_fails_closed_without_running_the_agent()
    {
        var agent = new FakeAgentIteration();
        var exec = new AgentLoopNodeExecutor(agent, new FakeAuditLog());
        var runner = FakeRunnerSession.Always(0);

        var result = await exec.ExecuteAsync(
            Ctx(LoopNode(until: "looks_good"), runner), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not supported", result.Output);
        Assert.Equal(0, agent.Calls);                       // never even started
    }

    [Fact]
    public async Task Missing_runner_session_fails_closed()
    {
        var agent = new FakeAgentIteration();
        var exec = new AgentLoopNodeExecutor(agent, new FakeAuditLog());

        var result = await exec.ExecuteAsync(Ctx(LoopNode(), runner: null), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("needs a runner session", result.Output);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task Missing_validation_command_fails_closed()
    {
        var agent = new FakeAgentIteration();
        var exec = new AgentLoopNodeExecutor(agent, new FakeAuditLog());
        var runner = FakeRunnerSession.Always(0);

        var result = await exec.ExecuteAsync(
            Ctx(LoopNode(run: null), runner), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("needs a 'run' validation command", result.Output);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task Fresh_context_starts_every_iteration_with_no_carried_thread()
    {
        var agent = new FakeAgentIteration();
        var exec = new AgentLoopNodeExecutor(agent, new FakeAuditLog());
        var runner = FakeRunnerSession.FailsThenPasses(failures: 2);

        await exec.ExecuteAsync(
            Ctx(LoopNode(freshContext: true), runner), CancellationToken.None);

        Assert.All(agent.ThreadsSeen, t => Assert.Null(t));   // never carries the prior conversation
    }

    [Fact]
    public async Task Continued_context_carries_the_thread_between_iterations()
    {
        var agent = new FakeAgentIteration();
        var exec = new AgentLoopNodeExecutor(agent, new FakeAuditLog());
        var runner = FakeRunnerSession.FailsThenPasses(failures: 2);

        await exec.ExecuteAsync(
            Ctx(LoopNode(freshContext: false), runner), CancellationToken.None);

        Assert.Null(agent.ThreadsSeen[0]);                    // first turn starts fresh
        Assert.Equal("thread-token", agent.ThreadsSeen[1]);   // later turns continue it
        Assert.Equal("thread-token", agent.ThreadsSeen[2]);
    }
}
