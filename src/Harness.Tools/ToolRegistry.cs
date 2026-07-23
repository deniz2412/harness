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
    GitHubToolset github, RepoToolset repo, PolicyPipeline policy, AuditEmitter audit)
{
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
    /// The catalog itself. There is no merge operation and no repo create/delete operation, and
    /// none may ever be added (invariant 1) — workflows end at opening a PR.
    /// The write tools close over the run's sandbox from <paramref name="ctx"/>, so the agent's
    /// view of a write tool is only its content (branch, message, path…) — never which worktree it
    /// acts on. A write tool resolved onto a node with no runner attached fails closed right here.
    /// </summary>
    private AIFunction Build(string name, ToolCallContext ctx) => name.ToLowerInvariant() switch
    {
        "github.pr_diff"    => AIFunctionFactory.Create(github.GetPrDiff,     "github_pr_diff",    "Get the unified diff of a pull request."),
        "github.pr_comment" => AIFunctionFactory.Create(github.PostPrComment, "github_pr_comment", "Post a review comment on a pull request."),
        "github.get_issue"  => AIFunctionFactory.Create(github.GetIssue,      "github_get_issue",  "Read a GitHub issue title and body."),
        "github.issue_comment" => AIFunctionFactory.Create(github.IssueComment, "github_issue_comment", "Post a comment on a GitHub issue."),
        "repo.read"         => AIFunctionFactory.Create(repo.ReadFile,        "repo_read_file",    "Read a file from the repository worktree."),
        "repo.list"         => AIFunctionFactory.Create(repo.ListFiles,       "repo_list_files",   "List files in the repository worktree."),
        "codesearch.query"  => AIFunctionFactory.Create(repo.Search,          "codesearch_query",  "Search the repository for a term."),

        // Write tools: the runner is injected by the closure, never exposed to the agent. `?? throw`
        // (not `!`) is the primary fail-closed guard — a write with no isolated worktree does not
        // happen; the toolset methods self-guard too, as defence in depth.
        "github.open_pr"    => AIFunctionFactory.Create(
            (int? issue, string head, string @base, string title, string body) =>
                github.OpenPr(issue, head, @base, title, body),
            "github_open_pr", "Open a pull request from a pushed branch. The workflow ends here; there is no merge."),
        "github.push_branch" => AIFunctionFactory.Create(
            (string branch, string message) => github.PushBranch(
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
