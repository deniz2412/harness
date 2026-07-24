using Harness.Engine;
using Harness.Policy;
using Xunit;

namespace Harness.Eval;

/// <summary>
/// Every shipped workflow must satisfy the real loader's fail-closed validation (prompt_refs
/// resolve, gate values are auto|human, depends_on targets exist) and — beyond what the loader
/// checks — must name only known node kinds and catalogued tools, and never reference a merge
/// capability. The gated-write workflows additionally must place a HUMAN gate before the node that
/// pushes/opens a PR (the M2 invariant). Caught here, offline, not on a run, using the production
/// <see cref="WorkflowLoader"/> and the real <see cref="ToolCatalog"/>.
/// </summary>
public class ShippedWriteWorkflowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static WorkflowLoader Loader() =>
        new(Path.Combine(RepoRoot(), "workflows"), Path.Combine(RepoRoot(), "prompts"),
            Path.Combine(RepoRoot(), "agents"));

    /// <summary>Every workflow shipped in workflows/ — discovered from disk so a new one is covered
    /// by the universal checks automatically, not only when someone remembers to add it here.</summary>
    public static IEnumerable<object[]> AllWorkflows() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "workflows"), "*.yaml")
            .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    /// <summary>The gated-write workflows: they push a branch / open a PR and so MUST carry a human
    /// gate before it. The analysis workflows (pr-review, dependency-audit, secrets-sweep) end at a
    /// comment and are deliberately not here.</summary>
    private static readonly string[] GatedWriteWorkflows =
        ["test-generation", "issue-to-pr", "coverage-gap-analysis", "regression-suite-author", "threat-model-draft"];

    private static readonly string[] KnownKinds = ["agent", "agent-loop", "bash", "gate"];

    // ---- universal checks: every shipped workflow ----

    [Theory]
    [MemberData(nameof(AllWorkflows))]
    public void Loads_and_is_pinned(string name)
    {
        var wf = Loader().Load(name);
        Assert.Equal(64, wf.Sha.Length);                 // sha256, lowercase hex
        Assert.Equal(wf.Sha, Loader().Load(name).Sha);   // deterministic pin
    }

    [Theory]
    [MemberData(nameof(AllWorkflows))]
    public void Every_node_kind_is_known_and_every_tool_is_in_the_curated_catalog(string name)
    {
        // The loader validates ids/depends_on/prompt_ref/gate but NOT node kinds or tool names —
        // those only fail-closed at runtime (executor dispatch / the ToolRegistry switch / the
        // catalog). For data workflows that is too late: catch an invented tool or kind here.
        var wf = Loader().Load(name);
        var catalog = ToolCatalog.Default;
        // M7c: a tool is valid if it is a curated built-in OR a declared MCP connector operation
        // (config+review, governed by the connector allowlist rather than the code catalog).
        var connectors = McpConnectorRegistry.FromFile(Path.Combine(RepoRoot(), "connectors.yaml"));

        foreach (var node in wf.Nodes)
        {
            Assert.Contains(node.Kind, KnownKinds);
            foreach (var tool in node.Tools)
            {
                var isBuiltin = catalog.TryGetTool(tool, out _);
                var isConnector = McpConnectorRegistry.TryParseToolName(tool, out var ns, out var op)
                                  && connectors.IsAllowed(ns, op);
                Assert.True(isBuiltin || isConnector,
                    $"'{name}' node '{node.Id}' names tool '{tool}', which is neither a curated catalog "
                    + "tool nor a declared connector operation.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllWorkflows))]
    public void Never_references_a_merge_capability(string name)
    {
        // Invariant 1, checked for EVERY workflow: no tool name mentions merge (there is no merge
        // tool and never will be), and nothing depends on a PR-opening node (open_pr is terminal).
        var wf = Loader().Load(name);

        var allTools = wf.Nodes.SelectMany(n => n.Tools).ToList();
        Assert.DoesNotContain(allTools, t => t.Contains("merge", StringComparison.OrdinalIgnoreCase));

        var openPr = wf.Nodes.SingleOrDefault(n => n.Tools.Contains("github.open_pr"));
        if (openPr is not null)
            Assert.DoesNotContain(wf.Nodes, n => n.DependsOn.Contains(openPr.Id));
    }

    // ---- gated-write workflows only ----

    [Theory]
    [MemberData(nameof(GatedWrite))]
    public void Declares_a_write_ceiling_so_the_engine_provisions_a_sandbox(string name)
    {
        var wf = Loader().Load(name);
        Assert.Equal("write-worktree", wf.Permissions["repo"]);
        Assert.Equal("open_pr+issues", wf.Permissions["github"]);
    }

    [Theory]
    [MemberData(nameof(GatedWrite))]
    public void A_human_gate_precedes_the_pr_open_node(string name)
    {
        var wf = Loader().Load(name);

        var openPr = Assert.Single(wf.Nodes, n => n.Tools.Contains("github.open_pr"));
        var gate = Assert.Single(wf.Nodes, n => n.Kind == "gate");
        Assert.Equal("human", gate.Gate);
        Assert.Contains("initiator", gate.Approvers);
        Assert.Contains(gate.Id, openPr.DependsOn);   // the PR-open node is downstream of the gate
    }

    public static IEnumerable<object[]> GatedWrite() => GatedWriteWorkflows.Select(n => new object[] { n });

    [Fact]
    public void Agent_loop_nodes_validate_with_dotnet_test_and_are_bounded()
    {
        // The QA/test-authoring workflows use a bounded agent-loop validated by `dotnet test`.
        // (threat-model-draft is a gated write but has no loop — it is deliberately excluded.)
        foreach (var name in new[]
                 { "test-generation", "issue-to-pr", "coverage-gap-analysis", "regression-suite-author" })
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
