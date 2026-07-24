using Harness.Audit;
using Harness.Policy;
using Microsoft.Extensions.AI;

namespace Harness.Tools;

/// <summary>
/// The curated tool catalog (invariant 7): maps declarative tool names from workflow YAML to the
/// AIFunctions an agent may call. Nothing reaches an agent except through here, and nothing leaves
/// here unwrapped — every resolved tool is an <see cref="AuditedTool"/>, which is what makes the
/// pre-tool policy check, the outbound scan and the audit event unavoidable rather than optional.
/// </summary>
public sealed class ToolRegistry(
    GitHubToolsetFactory githubFactory, IReadOnlyCollection<string> repoAllowlist,
    RepoToolset repo, PolicyPipeline policy, AuditEmitter audit,
    IReadOnlyDictionary<string, IMcpConnector>? connectors = null)
{
    // M7c: declared external toolsets, keyed by namespace (empty ⇒ none mounted, the fail-closed
    // default). The composition root builds these from connectors.yaml; a real MCP client swaps in
    // behind IMcpConnector without changing this class.
    private readonly IReadOnlyDictionary<string, IMcpConnector> _connectors =
        connectors ?? new Dictionary<string, IMcpConnector>();

    public IList<AITool> Resolve(ToolCallContext ctx)
    {
        var tools = new List<AITool>();
        foreach (var name in ctx.NodeTools)
        {
            // Resolve-time check: a workflow that asks for more than its ceiling allows fails
            // before the agent runs, not after it has already spent a model call discovering it.
            policy.AssertToolAllowed(name, ctx.NodeTools, ctx.WorkflowPermissions);
            tools.Add(new AuditedTool(Build(name, ctx), name, ctx, policy, audit));
        }
        return tools;
    }

    /// <summary>
    /// Read tools act on the run's own worktree when there is one, so a write workflow reads back
    /// exactly the tree it clones and writes into — not the shared root, and never another run's
    /// worktree. A read-only workflow (no runner) keeps the singleton over the shared root, which is
    /// the unchanged pr-review behaviour. This is the read-side twin of the write tools' scoping.
    /// </summary>
    private RepoToolset RepoFor(ToolCallContext ctx) =>
        ctx.Runner is null ? repo : new RepoToolset(ctx.Runner.WorktreePath);

    /// <summary>
    /// The catalog itself. There is no merge operation and no repo create/delete operation, and
    /// none may ever be added (invariant 1) — workflows end at opening a PR.
    /// The write tools close over the run's sandbox from <paramref name="ctx"/>, so the agent's
    /// view of a write tool is only its content (branch, message, path…) — never which worktree it
    /// acts on. A write tool resolved onto a node with no runner attached fails closed right here.
    /// </summary>
    private AIFunction Build(string name, ToolCallContext ctx)
    {
        // M7c: a declared connector tool (namespace.operation) mounts via its IMcpConnector. The
        // per-tool policy check (connector allowlist + write-capable boundary) already ran in Resolve
        // via policy.AssertToolAllowed, and the AuditedTool wrapper there logs + scans every call — so
        // a mounted op is governed and audited exactly like a built-in. The connector's advertised
        // Operations are a fail-closed second gate. Built-ins fall through (their reserved namespaces
        // are never declared connectors, so the lookup misses).
        if (McpConnectorRegistry.TryParseToolName(name, out var ns, out var op)
            && ns is not null && op is not null
            && _connectors.TryGetValue(ns, out var connector))
        {
            if (!connector.Operations.Any(o => string.Equals(o, op, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Connector '{ns}' does not serve operation '{op}'.");
            return AIFunctionFactory.Create(
                (string argumentsJson) => connector.InvokeAsync(op, argumentsJson, CancellationToken.None),
                $"{ns}_{op}", $"MCP {ns}.{op} (external toolset, mounted via the connector layer).");
        }

        var scoped = RepoFor(ctx);   // per-run worktree for write workflows; shared root otherwise
        // Per-run GitHub binding (M3): the toolset targets the run's own repo, not a single
        // startup-configured one. Built lazily, only when a github.* tool is actually resolved — a
        // repo-only node never needs it. The allowlist has already been enforced (before any tool
        // runs), so by here ctx.Repo is a permitted repo; a null repo is a wiring error.
        GitHubToolset Github() => githubFactory.ForRepo(
            ctx.Repo ?? throw new InvalidOperationException(
                "A run must target a repo (ctx.Repo) to use github.* tools."));
        return name.ToLowerInvariant() switch
        {
        "github.pr_diff"    => AIFunctionFactory.Create(Github().GetPrDiff,     "github_pr_diff",    "Get the unified diff of a pull request."),
        "github.pr_comment" => AIFunctionFactory.Create(Github().PostPrComment, "github_pr_comment", "Post a review comment on a pull request."),
        "github.get_issue"  => AIFunctionFactory.Create(Github().GetIssue,      "github_get_issue",  "Read a GitHub issue title and body."),
        "github.issue_comment" => AIFunctionFactory.Create(Github().IssueComment, "github_issue_comment", "Post a comment on a GitHub issue."),

        // Cross-repo search (M3): read-only, and confined to the allowlist — the agent supplies only
        // the query; the permitted repo scope is closed over here, so search can never reach outside
        // the policy boundary even though it is not bound to ctx.Repo.
        "github.search_code"  => AIFunctionFactory.Create(
            (string query) => Github().SearchCode(repoAllowlist, query),
            "github_search_code", "Search code across the allowlisted repositories (read-only)."),
        "github.search_repos" => AIFunctionFactory.Create(
            (string query) => Github().SearchRepos(repoAllowlist, query),
            "github_search_repos", "Search the allowlisted repositories by name and description (read-only)."),

        "repo.read"         => AIFunctionFactory.Create(scoped.ReadFile,      "repo_read_file",    "Read a file from the repository worktree."),
        "repo.list"         => AIFunctionFactory.Create(scoped.ListFiles,     "repo_list_files",   "List files in the repository worktree."),
        "codesearch.query"  => AIFunctionFactory.Create(scoped.Search,        "codesearch_query",  "Search the repository for a term."),

        // Write tools: the runner is injected by the closure, never exposed to the agent. `?? throw`
        // (not `!`) is the primary fail-closed guard — a write with no isolated worktree does not
        // happen; the toolset methods self-guard too, as defence in depth.
        "github.open_pr"    => AIFunctionFactory.Create(
            (int? issue, string head, string @base, string title, string body) =>
                Github().OpenPr(issue, head, @base, title, body),
            "github_open_pr", "Open a pull request from a pushed branch. The workflow ends here; there is no merge."),
        "github.push_branch" => AIFunctionFactory.Create(
            (string branch, string message) => Github().PushBranch(
                ctx.Runner ?? throw new InvalidOperationException(
                    "push_branch requires an isolated runner worktree; none is attached to this node."),
                branch, message),
            "github_push_branch", "Commit the worktree changes onto a new branch and push it."),
        "repo.write_worktree" => AIFunctionFactory.Create(
            (string path, string content) => repo.WriteFile(
                (ctx.Runner ?? throw new InvalidOperationException(
                    "repo.write_worktree requires an isolated runner worktree; none is attached to this node."))
                    .WorktreePath,
                path, content),
            "repo_write_worktree", "Write a file inside the run's isolated worktree."),

        _ => throw new InvalidOperationException($"Unknown tool '{name}'.")
        };
    }
}
