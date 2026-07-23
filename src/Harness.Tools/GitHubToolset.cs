using System.Text;
using Harness.Contracts;
using Octokit;

namespace Harness.Tools;

/// <summary>
/// GitHub operations exposed to agents. Deliberately narrow: THERE IS NO MERGE OPERATION,
/// and none may ever be added — workflows end at opening a PR (invariant 1). The write surface
/// (<see cref="PushBranch"/>, <see cref="OpenPr"/>, <see cref="IssueComment"/>) reaches exactly as
/// far as opening a PR and commenting; there is no call to <c>PullRequest.Merge</c> anywhere in this
/// type, and no repository create/delete call either.
/// </summary>
public sealed class GitHubToolset(IGitHubClient client, string owner, string repo)
{
    // Non-interactive commit identity for pushes the harness makes on the initiator's behalf. Not a
    // secret and not the credential: the push itself reuses the token the runner cloned with.
    private const string CommitEmail = "harness@users.noreply.github.com";
    private const string CommitName = "Harness";

    public async Task<string> GetPrDiff(int prNumber)
    {
        var files = await client.PullRequest.Files(owner, repo, prNumber);
        return string.Join("\n\n", files.Select(f => $"--- {f.FileName} ({f.Status})\n{f.Patch}"));
    }

    public async Task<string> PostPrComment(int prNumber, string body)
    {
        var comment = await client.Issue.Comment.Create(owner, repo, prNumber, body);
        return comment.HtmlUrl;
    }

    /// <summary>
    /// Post a comment on an issue. The issue-facing sibling of <see cref="PostPrComment"/> — GitHub
    /// backs both with the same issue-comment endpoint, but the two stay separate catalogued tools so
    /// a workflow's permission ceiling can grant one without the other.
    /// </summary>
    public async Task<string> IssueComment(int issueNumber, string body)
    {
        var comment = await client.Issue.Comment.Create(owner, repo, issueNumber, body);
        return comment.HtmlUrl;
    }

    public async Task<string> GetIssue(int issueNumber)
    {
        var issue = await client.Issue.Get(owner, repo, issueNumber);
        return $"#{issue.Number} {issue.Title}\n\n{issue.Body}";
    }

    /// <summary>
    /// Commit every change in the run's worktree onto a new branch and push it. The git work happens
    /// inside the run's sandbox (<paramref name="runner"/>); each step is a separate argv invocation
    /// — no shell, no <c>&amp;&amp;</c> — so there is no shell-injection surface, and the first
    /// non-zero exit fails the whole push closed (invariant 2) with the git stderr surfaced. The
    /// clone the runner made carries the platform token, so <c>git push</c> reuses that credential;
    /// this toolset never handles the token itself (invariant 3).
    /// </summary>
    public async Task<string> PushBranch(IRunnerSession runner, string branch, string message)
    {
        // Fail-closed: a push with no isolated worktree does not happen. The ToolRegistry closure is
        // the primary guard (it supplies `ctx.Runner ?? throw`); this is defence in depth.
        ArgumentNullException.ThrowIfNull(runner);

        // The runner tokenises a command string into argv: whitespace separates tokens, and a
        // double-quoted span is one token with the quotes stripped (no escaping). So a multi-word
        // commit subject must be quoted to survive as a single -m argument, and any embedded quote
        // is removed so it cannot unbalance the tokeniser (which refuses an unbalanced quote). A
        // stray newline is folded to a space for the same reason.
        var subject = message.Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "").Trim();
        if (subject.Length == 0) subject = "Harness change";

