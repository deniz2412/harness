using Harness.Contracts;
using Xunit;

namespace Harness.Policy.Tests;

/// <summary>
/// The M7 org policy floor: a <c>policy.yaml</c> that fixes the ceiling every workflow authored under
/// it must fit inside. Fail-closed — a malformed floor stops startup, an empty tool ceiling denies
/// every tool, and a non-compliant workflow never runs. Covers the loader (<see cref="PolicyFloor"/>)
/// and the load-time validator (<see cref="PolicyFloorValidator"/>).
/// </summary>
public class PolicyFloorTests
{
    // ---- fixtures ----

    private const string ValidYaml = """
        allowed_tools:
          - github.pr_diff
          - github.open_pr
          - repo.write_worktree
          - codesearch.query
        repo_allowlist:
          - deniz2412/test-repo-harness
          - octocat/*
        gate_requirements:
          - github.open_pr
        max_run_budget_usd: 5.00
        """;

    private static NodeDefinition Node(
        string id, string kind = "agent", string? gate = null,
        string[]? tools = null, string[]? dependsOn = null) =>
        new()
        {
            Id = id,
            Kind = kind,
            Gate = gate,
            Tools = [.. tools ?? []],
            DependsOn = [.. dependsOn ?? []],
        };

    private static WorkflowDefinition Workflow(string name, params NodeDefinition[] nodes) =>
        new() { Name = name, Nodes = [.. nodes] };

    private static PolicyFloor WriteToolFloor() =>
        new(allowedTools: ["github.pr_diff", "github.open_pr", "repo.write_worktree"],
            repoAllowlist: ["deniz2412/*"],
            gateRequirements: ["github.open_pr"],
            maxRunBudgetUsd: null);

    // ---- loader: happy path ----

    [Fact]
    public void Parses_a_valid_policy()
    {
        var floor = PolicyFloor.FromYaml(ValidYaml);

        Assert.Contains("github.pr_diff", floor.AllowedTools);
        Assert.Contains("codesearch.query", floor.AllowedTools);
        Assert.True(floor.AllowsTool("github.open_pr"));
        Assert.True(floor.AllowsTool(" github.pr_diff "));          // trimmed
        Assert.True(floor.AllowsTool("GITHUB.PR_DIFF"));            // case-insensitive
        Assert.False(floor.AllowsTool("github.merge_pr"));         // outside the ceiling

        Assert.Contains("deniz2412/test-repo-harness", floor.RepoAllowlist);
        Assert.Contains("octocat/*", floor.RepoAllowlist);

        Assert.True(floor.RequiresGate("github.open_pr"));
        Assert.False(floor.RequiresGate("github.pr_diff"));

        Assert.Equal(5.00m, floor.MaxRunBudgetUsd);
    }

    [Fact]
    public void Optional_budget_may_be_omitted()
    {
        var floor = PolicyFloor.FromYaml("allowed_tools: [github.pr_diff]\n");
        Assert.Null(floor.MaxRunBudgetUsd);
    }

    [Fact]
    public void Zero_budget_is_valid()
    {
        var floor = PolicyFloor.FromYaml("allowed_tools: [github.pr_diff]\nmax_run_budget_usd: 0\n");
        Assert.Equal(0m, floor.MaxRunBudgetUsd);
    }

    // ---- loader: deny-all ----

    [Fact]
    public void Empty_allowed_tools_denies_every_tool()
    {
        var floor = PolicyFloor.FromYaml("allowed_tools: []\n");

        Assert.Empty(floor.AllowedTools);
        Assert.False(floor.AllowsTool("github.pr_diff"));
        Assert.False(floor.AllowsTool("anything.at.all"));
    }

    [Fact]
    public void Omitted_allowed_tools_denies_every_tool()
    {
        var floor = PolicyFloor.FromYaml("repo_allowlist: [deniz2412/*]\n");

        Assert.Empty(floor.AllowedTools);
        Assert.False(floor.AllowsTool("github.pr_diff"));
    }

    // ---- loader: fail-closed on malformed input ----

    [Fact]
    public void Malformed_yaml_throws()
    {
        var ex = Assert.Throws<PolicyViolationException>(
            () => PolicyFloor.FromYaml("allowed_tools: [github.pr_diff\n  bad: : :\n"));
        Assert.Equal("policy-floor", ex.Stage);
    }

