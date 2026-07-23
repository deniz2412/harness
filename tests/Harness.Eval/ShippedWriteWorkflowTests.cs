using Harness.Engine;
using Xunit;

namespace Harness.Eval;

/// <summary>
/// The write-path workflows this workstream ships must satisfy the real loader's fail-closed
/// validation (prompt_refs resolve, gate values are auto|human, depends_on targets exist) and, on
/// top of that, must honour the M2 invariant: a HUMAN gate sits before the node that pushes/opens a
/// PR. Caught here, offline, not on a run. Uses the production <see cref="WorkflowLoader"/>.
/// </summary>
public class ShippedWriteWorkflowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static WorkflowLoader Loader()
    {
        var root = RepoRoot();
        return new WorkflowLoader(Path.Combine(root, "workflows"), Path.Combine(root, "prompts"));
    }

    [Theory]
    [InlineData("test-generation")]
    [InlineData("issue-to-pr")]
    public void Loads_and_is_pinned(string name)
    {
        var loader = Loader();

        var wf = loader.Load(name);

        Assert.Equal(64, wf.Sha.Length);                 // sha256, lowercase hex
        Assert.Equal(wf.Sha, loader.Load(name).Sha);     // deterministic pin
    }

    [Theory]
    [InlineData("test-generation")]
    [InlineData("issue-to-pr")]
    public void Declares_a_write_ceiling_so_the_engine_provisions_a_sandbox(string name)
    {
        var wf = Loader().Load(name);

        Assert.Equal("write-worktree", wf.Permissions["repo"]);
        Assert.Equal("open_pr+issues", wf.Permissions["github"]);
    }

    [Theory]
    [InlineData("test-generation")]
    [InlineData("issue-to-pr")]
    public void A_human_gate_precedes_the_pr_open_node(string name)
    {
        var wf = Loader().Load(name);

        var openPr = Assert.Single(wf.Nodes, n => n.Tools.Contains("github.open_pr"));

        // The open-pr node depends on a gate node, and that gate is a hard human gate.
        var gate = Assert.Single(wf.Nodes, n => n.Kind == "gate");
        Assert.Equal("human", gate.Gate);
        Assert.Contains("initiator", gate.Approvers);
        Assert.Contains(gate.Id, openPr.DependsOn);
    }

    [Theory]
    [InlineData("test-generation")]
    [InlineData("issue-to-pr")]
    public void Never_references_a_merge_capability(string name)
    {
        var wf = Loader().Load(name);

        var allTools = wf.Nodes.SelectMany(n => n.Tools).ToList();
        Assert.DoesNotContain(allTools, t => t.Contains("merge", StringComparison.OrdinalIgnoreCase));
        // open_pr is the terminal write; nothing may depend on the PR-opening node.
        var openPr = Assert.Single(wf.Nodes, n => n.Tools.Contains("github.open_pr"));
        Assert.DoesNotContain(wf.Nodes, n => n.DependsOn.Contains(openPr.Id));
    }

    [Fact]
    public void Agent_loop_nodes_validate_with_dotnet_test_and_are_bounded()
    {
        foreach (var name in new[] { "test-generation", "issue-to-pr" })
        {
            var wf = Loader().Load(name);
            var loop = Assert.Single(wf.Nodes, n => n.Kind == "agent-loop");

            Assert.Equal("validation_pass", loop.Until);
            Assert.Equal("dotnet test", loop.Run);
            Assert.True(loop.FreshContext);
            Assert.InRange(loop.MaxIterations, 1, 10);
            Assert.Contains("repo.write_worktree", loop.Tools);
        }
    }
}
