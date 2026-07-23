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

    private static async Task RunOrThrow(IRunnerSession runner, string command, string what)
    {
        var result = await runner.RunAsync(command, CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(
                $"push_branch could not {what}: `{command}` exited {result.ExitCode}. {result.Stderr}".Trim());
    }
}
