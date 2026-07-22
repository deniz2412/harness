using Harness.Policy;
using Microsoft.Extensions.AI;

namespace Harness.Tools;

/// <summary>
/// Maps declarative tool names (workflow YAML) to AIFunctions handed to the agent.
/// Every tool call is policy-checked and audited by the caller (Harness.Agents middleware).
/// </summary>
public sealed class ToolRegistry(GitHubToolset github, RepoToolset repo, PolicyPipeline policy)
{
    public IList<AITool> Resolve(IReadOnlyCollection<string> toolNames)
    {
        var tools = new List<AITool>();
        foreach (var name in toolNames)
        {
            policy.AssertToolAllowed(name, toolNames);
            tools.Add(name.ToLowerInvariant() switch
            {
                "github.pr_diff"    => AIFunctionFactory.Create(github.GetPrDiff,     "github_pr_diff",    "Get the unified diff of a pull request."),
                "github.pr_comment" => AIFunctionFactory.Create(github.PostPrComment, "github_pr_comment", "Post a review comment on a pull request."),
                "github.get_issue"  => AIFunctionFactory.Create(github.GetIssue,      "github_get_issue",  "Read a GitHub issue title and body."),
                "repo.read"         => AIFunctionFactory.Create(repo.ReadFile,        "repo_read_file",    "Read a file from the repository worktree."),
                "repo.list"         => AIFunctionFactory.Create(repo.ListFiles,       "repo_list_files",   "List files in the repository worktree."),
                "codesearch.query"  => AIFunctionFactory.Create(repo.Search,          "codesearch_query",  "Search the repository for a term."),
                _ => throw new InvalidOperationException($"Unknown tool '{name}'.")
            });
        }
        return tools;
    }
}
