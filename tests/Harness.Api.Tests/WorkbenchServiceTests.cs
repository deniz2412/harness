using Harness.Api.Ops;
using Harness.Engine;
using Harness.Policy;
using Xunit;

namespace Harness.Api.Tests;

/// <summary>
/// The F3 authoring-workbench validation service, wired to the REAL repo (the production loader, the
/// shipped policy floor and the declared connectors) so a draft is checked against exactly the
/// floor/catalog a run enforces. Offline: no model, no tool, no network, no write — the whole point
/// is that authored YAML is validated but never executed.
/// </summary>
public class WorkbenchServiceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static WorkbenchService Service()
    {
        var root = RepoRoot();
        var loader = new WorkflowLoader(
            Path.Combine(root, "workflows"), Path.Combine(root, "prompts"), Path.Combine(root, "agents"));
        var floor = new PolicyFloorValidator(PolicyFloor.FromFile(Path.Combine(root, "policy.yaml")));
        var connectors = McpConnectorRegistry.FromFile(Path.Combine(root, "connectors.yaml"));
        return new WorkbenchService(loader, floor, connectors);
    }

    // A read-only three-node review whose tools are all catalogued and inside the floor, under a
    // repo:read / github:comment ceiling. The canonical happy path.
    private const string ValidReview = """
        name: my-review
        description: A read-only review draft.
        permissions:
          repo: read
          github: comment
        nodes:
          - id: gather
            kind: agent
            prompt_ref: pr-review/gather.md
            tools: [github.pr_diff, repo.read, codesearch.query]
          - id: review
            kind: agent
            depends_on: [gather]
            prompt_ref: pr-review/review.md
            tools: [repo.read]
          - id: post
            kind: agent
            depends_on: [review]
            gate: auto
            prompt_ref: pr-review/post.md
            tools: [github.pr_comment]
        """;

    // Two nodes that depend on each other: every node exists, prompts resolve, tools are catalogued +
    // floored — so it passes structure, catalog and floor, and ONLY the cycle check should reject it.
    private const string CyclicWorkflow = """
        name: cyclic
        permissions:
          repo: read
        nodes:
          - id: a
            kind: agent
            prompt_ref: pr-review/gather.md
            depends_on: [b]
            tools: [repo.read]
          - id: b
            kind: agent
            prompt_ref: pr-review/review.md
            depends_on: [a]
            tools: [repo.read]
        """;

    [Fact]
    public void A_dependency_cycle_is_rejected_even_when_structure_catalog_and_floor_pass()
    {
        var result = Service().Validate(CyclicWorkflow);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Severity == "error" && i.Message.Contains("cycle"));
    }

    [Fact]
    public void Valid_workflow_validates_with_dag_layers_and_publish_path()
    {
        var result = Service().Validate(ValidReview);

        Assert.True(result.Ok);
        Assert.DoesNotContain(result.Issues, i => i.Severity == "error");
        Assert.NotNull(result.Dag);
        Assert.Equal("my-review", result.Dag!.Name);
        Assert.Equal("read", result.Dag.Permissions["repo"]);
        Assert.Equal("comment", result.Dag.Permissions["github"]);

        Assert.Equal(0, result.Dag.Nodes.Single(n => n.Id == "gather").Layer);
        Assert.Equal(1, result.Dag.Nodes.Single(n => n.Id == "review").Layer);
        Assert.Equal(2, result.Dag.Nodes.Single(n => n.Id == "post").Layer);

        // Publish path is a preview string for the org-default namespace — nothing is written.
        Assert.Equal("workflows/my-review.yaml", result.PublishPath);
    }

    [Fact]
    public void Blank_input_is_a_single_empty_error_with_no_dag()
    {
        var result = Service().Validate("   ");

        Assert.False(result.Ok);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("error", issue.Severity);
        Assert.Contains("empty", issue.Message);
        Assert.Null(result.Dag);
        Assert.Null(result.PublishPath);
    }

    [Fact]
    public void Malformed_yaml_is_one_error_and_no_dag()
    {
        var result = Service().Validate("name: broken\nnodes: [ {id: a, kind: agent");

        Assert.False(result.Ok);
        Assert.Single(result.Issues);
        Assert.Equal("error", result.Issues[0].Severity);
        Assert.Null(result.Dag);
    }

    [Fact]
    public void Structural_error_is_surfaced_one_at_a_time_with_no_dag()
    {
        // A depends_on that names no node — the loader fail-closes on it (structural), so a single
        // error and no DAG. (Structural errors are first-only because the loader throws on the first.)
        var yaml = """
            name: dangling-dep
            permissions:
              repo: read
            nodes:
              - id: only
                kind: agent
                prompt_ref: pr-review/gather.md
                tools: [repo.read]
                depends_on: [nope]
            """;

        var result = Service().Validate(yaml);

        Assert.False(result.Ok);
        Assert.Single(result.Issues);
        Assert.Null(result.Dag);
        Assert.Contains("nope", result.Issues[0].Message);
    }

    [Fact]
    public void Uncatalogued_tool_is_a_catalog_error_naming_the_tool_and_node()
    {
        var yaml = """
            name: bad-tool
            permissions:
              repo: read
              github: comment
            nodes:
              - id: work
                kind: agent
                prompt_ref: pr-review/gather.md
                tools: [github.merge_pr]
            """;

        var result = Service().Validate(yaml);

        Assert.False(result.Ok);
        // The DAG is still built so the editor can render it beside the error.
        Assert.NotNull(result.Dag);
        var catalogError = Assert.Single(
            result.Issues, i => i.Message.Contains("not a curated tool or a declared connector operation"));
        Assert.Equal("error", catalogError.Severity);
        Assert.Contains("github.merge_pr", catalogError.Message);
        Assert.Equal("work", catalogError.NodeId);
    }

    [Fact]
    public void Write_tool_without_a_human_gate_is_a_floor_error_and_with_a_gate_is_clean()
    {
        // github.open_pr is inside the floor's ceiling but is gate-required: no upstream human gate
        // ⇒ a floor violation.
        var ungated = """
            name: ungated-write
            permissions:
              repo: write-worktree
              github: open_pr+issues
            nodes:
              - id: work
                kind: agent
                prompt_ref: pr-review/gather.md
                tools: [github.open_pr]
            """;

        var ungatedResult = Service().Validate(ungated);
        Assert.False(ungatedResult.Ok);
        var gateError = Assert.Single(ungatedResult.Issues, i => i.Message.Contains("human gate"));
        Assert.Equal("error", gateError.Severity);
        Assert.Equal("work", gateError.NodeId);

        // The same workflow WITH a human gate upstream of the write node ⇒ clean.
        var gated = """
            name: gated-write
            permissions:
              repo: write-worktree
              github: open_pr+issues
            nodes:
              - id: approve
                kind: gate
                gate: human
                approvers: [initiator]
              - id: work
                kind: agent
                depends_on: [approve]
                prompt_ref: pr-review/gather.md
                tools: [github.open_pr]
            """;

        var gatedResult = Service().Validate(gated);
        Assert.True(gatedResult.Ok);
        Assert.DoesNotContain(gatedResult.Issues, i => i.Severity == "error");
        Assert.NotNull(gatedResult.Dag);
    }

    [Fact]
    public void Declared_connector_op_on_a_read_only_node_validates()
    {
        // docs.search is not a code-catalogued built-in; it is a declared+allowed connector operation
        // and is named in the floor. On a read-only node it is fine.
        var yaml = """
            name: kb-lookup
            permissions:
              repo: read
              github: comment
            nodes:
              - id: look
                kind: agent
                prompt_ref: pr-review/gather.md
                tools: [docs.search]
            """;

        var result = Service().Validate(yaml);

        Assert.True(result.Ok);
        Assert.DoesNotContain(result.Issues, i => i.Severity == "error");
        Assert.NotNull(result.Dag);
    }

    [Fact]
    public void Team_agent_ref_resolves_within_the_team_namespace()
    {
        // agent_ref carries the agent's prompt/tools/tier; validated under team="payments" it resolves
        // the payments-team security-reviewer and merges its (floored, catalogued) config onto the node.
        var yaml = """
            name: team-review
            permissions:
              repo: read
              github: comment
            nodes:
              - id: sec
                kind: agent
                agent_ref: security-reviewer
            """;

        var result = Service().Validate(yaml, team: "payments");

        Assert.True(result.Ok);
        Assert.DoesNotContain(result.Issues, i => i.Severity == "error");
        Assert.NotNull(result.Dag);

        var node = result.Dag!.Nodes.Single(n => n.Id == "sec");
        Assert.Equal("security-reviewer", node.AgentRef);
        Assert.Equal("strong", node.ModelTier);           // merged from the agent definition
        Assert.Contains("repo.read", node.Tools);         // merged tools, catalogued + floored

        // A team-namespaced draft previews under the team path.
        Assert.Equal("workflows/teams/payments/team-review.yaml", result.PublishPath);
    }

    [Fact]
    public void Bogus_agent_ref_is_a_structural_error_with_no_dag()
    {
        var yaml = """
            name: bad-agent
            permissions:
              repo: read
            nodes:
              - id: sec
                kind: agent
                agent_ref: does-not-exist
            """;

        var result = Service().Validate(yaml, team: "payments");

        Assert.False(result.Ok);
        Assert.Single(result.Issues);
        Assert.Null(result.Dag);
    }
}
