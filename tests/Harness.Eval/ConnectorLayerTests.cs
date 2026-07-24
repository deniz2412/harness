using Harness.Engine;
using Harness.Policy;
using Xunit;

namespace Harness.Eval;

/// <summary>
/// M7c — the MCP connector layer, checked offline against the SHIPPED connectors.yaml + org floor.
/// A declared connector operation is admitted on a read-only node; an un-allowlisted operation, an
/// undeclared namespace, and the write-capable boundary are all fail-closed; and the demonstrator
/// workflow that names docs.search loads and satisfies the floor. (The actual mount + per-call audit
/// is pinned by Harness.Tools.Tests/ConnectorMountTests against the same governance.)
/// </summary>
public class ConnectorLayerTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static McpConnectorRegistry Connectors() =>
        McpConnectorRegistry.FromFile(Path.Combine(RepoRoot(), "connectors.yaml"));

    private static PolicyPipeline Pipeline() =>
        new(SecretScanner.Default, ToolCatalog.Default, Connectors());

    private static readonly Dictionary<string, string> ReadOnly =
        new() { ["repo"] = "read", ["github"] = "comment" };
    private static readonly Dictionary<string, string> WriteCapable =
        new() { ["repo"] = "write-worktree", ["github"] = "open_pr+issues" };

    [Fact]
    public void The_shipped_docs_connector_is_declared_read_only_with_its_op_allowlist()
    {
        var c = Connectors();
        Assert.True(c.IsDeclared("docs"));
        Assert.True(c.IsAllowed("docs", "search"));
        Assert.True(c.IsAllowed("docs", "get_page"));
        Assert.False(c.IsAllowed("docs", "delete"));   // deny-by-default: op not on the allowlist
        Assert.False(c.IsWriteCapable("docs"));         // read-only connector
    }

    [Fact]
    public void A_declared_connector_op_is_admitted_on_a_read_only_node()
    {
        // Declared on the node, read-only ceiling → allowed (no throw).
        Pipeline().AssertToolAllowed("docs.search", new[] { "repo.read", "docs.search" }, ReadOnly);
    }

    [Fact]
    public void An_un_allowlisted_connector_op_is_refused()
    {
        Assert.Throws<PolicyViolationException>(() =>
            Pipeline().AssertToolAllowed("docs.delete", new[] { "docs.delete" }, ReadOnly));
    }

    [Fact]
    public void An_undeclared_connector_namespace_is_refused()
    {
        // jira.* is not declared → falls to the curated-catalog path → not a built-in → denied.
        Assert.Throws<PolicyViolationException>(() =>
            Pipeline().AssertToolAllowed("jira.create", new[] { "jira.create" }, ReadOnly));
    }

    [Fact]
    public void A_read_only_connector_cannot_attach_to_a_write_capable_node()
    {
        // The write-capable boundary: docs (write_capable: false) refused on a write-capable ceiling.
        Assert.Throws<PolicyViolationException>(() =>
            Pipeline().AssertToolAllowed("docs.search", new[] { "docs.search" }, WriteCapable));
    }

    [Fact]
    public void The_connector_demonstrator_workflow_loads_and_satisfies_the_floor()
    {
        var loader = new WorkflowLoader(
            Path.Combine(RepoRoot(), "workflows"), Path.Combine(RepoRoot(), "prompts"),
            Path.Combine(RepoRoot(), "agents"));
        var wf = loader.Load("pr-review-with-context");

        var floor = PolicyFloor.FromFile(Path.Combine(RepoRoot(), "policy.yaml"));
        new PolicyFloorValidator(floor).EnsureCompliant(wf);   // docs.search is within the floor
    }
}