        await RunOrThrow(runner, $"git checkout -b {branch}", "create the branch");
        await RunOrThrow(runner, "git add -A", "stage the worktree changes");
        await RunOrThrow(runner,
            $"git -c user.email={CommitEmail} -c user.name={CommitName} commit -m \"{subject}\"",
            "commit the changes");
        await RunOrThrow(runner, $"git push origin {branch}", "push the branch");
        return branch;
    }

    /// <summary>
    /// Open a pull request from <paramref name="head"/> into <paramref name="base"/>. This is where
    /// every write workflow ENDS: there is deliberately no follow-on merge — GitHub decides nothing
    /// here, a human does. If an originating <paramref name="issue"/> is given it is referenced with
    /// a "Closes #N" line so the merge (whenever a human performs it) links the two; this tool does
    /// not itself close, merge, or otherwise mutate the issue.
    /// </summary>
    public async Task<string> OpenPr(int? issue, string head, string @base, string title, string body)
    {
        var prBody = issue is int n ? $"{body}\n\nCloses #{n}" : body;
        var pr = await client.PullRequest.Create(owner, repo, new NewPullRequest(title, head, @base) { Body = prBody });
        return pr.HtmlUrl;
    }

    // ---------- Read-only cross-repo search (M3) ----------
    //
    // These two are the only cross-repo surface on this toolset: they ignore the instance's bound
    // owner/repo and operate purely on the `repoScope` the orchestrator injects (the allowlisted
    // repos — the policy boundary, workstream 1). They live here, not on the factory, so there is one
    // GitHub surface and the ToolRegistry resolves them off the same per-run instance as every other
    // github.* tool. They are READ-ONLY (invariant 1): they call the Search API and change nothing —
    // no create, no delete, no merge. Results are untrusted data (invariant 4): they are summarised
    // and returned, never acted on. Fail-closed (invariant 2): an empty scope or blank query yields a
    // clear refusal, and any API failure is turned into a message — never an exception the MAF
    // function loop could spin on. Output is bounded so a huge result set cannot blow up a model call.

    /// <summary>Hard cap on results surfaced from a single search — bounds the model-bound payload.</summary>
    private const int MaxResults = 30;

    /// <summary>Hard cap on total characters returned — a second bound so long paths/descriptions can't blow the budget.</summary>
    private const int MaxChars = 8000;

    /// <summary>
    /// Search code across <paramref name="repoScope"/> (each entry <c>owner/name</c>) for
    /// <paramref name="query"/>. Results are confined to the scope twice over: the request carries a
    /// <c>repo:</c> qualifier per scoped repo, and the returned items are then filtered to the exact
    /// allowlisted full names (defence in depth — a scope leak cannot surface a foreign repo). An
    /// empty scope means nothing is searchable and returns an empty result, never "search everything".
    /// </summary>
    public async Task<string> SearchCode(IReadOnlyCollection<string> repoScope, string query)
    {
        var scope = NormaliseScope(repoScope);
        if (scope.Count == 0) return "No repositories are in scope to search; nothing is searchable.";
        if (string.IsNullOrWhiteSpace(query)) return "Provide a non-empty search query.";

        var request = new SearchCodeRequest(query.Trim()) { PerPage = MaxResults };
        var repos = new RepositoryCollection();
        foreach (var full in scope) repos.Add(full);   // Add("owner/name") → a repo: qualifier
        request.Repos = repos;

        SearchCodeResult result;
        try
        {
            result = await client.Search.SearchCode(request);
        }
        catch (Exception ex)
        {
            // Fail-closed as a refusal, not an exception the agent loop retries: read-only search that
            // cannot run returns nothing actionable. (Octokit exceptions carry no credential.)
            return $"Code search could not be completed: {ex.Message}";
        }

        var hits = (result.Items ?? [])
            .Where(i => i.Repository?.FullName is string fn && scope.Contains(fn))
            .Take(MaxResults)
            .ToList();
        if (hits.Count == 0)
            return $"No code matches for \"{query.Trim()}\" in the {scope.Count} in-scope repositor{(scope.Count == 1 ? "y" : "ies")}.";

        var sb = new StringBuilder();
        sb.Append(hits.Count).Append(hits.Count == 1 ? " code match" : " code matches");
        if (result.TotalCount > hits.Count) sb.Append(" (showing the first ").Append(hits.Count).Append(" of ").Append(result.TotalCount).Append(')');
        sb.AppendLine(":");
        foreach (var h in hits)
            sb.Append("- ").Append(h.Repository.FullName).Append(" · ").AppendLine(h.Path);
        return Bound(sb.ToString());
    }

    /// <summary>
    /// Search repositories within <paramref name="repoScope"/> for <paramref name="query"/>, returning
    /// name, description and default branch. The request is narrowed with a <c>user:</c> qualifier per
    /// distinct owner in the scope, and results are then filtered to the exact allowlisted full names
    /// (defence in depth). An empty scope returns an empty result.
    /// </summary>
    public async Task<string> SearchRepos(IReadOnlyCollection<string> repoScope, string query)
    {
        var scope = NormaliseScope(repoScope);
        if (scope.Count == 0) return "No repositories are in scope to search; nothing is searchable.";
        if (string.IsNullOrWhiteSpace(query)) return "Provide a non-empty search query.";

        // Narrow the API call to the owners the scope covers; the exact-full-name filter below is the
        // actual confinement boundary.
        var owners = scope.Select(f => f.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase);
        var term = query.Trim() + " " + string.Join(" ", owners.Select(o => $"user:{o}"));
        var request = new SearchRepositoriesRequest(term) { PerPage = MaxResults };

        SearchRepositoryResult result;
        try
        {
            result = await client.Search.SearchRepo(request);
        }
        catch (Exception ex)
        {
            return $"Repository search could not be completed: {ex.Message}";
        }

        var hits = (result.Items ?? [])
            .Where(r => r.FullName is string fn && scope.Contains(fn))
            .Take(MaxResults)
            .ToList();
        if (hits.Count == 0)
            return $"No repositories match \"{query.Trim()}\" within the {scope.Count} in-scope repositor{(scope.Count == 1 ? "y" : "ies")}.";

        var sb = new StringBuilder();
        sb.Append(hits.Count).AppendLine(hits.Count == 1 ? " repository:" : " repositories:");
        foreach (var r in hits)
        {
            var desc = string.IsNullOrWhiteSpace(r.Description) ? "(no description)" : r.Description!.Trim();
            var branch = string.IsNullOrWhiteSpace(r.DefaultBranch) ? "?" : r.DefaultBranch;
            sb.Append("- ").Append(r.FullName).Append(" (default: ").Append(branch).Append(") — ").AppendLine(desc);
        }
        return Bound(sb.ToString());
    }

    /// <summary>Trims blanks, de-dupes case-insensitively, and keeps only well-formed <c>owner/name</c> entries.</summary>
    private static HashSet<string> NormaliseScope(IReadOnlyCollection<string>? repoScope)
    {
        var scope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (repoScope is null) return scope;
        foreach (var raw in repoScope)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var full = raw.Trim();
            var parts = full.Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) continue;
            scope.Add(full);
        }
        return scope;
    }

    /// <summary>Caps the total size of a search summary so a large result set cannot blow up a model call.</summary>
    private static string Bound(string text) =>
        text.Length <= MaxChars ? text.TrimEnd() : text[..MaxChars].TrimEnd() + "\n… (truncated)";

    private static async Task RunOrThrow(IRunnerSession runner, string command, string what)
    {
        var result = await runner.RunAsync(command, CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(
                $"push_branch could not {what}: `{command}` exited {result.ExitCode}. {result.Stderr}".Trim());
    }
}
