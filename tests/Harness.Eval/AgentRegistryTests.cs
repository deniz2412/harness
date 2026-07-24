using Harness.Engine;
using Harness.Policy;
using Xunit;

namespace Harness.Eval;

/// <summary>
/// M7b — the named agent registry, checked offline against the production loaders and the real org
/// floor. Every shipped agent (flat + team-namespaced) loads and stays within the floor's tool ceiling;
/// a workflow that references an agent via <c>agent_ref</c> resolves it (merging the agent's prompt,
/// tools, model tier and schema onto the node); and a team's workflow resolves that team's agent
/// override. A referenced agent that stepped outside the floor, or an unresolvable ref, would fail here
/// — offline — not on a run.
/// </summary>
public class AgentRegistryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static string WorkflowsRoot() => Path.Combine(RepoRoot(), "workflows");
    private static string PromptsRoot() => Path.Combine(RepoRoot(), "prompts");
    private static string AgentsRoot() => Path.Combine(RepoRoot(), "agents");
    private static string PolicyPath() => Path.Combine(RepoRoot(), "policy.yaml");

    private static WorkflowLoader Loader() => new(WorkflowsRoot(), PromptsRoot(), AgentsRoot());
    private static AgentLoader Agents() => new(AgentsRoot(), PromptsRoot());
    private static PolicyFloor Floor() => PolicyFloor.FromFile(PolicyPath());

    /// <summary>Every agent under agents/ — flat and team-namespaced — as an AgentLoader name.</summary>
    public static IEnumerable<object[]> AllAgents() =>
        Directory.EnumerateFiles(AgentsRoot(), "*.yaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(AgentsRoot(), f)[..^".yaml".Length].Replace('\\', '/'))
            .Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(AllAgents))]
    public void Every_agent_loads_and_stays_within_the_org_floor(string name)
    {
        var agent = Agents().Load(name);
        var floor = Floor();

        Assert.Equal(64, agent.Sha.Length);   // content-pinned like a workflow
        foreach (var tool in agent.Tools)
            Assert.True(floor.AllowsTool(tool),
                $"Agent '{name}' names tool '{tool}' outside the org policy floor's allowed_tools ceiling.");
    }

    [Fact]
    public void The_payments_team_overrides_the_default_security_reviewer()
    {
        var catalog = new AgentCatalog(AgentsRoot());
        Assert.NotEqual(
            catalog.ResolvePath("security-reviewer"),                 // org default
            catalog.ResolvePath("security-reviewer", "payments"));    // team override wins

        // Same name, different persona content ⇒ different pinned identity.
        var org = Agents().Load("security-reviewer");
        var team = Agents().Load("teams/payments/security-reviewer");
        Assert.Equal("security-reviewer", org.Name);
        Assert.Equal("security-reviewer", team.Name);
        Assert.NotEqual(org.Sha, team.Sha);
    }

    [Fact]
    public void A_workflow_agent_ref_node_inherits_the_agents_prompt_tools_and_tier()
    {
        var wf = Loader().Load("pr-security-review");
        var review = Assert.Single(wf.Nodes, n => n.Id == "review");

        // The review node declared ONLY agent_ref; the loader merged the registry agent onto it.
        Assert.Equal("agents/security-reviewer.md", review.PromptRef);
        Assert.Equal("strong", review.ModelTier);
        Assert.Contains("repo.read", review.Tools);
        Assert.Contains("github.pr_diff", review.Tools);
        Assert.Contains("codesearch.query", review.Tools);
        Assert.DoesNotContain("github.pr_comment", review.Tools);   // that is the post node's tool

        // The resolved workflow still satisfies the org floor (merged tools included).
        new PolicyFloorValidator(Floor()).EnsureCompliant(wf);
    }

    [Fact]
    public void A_team_workflow_resolves_the_team_agent_override()
    {
        // teams/payments/pr-security-review lives in the payments namespace, so agent_ref resolves the
        // payments override, not the org default (agent scope follows workflow scope).
        var wf = Loader().Load("teams/payments/pr-security-review");
        var review = Assert.Single(wf.Nodes, n => n.Id == "review");

        Assert.Equal("teams/payments/security-reviewer.md", review.PromptRef);
        new PolicyFloorValidator(Floor()).EnsureCompliant(wf);
    }

    [Fact]
    public void Referencing_an_agent_pins_it_in_the_workflow_sha()
    {
        // The default and the payments variant reference the SAME agent name but resolve different
        // agents, so their workflow shas must differ — the run pins which agent version ran.
        Assert.NotEqual(
            Loader().Load("pr-security-review").Sha,
            Loader().Load("teams/payments/pr-security-review").Sha);
    }
}
