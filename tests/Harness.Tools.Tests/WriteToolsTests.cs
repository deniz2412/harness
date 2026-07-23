using Harness.Policy;
using Harness.Tools.Tests.Fakes;
using Octokit;
using Xunit;

namespace Harness.Tools.Tests;

/// <summary>
/// The M2 write tools in isolation, fully offline: no network, no GitHub, no runner process. These
/// prove the guarantees that matter for a write path — writes stay inside the worktree, a failed git
/// step fails the push closed, and the whole surface stops at opening a PR (no merge is ever issued,
/// through git or the API).
/// </summary>
public sealed class WriteToolsTests : IDisposable
{
    private readonly string _worktree =
        Path.Combine(Path.GetTempPath(), "harness-write-tests", Guid.NewGuid().ToString("N"));

    public WriteToolsTests() => Directory.CreateDirectory(_worktree);

    // ---------- helpers ----------

    private static (GitHubToolset gh, FakePullRequestsClient prs, FakeIssueCommentsClient comments) Github()
    {
        var comments = new FakeIssueCommentsClient { ReturnHtmlUrl = "https://github.com/o/r/issues/9#issuecomment-42" };
        var prs = new FakePullRequestsClient { ReturnHtmlUrl = "https://github.com/o/r/pull/7" };
        var client = new FakeGitHubClient(new FakeIssuesClient(comments), prs);
        return (new GitHubToolset(client, "o", "r"), prs, comments);
    }

    // ---------- RepoToolset.WriteFile ----------

    [Fact]
    public async Task WriteFile_writes_content_into_the_worktree()
    {
        var repo = new RepoToolset(_worktree);
        var result = await repo.WriteFile(_worktree, "src/app.cs", "class App {}");

        Assert.Equal("class App {}", await File.ReadAllTextAsync(Path.Combine(_worktree, "src", "app.cs")));
        Assert.Contains("12 bytes", result);          // UTF-8 byte count of the content
        Assert.Contains("src/app.cs", result);
    }

