using Harness.Api.Ops;
using Harness.Audit;
using Harness.Contracts;
using Harness.Engine;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harness.Api.Tests;

/// <summary>
/// The F2 workflow-catalog read model, exercised against the REAL shipped <c>workflows/</c>,
/// <c>prompts/</c> and <c>agents/</c> (so it sees what a run would resolve — agent_refs merged, shas
/// stamped) and an in-memory runs table for the stats. Offline by contract: no gateway, no Postgres,
/// no network.
/// </summary>
public sealed class WorkflowCatalogQueriesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static string WorkflowsDir => Path.Combine(RepoRoot(), "workflows");

    private static WorkflowCatalogQueries NewQueries(IDbContextFactory<HarnessDbContext>? db = null)
    {
        var loader = new WorkflowLoader(
            WorkflowsDir, Path.Combine(RepoRoot(), "prompts"), Path.Combine(RepoRoot(), "agents"));
        return new WorkflowCatalogQueries(
            new WorkflowCatalog(WorkflowsDir), loader, WorkflowsDir, db ?? new InMemoryDbContextFactory());
    }

    // ---- ListWorkflows ----

    [Fact]
    public void ListWorkflows_surfaces_the_shipped_workflows_including_a_team_one()
    {
        var list = NewQueries().ListWorkflows();

        // The flat org default and the team override are BOTH present (distinct scopes), keyed by the
        // loader name a run targets.
        var prReview = Assert.Single(list, w => w.LoaderName == "pr-review");
        Assert.Equal(WorkflowScope.Flat, prReview.Scope);
        Assert.Null(prReview.Team);

        var teamPrReview = Assert.Single(list, w => w.LoaderName == "teams/payments/pr-review");
        Assert.Equal(WorkflowScope.Team, teamPrReview.Scope);
        Assert.Equal("payments", teamPrReview.Team);
    }

    [Fact]
    public void ListWorkflows_summarises_each_workflow()
    {
        var list = NewQueries().ListWorkflows();

        // A read-mostly review ends at a comment, carries no human gate, and has a real node count + sha.
        var prReview = Assert.Single(list, w => w.LoaderName == "pr-review");
        Assert.True(prReview.NodeCount > 0);
        Assert.Equal("comment", prReview.EndsAt);
        Assert.False(prReview.HasHumanGate);
        Assert.Equal(64, prReview.Sha.Length);

        // A gated write ends at a PR and is fronted by a human gate.
        var testGen = Assert.Single(list, w => w.LoaderName == "test-generation");
        Assert.Equal("PR", testGen.EndsAt);
        Assert.True(testGen.HasHumanGate);
        Assert.True(testGen.NodeCount > 0);
    }

    // ---- GetWorkflow ----

    [Fact]
    public void GetWorkflow_returns_nodes_with_topological_layers()
    {
        var detail = NewQueries().GetWorkflow("pr-review");

        Assert.NotNull(detail);
        Assert.Equal("pr-review", detail!.LoaderName);
        Assert.Equal(WorkflowScope.Flat, detail.Scope);

        int Layer(string id) => Assert.Single(detail.Nodes, n => n.Id == id).Layer;
        Assert.Equal(0, Layer("gather"));   // root
        Assert.Equal(1, Layer("review"));   // depends on gather
        Assert.Equal(2, Layer("post"));     // depends on review
    }

    [Fact]
    public void GetWorkflow_returns_null_for_an_unknown_workflow()
    {
        Assert.Null(NewQueries().GetWorkflow("no-such-workflow"));
        Assert.Null(NewQueries().GetWorkflow(""));
    }

    [Fact]
    public void GetWorkflow_surfaces_a_referenced_agents_merged_config()
    {
        // pr-security-review's `review` node is defined purely by agent_ref: security-reviewer. The
        // loader merges the agent's prompt, tools, tier and schema onto the node; the read model shows
        // them as-is plus the AgentRef so the UI can render "via agent security-reviewer".
        var detail = NewQueries().GetWorkflow("pr-security-review");
        Assert.NotNull(detail);

        var review = Assert.Single(detail!.Nodes, n => n.Id == "review");
        Assert.Equal("security-reviewer", review.AgentRef);
        Assert.Equal("strong", review.ModelTier);
        Assert.Equal("agents/security-reviewer.md", review.PromptRef);
        Assert.Equal("schemas/review-findings.json", review.OutputSchema);
        Assert.Contains("repo.read", review.Tools);
        Assert.Contains("github.pr_diff", review.Tools);
        Assert.Contains("codesearch.query", review.Tools);
    }

    // ---- WorkflowStatsAsync ----

    [Fact]
    public async Task WorkflowStatsAsync_groups_runs_and_computes_success_rate_and_last_run()
    {
        var db = new InMemoryDbContextFactory();
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);

        // wf-a: 2 completed + 1 failed → success rate 2/3; AwaitingApproval does not count as terminal.
        SeedRun(db, "wf-a", RunStatus.Completed, t0);
        SeedRun(db, "wf-a", RunStatus.Completed, t0.AddHours(1));
        SeedRun(db, "wf-a", RunStatus.Failed, t0.AddHours(2));
        var lastA = t0.AddHours(2).AddMinutes(30);
        SeedRun(db, "wf-a", RunStatus.AwaitingApproval, lastA);
        // wf-b: never terminated → success rate 0.
        SeedRun(db, "wf-b", RunStatus.AwaitingApproval, t0);

        var stats = await NewQueries(db).WorkflowStatsAsync();

        var a = Assert.Single(stats, s => s.Workflow == "wf-a");
        Assert.Equal(4, a.TotalRuns);
        Assert.Equal(2, a.Completed);
        Assert.Equal(1, a.Failed);
        Assert.Equal(1, a.AwaitingApproval);
        Assert.Equal(2d / 3d, a.SuccessRate, 5);
        Assert.Equal(lastA, a.LastRunAt);

        var b = Assert.Single(stats, s => s.Workflow == "wf-b");
        Assert.Equal(1, b.TotalRuns);
        Assert.Equal(0d, b.SuccessRate);
    }

    private static void SeedRun(
        IDbContextFactory<HarnessDbContext> factory, string workflow, RunStatus status, DateTimeOffset startedAt)
    {
        using var db = factory.CreateDbContext();
        db.Runs.Add(new Run
        {
            Workflow = workflow,
            WorkflowSha = "sha",
            Initiator = "alice",
            Repo = "acme/repo",
            Status = status,
            StartedAt = startedAt
        });
        db.SaveChanges();
    }
}
