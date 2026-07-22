using Octokit;

namespace Harness.Tools;

/// <summary>
/// GitHub operations exposed to agents. Deliberately narrow: THERE IS NO MERGE OPERATION,
/// and none may ever be added — workflows end at opening a PR.
/// </summary>
public sealed class GitHubToolset(IGitHubClient client, string owner, string repo)
{
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

    public async Task<string> GetIssue(int issueNumber)
    {
        var issue = await client.Issue.Get(owner, repo, issueNumber);
        return $"#{issue.Number} {issue.Title}\n\n{issue.Body}";
    }
}