    [Fact]
    public async Task WriteFile_creates_missing_parent_directories()
    {
        var repo = new RepoToolset(_worktree);
        await repo.WriteFile(_worktree, "a/b/c/deep.txt", "hi");

        Assert.True(File.Exists(Path.Combine(_worktree, "a", "b", "c", "deep.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../outside.txt")]
    public async Task WriteFile_rejects_paths_that_escape_the_worktree(string escaping)
    {
        var repo = new RepoToolset(_worktree);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => repo.WriteFile(_worktree, escaping, "owned"));

        // A rejected traversal must not have created a sibling artefact either.
        Assert.False(File.Exists(Path.Combine(_worktree, escaping)));
    }

    [Fact]
    public async Task WriteFile_rejects_a_sibling_directory_that_merely_shares_the_prefix()
    {
        // The M1 separator fix: "…/wt" must not admit "…/wt-evil". Point the write at a nested root
        // and try to climb into an adjacent, prefix-sharing directory.
        var root = Path.Combine(_worktree, "wt");
        Directory.CreateDirectory(root);
        var repo = new RepoToolset(root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => repo.WriteFile(root, "../wt-evil/x.txt", "no"));
    }

    // ---------- GitHubToolset.PushBranch ----------

    [Fact]
    public async Task PushBranch_issues_the_git_sequence_in_order_and_returns_the_branch()
    {
        var (gh, _, _) = Github();
        var runner = FakeRunnerSession.AlwaysOk();

        var branch = await gh.PushBranch(runner, "feature/x", "Add x");

        Assert.Equal("feature/x", branch);
        Assert.Equal(
        [
            "git checkout -b feature/x",
            "git add -A",
            // The subject is double-quoted so the runner's tokeniser keeps a multi-word message as
            // one -m argument (the runner splits on whitespace and strips quotes; see CommandLine).
            "git -c user.email=harness@users.noreply.github.com -c user.name=Harness commit -m \"Add x\"",
            "git push origin feature/x",
        ], runner.Commands);
    }

    [Fact]
    public async Task PushBranch_never_issues_a_merge_command()
    {
        var (gh, _, _) = Github();
        var runner = FakeRunnerSession.AlwaysOk();

        await gh.PushBranch(runner, "feature/x", "msg");

        Assert.DoesNotContain(runner.Commands, c => c.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PushBranch_fails_closed_when_push_exits_non_zero_and_surfaces_stderr()
    {
        var (gh, _, _) = Github();
        var runner = FakeRunnerSession.FailingAt("push", "fatal: repository not found");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gh.PushBranch(runner, "feature/x", "msg"));

        Assert.Contains("fatal: repository not found", ex.Message);
        // It got as far as attempting the push, and no merge slipped in.
        Assert.Contains("git push origin feature/x", runner.Commands);
        Assert.DoesNotContain(runner.Commands, c => c.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PushBranch_stops_at_the_first_failure_and_does_not_push()
    {
        var (gh, _, _) = Github();
        var runner = FakeRunnerSession.FailingAt("commit", "nothing to commit");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gh.PushBranch(runner, "feature/x", "msg"));

        // commit failed ⇒ push was never attempted (fail-closed, not "carry on").
        Assert.DoesNotContain(runner.Commands, c => c.StartsWith("git push"));
    }

    [Fact]
    public async Task PushBranch_fails_closed_when_the_runner_is_null()
    {
        var (gh, _, _) = Github();

        // A write with no isolated worktree does not happen (invariant 2). The ToolRegistry closure
        // is the primary guard; the method refuses a null runner as defence in depth.
        await Assert.ThrowsAsync<ArgumentNullException>(() => gh.PushBranch(null!, "b", "m"));
    }

    [Fact]
    public async Task PushBranch_collapses_a_multiline_message_to_a_single_commit_subject()
    {
        var (gh, _, _) = Github();
        var runner = FakeRunnerSession.AlwaysOk();

        await gh.PushBranch(runner, "b", "line one\nline two");

        Assert.Contains("commit -m \"line one line two\"", runner.Commands[2]);
        Assert.DoesNotContain("\n", runner.Commands[2]);
    }

    // ---------- GitHubToolset.OpenPr ----------

    [Fact]
    public async Task OpenPr_creates_the_pull_request_with_the_given_fields_and_returns_the_url()
    {
        var (gh, prs, _) = Github();

        var url = await gh.OpenPr(issue: null, head: "feature/x", @base: "main", title: "Add x", body: "does x");

        Assert.Equal("https://github.com/o/r/pull/7", url);
        var call = Assert.Single(prs.Created);
        Assert.Equal("o", call.Owner);
        Assert.Equal("r", call.Name);
        Assert.Equal("Add x", call.PullRequest.Title);
        Assert.Equal("feature/x", call.PullRequest.Head);
        Assert.Equal("main", call.PullRequest.Base);
        Assert.Equal("does x", call.PullRequest.Body);
        Assert.False(prs.MergeAttempted);
    }

    [Fact]
    public async Task OpenPr_references_the_originating_issue_but_never_merges()
    {
        var (gh, prs, _) = Github();

        await gh.OpenPr(issue: 42, head: "feature/x", @base: "main", title: "Fix", body: "the fix");

        var call = Assert.Single(prs.Created);
        Assert.Contains("Closes #42", call.PullRequest.Body);
        Assert.False(prs.MergeAttempted);   // referencing an issue is not closing or merging it
    }

    // ---------- GitHubToolset.IssueComment ----------

    [Fact]
    public async Task IssueComment_posts_to_the_issue_and_returns_the_comment_url()
    {
        var (gh, _, comments) = Github();

        var url = await gh.IssueComment(9, "a note");

        Assert.Equal("https://github.com/o/r/issues/9#issuecomment-42", url);
        var call = Assert.Single(comments.Created);
        Assert.Equal("o", call.Owner);
        Assert.Equal("r", call.Name);
        Assert.Equal(9, call.IssueNumber);
        Assert.Equal("a note", call.Body);
    }

    // ---------- catalog ----------

    [Fact]
    public void Catalog_carries_the_new_write_tools_at_the_expected_levels()
    {
        var catalog = ToolCatalog.Default;   // loads and validates the embedded YAML

        Assert.True(catalog.TryGetTool("repo.write_worktree", out var write));
        Assert.Equal(("repo", "write-worktree"), (write!.Scope, write.Level));

        Assert.True(catalog.TryGetTool("github.push_branch", out var push));
        Assert.Equal(("github", "open_pr+issues"), (push!.Scope, push.Level));

        Assert.True(catalog.TryGetTool("github.open_pr", out var openPr));
        Assert.Equal(("github", "open_pr+issues"), (openPr!.Scope, openPr.Level));

        Assert.True(catalog.TryGetTool("github.issue_comment", out var comment));
        Assert.Equal(("github", "comment"), (comment!.Scope, comment.Level));

        // The catalog still forbids a merge name outright — no write tool smuggled one in.
        Assert.DoesNotContain(catalog.ToolNames, n => n.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
