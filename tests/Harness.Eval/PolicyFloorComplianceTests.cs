using Harness.Contracts;
using Harness.Engine;
using Harness.Policy;
using Xunit;

namespace Harness.Eval;

/// <summary>
/// The headline M7 guarantee, checked offline: EVERY workflow the platform ships — the flat org
/// defaults and the team-namespaced variants under workflows/teams/&lt;team&gt;/ — satisfies the org
/// policy FLOOR declared in the repo-root policy.yaml. The floor is fail-closed: it caps the tools a
/// workflow may name, the repos it may target, and requires a human gate upstream of any gated write
/// tool. A workflow that steps outside the ceiling is rejected at load time, never on a run. This
/// suite loads the real floor and every shipped workflow through the production
/// <see cref="WorkflowLoader"/> and asserts compliance.
///
/// Discovery uses a directory walk of workflows/**/*.yaml (the acceptable fallback noted in the M7
/// data-layer brief), mapping each file to the loader name via its path relative to workflows/ — so
/// a newly-shipped workflow, flat or team-namespaced, is covered automatically.
/// </summary>
public class PolicyFloorComplianceTests
{
    // Copied from ShippedWriteWorkflowTests (do not edit that file): climb to the repo root, the
    // directory holding Harness.sln, so the test is independent of the test binary's location.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static string WorkflowsRoot() => Path.Combine(RepoRoot(), "workflows");
    private static string PolicyPath() => Path.Combine(RepoRoot(), "policy.yaml");

    private static WorkflowLoader Loader() =>
        new(WorkflowsRoot(), Path.Combine(RepoRoot(), "prompts"));

    private static PolicyFloor Floor() => PolicyFloor.FromFile(PolicyPath());

    /// <summary>Every workflow under workflows/ — flat defaults AND team-namespaced variants — as a
    /// loader name (its path relative to workflows/, minus the .yaml extension). A team file
    /// workflows/teams/payments/pr-review.yaml maps to "teams/payments/pr-review".</summary>
    public static IEnumerable<object[]> AllWorkflows() =>
        Directory.EnumerateFiles(WorkflowsRoot(), "*.yaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(WorkflowsRoot(), f))
            .Select(rel => rel[..^".yaml".Length])
            .Select(name => new object[] { name });

    // ---- the headline guarantee ----

    [Theory]
    [MemberData(nameof(AllWorkflows))]
    public void Every_shipped_workflow_satisfies_the_org_floor(string name)
    {
        var wf = Loader().Load(name);
        var validator = new PolicyFloorValidator(Floor());

        // EnsureCompliant is fail-closed: it throws PolicyFloorException on any violation. If a
        // shipped workflow ever names an un-floored tool or opens a PR without a human gate, this
        // fails here — offline — rather than at POST /runs.
        validator.EnsureCompliant(wf);

        // And, equivalently, no violations are reported.
        Assert.Empty(validator.Validate(wf));
    }

    [Fact]
    public void The_payments_team_workflow_loads_and_is_floor_compliant()
    {
        // The example team-owned override: workflows/teams/payments/pr-review.yaml is a same-named
        // variant of the org-default pr-review, authored WITHIN the ceiling. It must load and comply.
        var wf = Loader().Load("teams/payments/pr-review");

        Assert.Equal("pr-review", wf.Name);                 // a same-named override of the default
        var validator = new PolicyFloorValidator(Floor());
        validator.EnsureCompliant(wf);
        Assert.Empty(validator.Validate(wf));
    }

    // ---- the floor actually bites ----

    [Fact]
    public void A_workflow_naming_an_un_floored_tool_is_rejected()
    {
        var validator = new PolicyFloorValidator(Floor());

        var rogue = new WorkflowDefinition
        {
            Name = "rogue",
            Permissions = new Dictionary<string, string> { ["repo"] = "read", ["github"] = "comment" },
            Nodes =
            [
                new NodeDefinition
                {
                    Id = "do-it",
                    Kind = "agent",
                    PromptRef = "pr-review/review.md",
                    Tools = ["repo.read", "tool.not_in_the_floor"],
                },
            ],
        };

        Assert.NotEmpty(validator.Validate(rogue));
        Assert.Throws<PolicyFloorException>(() => validator.EnsureCompliant(rogue));
    }

    [Fact]
    public void A_gated_write_tool_without_a_human_gate_is_rejected()
    {
        // github.open_pr is in the floor's allowed_tools but is a gate_requirement: using it with no
        // human gate anywhere upstream violates the floor.
        var validator = new PolicyFloorValidator(Floor());

        var ungated = new WorkflowDefinition
        {
            Name = "ungated-write",
            Permissions = new Dictionary<string, string> { ["repo"] = "write-worktree", ["github"] = "open_pr+issues" },
            Nodes =
            [
                new NodeDefinition
                {
                    Id = "open-pr",
                    Kind = "agent",
                    PromptRef = "pr-review/post.md",
                    Tools = ["github.push_branch", "github.open_pr"],
                    // deliberately NO upstream gate: human == null
                },
            ],
        };

        Assert.NotEmpty(validator.Validate(ungated));
        Assert.Throws<PolicyFloorException>(() => validator.EnsureCompliant(ungated));
    }
}