    [Fact]
    public void Negative_budget_throws()
    {
        var ex = Assert.Throws<PolicyViolationException>(
            () => PolicyFloor.FromYaml("allowed_tools: [github.pr_diff]\nmax_run_budget_usd: -1\n"));
        Assert.Equal("policy-floor", ex.Stage);
        Assert.Contains("max_run_budget_usd", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x")]                 // no owner/name separator
    [InlineData("a/b/c")]             // too many segments
    [InlineData("owner/")]            // empty name
    [InlineData("../etc")]            // path traversal
    public void Malformed_repo_entry_throws(string badRepo)
    {
        Assert.Throws<PolicyViolationException>(
            () => PolicyFloor.FromYaml($"allowed_tools: [github.pr_diff]\nrepo_allowlist: [{badRepo}]\n"));
    }

    [Fact]
    public void Blank_tool_name_throws()
    {
        var ex = Assert.Throws<PolicyViolationException>(
            () => new PolicyFloor(allowedTools: ["github.pr_diff", "   "], null, null, null));
        Assert.Equal("policy-floor", ex.Stage);
    }

    [Fact]
    public void Blank_gate_requirement_throws()
    {
        Assert.Throws<PolicyViolationException>(
            () => new PolicyFloor(allowedTools: ["github.open_pr"], null, gateRequirements: [""], null));
    }

    [Fact]
    public void FromFile_reads_a_policy_from_disk_and_a_missing_file_fails_closed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-policy-floor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "policy.yaml");
            File.WriteAllText(path, ValidYaml);

            var floor = PolicyFloor.FromFile(path);
            Assert.True(floor.AllowsTool("github.open_pr"));

            var ex = Assert.Throws<PolicyViolationException>(
                () => PolicyFloor.FromFile(Path.Combine(dir, "does-not-exist.yaml")));
            Assert.Equal("policy-floor", ex.Stage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- validator: tool ceiling ----

    [Fact]
    public void Out_of_ceiling_tool_is_a_violation()
    {
        var floor = new PolicyFloor(allowedTools: ["github.pr_diff"], null, null, null);
        var wf = Workflow("recon",
            Node("read", tools: ["github.pr_diff"]),
            Node("snoop", tools: ["github.pr_diff", "codesearch.query"]));

        var violations = new PolicyFloorValidator(floor).Validate(wf);

        var v = Assert.Single(violations);
        Assert.Equal("snoop", v.NodeId);
        Assert.Equal(PolicyFloorRule.ToolCeiling, v.Rule);
        Assert.Contains("codesearch.query", v.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deny_all_floor_flags_every_named_tool()
    {
        var floor = new PolicyFloor(allowedTools: [], null, null, null);
        var wf = Workflow("recon", Node("read", tools: ["github.pr_diff"]));

        var violations = new PolicyFloorValidator(floor).Validate(wf);

        var v = Assert.Single(violations);
        Assert.Equal(PolicyFloorRule.ToolCeiling, v.Rule);
    }

    [Fact]
    public void A_fully_compliant_read_only_workflow_has_no_violations()
    {
        var floor = new PolicyFloor(
            allowedTools: ["github.pr_diff", "codesearch.query"],
            repoAllowlist: ["deniz2412/*"],
            gateRequirements: ["github.open_pr"],
            maxRunBudgetUsd: 10m);
        var wf = Workflow("pr-review",
            Node("diff", tools: ["github.pr_diff"]),
            Node("review", tools: ["codesearch.query"], dependsOn: ["diff"]));

        Assert.Empty(new PolicyFloorValidator(floor).Validate(wf));
    }

    // ---- validator: gate requirement ----

    [Fact]
    public void Write_tool_node_with_no_human_gate_is_a_violation()
    {
        var wf = Workflow("issue-to-pr",
            Node("author", tools: ["repo.write_worktree"]),
            Node("open", tools: ["github.open_pr"], dependsOn: ["author"]));

        var violations = new PolicyFloorValidator(WriteToolFloor()).Validate(wf);

        var v = Assert.Single(violations);
        Assert.Equal("open", v.NodeId);
        Assert.Equal(PolicyFloorRule.GateRequirement, v.Rule);
    }

    [Fact]
    public void Write_tool_node_with_a_human_gate_upstream_is_compliant()
    {
        // author -> gate(human) -> open_pr : the M2 shape, generalised.
        var wf = Workflow("issue-to-pr",
            Node("author", tools: ["repo.write_worktree"]),
            Node("gate", kind: "gate", gate: "human", dependsOn: ["author"]),
            Node("open", tools: ["github.open_pr"], dependsOn: ["gate"]));

        Assert.Empty(new PolicyFloorValidator(WriteToolFloor()).Validate(wf));
    }

    [Fact]
    public void A_human_gate_that_is_transitively_upstream_satisfies_the_requirement()
    {
        // gate is two hops up (open depends on stage depends on gate): reachability must be transitive.
        var wf = Workflow("issue-to-pr",
            Node("gate", kind: "gate", gate: "human"),
            Node("stage", tools: ["repo.write_worktree"], dependsOn: ["gate"]),
            Node("open", tools: ["github.open_pr"], dependsOn: ["stage"]));

        Assert.Empty(new PolicyFloorValidator(WriteToolFloor()).Validate(wf));
    }

    [Fact]
    public void A_gate_downstream_of_the_write_does_not_satisfy_the_requirement()
    {
        // open_pr does NOT depend on the gate; the gate sits after it. Not an ancestor ⇒ violation.
        var wf = Workflow("issue-to-pr",
            Node("open", tools: ["github.open_pr"]),
            Node("gate", kind: "gate", gate: "human", dependsOn: ["open"]));

        var v = Assert.Single(new PolicyFloorValidator(WriteToolFloor()).Validate(wf));
        Assert.Equal(PolicyFloorRule.GateRequirement, v.Rule);
        Assert.Equal("open", v.NodeId);
    }

    [Fact]
    public void An_auto_gate_does_not_satisfy_a_human_gate_requirement()
    {
        var wf = Workflow("issue-to-pr",
            Node("gate", kind: "gate", gate: "auto"),
            Node("open", tools: ["github.open_pr"], dependsOn: ["gate"]));

        var v = Assert.Single(new PolicyFloorValidator(WriteToolFloor()).Validate(wf));
        Assert.Equal(PolicyFloorRule.GateRequirement, v.Rule);
    }

    [Fact]
    public void A_cyclic_depends_on_is_handled_without_looping()
    {
        // Defensive: the loader rejects unknown depends_on but not a cycle. No human gate anywhere,
        // so this must terminate and report the gate-requirement violation, not hang.
        var wf = Workflow("cyclic",
            Node("a", tools: ["github.open_pr"], dependsOn: ["b"]),
            Node("b", dependsOn: ["a"]));

        var violations = new PolicyFloorValidator(WriteToolFloor()).Validate(wf);
        Assert.Contains(violations, v => v.Rule == PolicyFloorRule.GateRequirement && v.NodeId == "a");
    }

    // ---- EnsureCompliant ----

    [Fact]
    public void EnsureCompliant_is_quiet_when_compliant()
    {
        var floor = new PolicyFloor(allowedTools: ["github.pr_diff"], null, null, null);
        var wf = Workflow("pr-review", Node("diff", tools: ["github.pr_diff"]));

        new PolicyFloorValidator(floor).EnsureCompliant(wf);   // does not throw
    }

    [Fact]
    public void EnsureCompliant_throws_listing_all_violations()
    {
        // One out-of-ceiling tool AND one ungated write tool ⇒ two violations, both reported.
        var floor = new PolicyFloor(
            allowedTools: ["github.open_pr"],   // repo.write_worktree is NOT in the ceiling
            repoAllowlist: null,
            gateRequirements: ["github.open_pr"],
            maxRunBudgetUsd: null);
        var wf = Workflow("bad",
            Node("author", tools: ["repo.write_worktree"]),
            Node("open", tools: ["github.open_pr"], dependsOn: ["author"]));

        var ex = Assert.Throws<PolicyFloorException>(
            () => new PolicyFloorValidator(floor).EnsureCompliant(wf));

        Assert.Equal("bad", ex.WorkflowName);
        Assert.Equal(2, ex.Violations.Count);
        Assert.Contains(ex.Violations, v => v.Rule == PolicyFloorRule.ToolCeiling && v.NodeId == "author");
        Assert.Contains(ex.Violations, v => v.Rule == PolicyFloorRule.GateRequirement && v.NodeId == "open");
        Assert.Contains("repo.write_worktree", ex.Message, StringComparison.Ordinal);
    }
}
