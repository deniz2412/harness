using Harness.Audit;
using Harness.Contracts;
using Harness.Engine;
using Microsoft.EntityFrameworkCore;

namespace Harness.Api.Ops;

/// <summary>
/// The workflow-catalog read model behind the F2 launcher: everything the console shows when a user
/// browses the shipped workflows, opens one to see its DAG, or looks at how a workflow has fared over
/// its run history. Every method is a read — it loads local workflow/prompt/agent YAML through the
/// production <see cref="WorkflowLoader"/> (so the console sees exactly what a run would resolve,
/// agent_refs merged and all) and reads run outcomes through <see cref="IDbContextFactory{T}"/>. It
/// never writes, never reaches a model or a tool, and never crashes the catalog because one workflow
/// file is malformed — a workflow that fails to load is simply omitted from the listing.
/// </summary>
public interface IWorkflowCatalogQueries
{
    /// <summary>Every workflow under <c>workflows/</c> (flat, defaults, and every team namespace),
    /// each loaded and summarised for the catalog grid. A team override and the default it shadows are
    /// both surfaced (distinct <see cref="WorkflowScope"/>s), exactly as
    /// <see cref="WorkflowCatalog.EnumerateAll"/> reports them. A workflow that fails to load is
    /// omitted rather than throwing, so one broken file cannot blank the catalog.</summary>
    IReadOnlyList<WorkflowSummary> ListWorkflows();

    /// <summary>One workflow's full detail — its permission ceiling and every node with the
    /// topological <see cref="WorkflowNodeView.Layer"/> the UI lays the DAG out in columns by — keyed
    /// by the loader name a run targets (<c>pr-review</c> or <c>teams/&lt;team&gt;/pr-review</c>).
    /// Null if the name is not a catalogued workflow or fails to load.</summary>
    WorkflowDetailView? GetWorkflow(string loaderName);

    /// <summary>Per-workflow run history from the runs table, grouped by the loader name a run stored
    /// as its workflow: totals, terminal-outcome counts, a 0..1 success rate, and the last run time —
    /// the catalog grid's "how has this fared" column. Most-recently-run workflows first.</summary>
    Task<IReadOnlyList<WorkflowStat>> WorkflowStatsAsync(CancellationToken ct = default);
}

/// <summary>One row of the catalog grid.</summary>
/// <param name="LoaderName">What a run targets — bare (<c>pr-review</c>) or team-namespaced
/// (<c>teams/payments/pr-review</c>). The identity the stats join and <see cref="GetWorkflow"/> use.</param>
/// <param name="EndsAt">A short label of the terminal effect: "PR" if any node opens a PR, else
/// "comment" if any node posts a PR comment, else "—".</param>
/// <param name="HasHumanGate">True if any node is a <c>gate</c> requiring a human decision.</param>
public sealed record WorkflowSummary(
    string LoaderName, string Name, string? Team, WorkflowScope Scope, string? Description,
    int NodeCount, bool HasHumanGate, string EndsAt, string Sha);

/// <summary>A workflow and everything the detail / DAG view renders about it.</summary>
public sealed record WorkflowDetailView(
    string LoaderName, string Name, string? Team, WorkflowScope Scope, string? Description,
    IReadOnlyDictionary<string, string> Permissions, IReadOnlyList<WorkflowNodeView> Nodes, string Sha);

/// <summary>One node as the DAG view draws it. <paramref name="Tools"/>/<paramref name="PromptRef"/>/
/// <paramref name="ModelTier"/> are already merged from a named agent when <paramref name="AgentRef"/>
/// is set (the loader does that), so the UI shows the effective config plus "via agent X".</summary>
/// <param name="Layer">Topological depth — the longest dependency path from a root (roots = 0) — so
/// the UI can column the DAG without recomputing topology.</param>
public sealed record WorkflowNodeView(
    string Id, string Kind, IReadOnlyList<string> DependsOn, IReadOnlyList<string> Tools,
    string? Gate, string? AgentRef, string? ModelTier, string? PromptRef, string? OutputSchema, int Layer);

/// <summary>Per-workflow run history. <paramref name="SuccessRate"/> is Completed over terminal runs
/// (Completed+Failed+PolicyBlocked+Rejected) as a 0..1 double, 0 when nothing has terminated.</summary>
public sealed record WorkflowStat(
    string Workflow, int TotalRuns, int Completed, int Failed, int AwaitingApproval,
    double SuccessRate, DateTimeOffset? LastRunAt);

