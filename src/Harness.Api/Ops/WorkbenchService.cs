using Harness.Contracts;
using Harness.Engine;
using Harness.Policy;

namespace Harness.Api.Ops;

/// <summary>
/// The F3 authoring-workbench validation seam: given workflow YAML <em>text</em> straight from the
/// editor, validate it the way a real run would — structure, prompt/agent resolution, the curated
/// tool catalog, the MCP connector allowlist and the org policy floor — and produce a dry-run DAG for
/// the editor to render. The safety crux (product-vision §5): authored YAML is VALIDATED but NEVER
/// EXECUTED from the editor, and it is validated against exactly the floor + catalog a real run
/// enforces. This service adds no capability (invariants 1/2), reaches no model (invariant 3), treats
/// the YAML as untrusted author input it only reads (invariant 4), and writes nothing — the
/// "publish path" it reports is a preview string, not a write (invariant 5).
/// </summary>
public interface IWorkbenchService
{
    /// <summary>
    /// Validate editor YAML without running it. Never throws out of validation: a malformed or
    /// structurally invalid draft comes back as <see cref="WorkbenchResult.Ok"/> = false with the
    /// failure carried in <see cref="WorkbenchResult.Issues"/>. Structural/parse errors are surfaced
    /// one at a time (the loader throws on the first), so on a parse/structure failure there is a
    /// single error issue and <see cref="WorkbenchResult.Dag"/> is null; once the draft parses and
    /// structurally validates, all catalog- and floor-level issues are collected together and the DAG
    /// is populated alongside them so the editor can render the graph next to the errors.
    /// </summary>
    /// <param name="yaml">The workflow YAML text from the editor.</param>
    /// <param name="team">The team namespace to resolve <c>agent_ref</c>s within, as a run would; null
    /// for the org-default namespace.</param>
    WorkbenchResult Validate(string yaml, string? team = null);
}

/// <summary>The outcome of validating an editor draft.</summary>
/// <param name="Ok">True when there are no error-severity issues.</param>
/// <param name="Issues">Every issue found (errors and warnings). A parse/structure failure yields a
/// single error; catalog/floor checks on a parsed draft yield all of theirs together.</param>
/// <param name="Dag">The dry-run DAG — populated whenever the draft parsed and structurally
/// validated (even if it then failed catalog/floor checks), null when it did not parse.</param>
/// <param name="PublishPath">The path a publish WOULD write to — a preview only; nothing is written.
/// Null when the draft did not parse (there is no name to compute it from).</param>
public sealed record WorkbenchResult(
    bool Ok, IReadOnlyList<WorkbenchIssue> Issues, WorkbenchDag? Dag, string? PublishPath);

/// <summary>One problem with a draft.</summary>
/// <param name="Severity">"error" (blocks Ok) or "warning".</param>
/// <param name="Message">A safe, human-readable description.</param>
/// <param name="NodeId">The offending node, when the issue is node-scoped; null for whole-workflow issues.</param>
public sealed record WorkbenchIssue(string Severity, string Message, string? NodeId);

/// <summary>The dry-run DAG the editor renders: the workflow name, its permission ceiling, and every
/// node with a topological layer.</summary>
public sealed record WorkbenchDag(
    string Name, IReadOnlyDictionary<string, string> Permissions, IReadOnlyList<WorkbenchNode> Nodes);

/// <summary>One node of the dry-run DAG. Tools/AgentRef/ModelTier are the effective, post-agent-merge
/// values (the loader merges a referenced agent's config onto the node), exactly what a run would see.</summary>
/// <param name="Layer">Topological depth — the longest dependency path from a root (roots = 0) — so
/// the editor can column the DAG without recomputing topology.</param>
public sealed record WorkbenchNode(
    string Id, string Kind, IReadOnlyList<string> DependsOn, IReadOnlyList<string> Tools,
    string? Gate, string? AgentRef, string? ModelTier, int Layer);

/// <summary>
/// Validates editor drafts against the same guardrails a run enforces. Composes the production
/// <see cref="WorkflowLoader"/> (structure + prompt/agent resolution), <see cref="ToolCatalog"/> +
/// <see cref="McpConnectorRegistry"/> (curated-tool / declared-connector membership) and
/// <see cref="PolicyFloorValidator"/> (the org floor). Pure and offline: no model, no tool, no
/// network, no write.
/// </summary>
public sealed class WorkbenchService(
    WorkflowLoader loader, PolicyFloorValidator floor, McpConnectorRegistry connectors)
    : IWorkbenchService
{
    private const string DraftName = "workbench-draft";

    public WorkbenchResult Validate(string yaml, string? team = null)
    {
        var issues = new List<WorkbenchIssue>();

        // 1. Blank input: nothing to parse, nothing to render.
        if (string.IsNullOrWhiteSpace(yaml))
        {
            issues.Add(new WorkbenchIssue("error", "the workflow is empty", null));
            return new WorkbenchResult(false, issues, Dag: null, PublishPath: null);
        }

        // 2. Parse + structural validation + prompt/agent resolution, via the SAME loader a run uses.
        //    The loader fail-closes on the FIRST structural/resolution problem (a duplicate node id, a
        //    depends_on target that doesn't exist, an agent node missing prompt_ref/agent_ref, a bad
        //    gate value, a dangling prompt_ref/agent_ref, malformed YAML), so a structural failure is
        //    surfaced one at a time as a single error issue with no DAG — the caller fixes it and
        //    re-validates to reach the catalog/floor checks below.
        //    Harness.Api does not reference YamlDotNet directly, so rather than name YamlException we
        //    treat any load-time exception as the draft's failure — except the two truly fatal
        //    conditions, which are re-thrown (mirroring WorkflowCatalogQueries.IsLoadFailure).
        WorkflowDefinition wf;
        try
        {
            wf = loader.LoadFromText(yaml, DraftName, team);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            issues.Add(new WorkbenchIssue("error", ex.Message, null));
            return new WorkbenchResult(false, issues, Dag: null, PublishPath: null);
        }

        // 3. The draft parsed and structurally validated. Collect ALL remaining issues — the catalog /
        //    connector membership of every tool, then the org policy floor — and build the DAG. The DAG
        //    is populated even when there are catalog/floor errors, so the editor renders the graph
        //    beside the errors.
        CollectCatalogIssues(wf, issues);
        CollectFloorIssues(wf, issues);
        CollectCycleIssues(wf, issues);

        var dag = BuildDag(wf);
        var publishPath = PublishPathFor(wf.Name, team);
        var ok = !issues.Any(i => i.Severity == "error");

        return new WorkbenchResult(ok, issues, dag, publishPath);
    }

    /// <summary>
    /// Every tool named by every node must be a curated built-in (<see cref="ToolCatalog.Default"/>)
    /// OR a declared+allowed MCP connector operation. This is exactly the curated-tool guardrail a run
    /// enforces — a workbench that let an author name an un-catalogued/un-declared tool would be a hole
    /// (invariant 7). Tools are the effective, post-agent-merge tools the loader put on each node.
    /// </summary>
    private void CollectCatalogIssues(WorkflowDefinition wf, List<WorkbenchIssue> issues)
    {
        var catalog = ToolCatalog.Default;
        foreach (var node in wf.Nodes)
        {
            foreach (var tool in node.Tools)
            {
                var isBuiltin = catalog.TryGetTool(tool, out _);
                var isConnector = McpConnectorRegistry.TryParseToolName(tool, out var ns, out var op)
                                  && connectors.IsAllowed(ns, op);
                if (!isBuiltin && !isConnector)
                    issues.Add(new WorkbenchIssue(
                        "error",
                        $"tool '{tool}' is not a curated tool or a declared connector operation",
                        node.Id));
            }
        }
    }

    /// <summary>Maps every org-policy-floor violation (tool-ceiling breaches, missing human gates) to
    /// an error issue on its node — the same floor a real load is validated against.</summary>
    private void CollectFloorIssues(WorkflowDefinition wf, List<WorkbenchIssue> issues)
    {
        foreach (var v in floor.Validate(wf))
            issues.Add(new WorkbenchIssue("error", v.Message, v.NodeId));
    }

    /// <summary>
    /// Flags a dependency cycle. The loader's structural validation rejects duplicate ids and dangling
    /// deps but NOT cycles — only the executor's topological sort does, at run time. Catching it here
    /// means the workbench's "valid" is a genuine run-acceptance guarantee, not just "it parses".
    /// Reports the first cycle found (DFS on the depends_on edges, on-stack back-edge = cycle).
    /// </summary>
    private static void CollectCycleIssues(WorkflowDefinition wf, List<WorkbenchIssue> issues)
    {
        var byId = wf.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 0 unseen, 1 on-stack, 2 done
        var path = new List<string>();
        WorkbenchIssue? cycle = null;

        bool Visit(string id)
        {
            state[id] = 1;
            path.Add(id);
            if (byId.TryGetValue(id, out var node))
                foreach (var dep in node.DependsOn)
                {
                    if (state.GetValueOrDefault(dep) == 1)
                    {
                        var loop = path.Skip(path.IndexOf(dep)).Append(dep);
                        cycle = new WorkbenchIssue(
                            "error",
                            $"dependency cycle: {string.Join(" → ", loop)} — a run would reject this",
                            dep);
                        return true;
                    }
                    if (state.GetValueOrDefault(dep) == 0 && Visit(dep)) return true;
                }
            state[id] = 2;
            path.RemoveAt(path.Count - 1);
            return false;
        }

        foreach (var n in wf.Nodes)
            if (state.GetValueOrDefault(n.Id) == 0 && Visit(n.Id)) break;

        if (cycle is not null) issues.Add(cycle);
    }

    private static WorkbenchDag BuildDag(WorkflowDefinition wf)
    {
        var byId = wf.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var cache = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);

        // Longest dependency path from a root, memoised. The loader already rejects unknown deps and a
        // DAG has no cycles, but guard defensively: a node seen twice on the current path returns 0
        // rather than recursing forever (mirrors WorkflowCatalogQueries.BuildNodeViews).
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

        var nodes = wf.Nodes.Select(n => new WorkbenchNode(
            n.Id, n.Kind, n.DependsOn.ToList(), n.Tools.ToList(),
            n.Gate, n.AgentRef, n.ModelTier, LayerOf(n.Id))).ToList();

        return new WorkbenchDag(wf.Name, new Dictionary<string, string>(wf.Permissions), nodes);
    }

    /// <summary>The path a publish WOULD write to — a preview string only, nothing is written. A
    /// team-namespaced draft targets <c>workflows/teams/&lt;team&gt;/&lt;name&gt;.yaml</c>; an
    /// org-default draft targets <c>workflows/&lt;name&gt;.yaml</c>.</summary>
    private static string PublishPathFor(string name, string? team) =>
        string.IsNullOrWhiteSpace(team)
            ? $"workflows/{name}.yaml"
            : $"workflows/teams/{team.Trim()}/{name}.yaml";
}