/// <summary>
/// Reads the workflow catalog. Discovers workflows via <see cref="WorkflowCatalog.EnumerateAll"/>
/// (a cheap file walk, no YAML parsed), loads each through the production <see cref="WorkflowLoader"/>
/// so agent_refs, prompt merges and the content sha are exactly what a run would see, and reads run
/// outcomes per call via <see cref="IDbContextFactory{T}"/> with <c>AsNoTracking</c>. Pure consumer:
/// no writes, no model, no network.
/// </summary>
public sealed class WorkflowCatalogQueries(
    WorkflowCatalog catalog, WorkflowLoader loader, string workflowsRoot,
    IDbContextFactory<HarnessDbContext> dbFactory) : IWorkflowCatalogQueries
{
    // Absolute, so relative-path derivation of a loader name from an enumerated file path is stable
    // regardless of how the root was passed in.
    private readonly string _workflowsRoot =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(workflowsRoot));

    public IReadOnlyList<WorkflowSummary> ListWorkflows()
    {
        var summaries = new List<WorkflowSummary>();
        foreach (var wref in catalog.EnumerateAll())
        {
            var loaderName = LoaderNameOf(wref.Path);
            // Fail-closed for the catalog, not for the whole page: a broken or unresolvable workflow
            // (missing prompt, unknown agent_ref, malformed YAML) is left out of the listing rather
            // than throwing — one bad file must not blank every other workflow.
            WorkflowDefinition wf;
            try { wf = loader.Load(loaderName); }
            catch (Exception ex) when (IsLoadFailure(ex)) { continue; }

            summaries.Add(new WorkflowSummary(
                loaderName, wf.Name, wref.Team, wref.Scope, wf.Description,
                wf.Nodes.Count, HasHumanGate(wf), EndsAt(wf), wf.Sha));
        }
        return summaries;
    }

    public WorkflowDetailView? GetWorkflow(string loaderName)
    {
        if (string.IsNullOrWhiteSpace(loaderName)) return null;

        // Only serve a name the catalog actually enumerates: this bounds GetWorkflow to real catalog
        // members (a traversal or an off-catalog path cannot be loaded via this seam) and hands us the
        // scope/team for free. The enumeration is a file walk, not a parse, so this stays cheap.
        var wref = catalog.EnumerateAll().FirstOrDefault(r => LoaderNameOf(r.Path) == loaderName);
        if (wref is null) return null;

        WorkflowDefinition wf;
        try { wf = loader.Load(loaderName); }
        catch (Exception ex) when (IsLoadFailure(ex)) { return null; }

        return new WorkflowDetailView(
            loaderName, wf.Name, wref.Team, wref.Scope, wf.Description,
            wf.Permissions, BuildNodeViews(wf), wf.Sha);
    }

    public async Task<IReadOnlyList<WorkflowStat>> WorkflowStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Project the three columns the stats need, then group in memory: correct across every status
        // and independent of the provider's GROUP BY translation.
        var rows = await db.Runs.AsNoTracking()
            .Select(r => new { r.Workflow, r.Status, r.StartedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Workflow, StringComparer.Ordinal)
            .Select(g =>
            {
                var completed = g.Count(x => x.Status == RunStatus.Completed);
                var failed = g.Count(x => x.Status == RunStatus.Failed);
                var awaiting = g.Count(x => x.Status == RunStatus.AwaitingApproval);
                var blocked = g.Count(x => x.Status == RunStatus.PolicyBlocked);
                var rejected = g.Count(x => x.Status == RunStatus.Rejected);
                var terminal = completed + failed + blocked + rejected;
                var rate = terminal == 0 ? 0d : completed / (double)terminal;
                return new WorkflowStat(
                    g.Key, g.Count(), completed, failed, awaiting, rate,
                    g.Max(x => x.StartedAt));
            })
            .OrderByDescending(s => s.LastRunAt)
            .ToList();
    }

    /// <summary>The loader name for an enumerated file: its path relative to the workflows root, minus
    /// <c>.yaml</c>, forward-slashed — the identity a run stores and <see cref="WorkflowLoader.Load"/>
    /// takes (the same derivation as the boot-time compliance sweep in Program.cs).</summary>
    private string LoaderNameOf(string absolutePath) =>
        Path.GetRelativePath(_workflowsRoot, absolutePath)[..^".yaml".Length].Replace('\\', '/');

    private static bool HasHumanGate(WorkflowDefinition wf) =>
        wf.Nodes.Any(n => n.Kind == "gate" && n.Gate == "human");

    // Terminal effect, most-visible first: a PR trumps a comment; a read-only workflow ends at "—".
    private static string EndsAt(WorkflowDefinition wf)
    {
        var tools = wf.Nodes.SelectMany(n => n.Tools).ToHashSet(StringComparer.Ordinal);
        if (tools.Contains("github.open_pr")) return "PR";
        if (tools.Contains("github.pr_comment")) return "comment";
        return "—";
    }

    private static IReadOnlyList<WorkflowNodeView> BuildNodeViews(WorkflowDefinition wf)
    {
        var byId = wf.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var cache = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);

        // Longest dependency path from a root, memoised. The loader already rejects unknown deps and a
        // DAG has no cycles, but guard defensively: a node seen twice on the current path returns 0
        // instead of recursing forever.
        int LayerOf(string id)
        {
            if (cache.TryGetValue(id, out var known)) return known;
            if (!byId.TryGetValue(id, out var node)) return 0;
            if (!onStack.Add(id)) return 0;

            var layer = node.DependsOn.Count == 0 ? 0 : node.DependsOn.Max(LayerOf) + 1;

            onStack.Remove(id);
            cache[id] = layer;
            return layer;
        }

        return wf.Nodes.Select(n => new WorkflowNodeView(
            n.Id, n.Kind, n.DependsOn.ToList(), n.Tools.ToList(), n.Gate, n.AgentRef,
            n.ModelTier, n.PromptRef, n.OutputSchema, LayerOf(n.Id))).ToList();
    }

    // The failure modes of loading a workflow — a missing file/prompt, malformed YAML (a
    // YamlDotNet.Core.YamlException), or a validation / agent-resolution failure — all surface as
    // exceptions from the loader. The catalog survives any one broken file, so this treats every
    // load-time exception as "omit this workflow" rather than naming YamlDotNet's exception type
    // here (Harness.Api does not reference that package directly). Cancellation is not raised on this
    // synchronous, in-process path, so there is nothing thread-control to re-surface.
    private static bool IsLoadFailure(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException);
}
