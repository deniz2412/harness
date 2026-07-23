using Octokit;

// These fakes implement whole Octokit client interfaces; a couple of their members reference
// obsolete sub-clients we never touch. The warning is noise for a test double.
#pragma warning disable CS0618

namespace Harness.Tools.Tests.Fakes;

/// <summary>
/// Hand-rolled Octokit fakes for the write tools. No mocking library is on the offline restore path,
/// so these implement the (large) client interfaces directly: every member throws
/// <see cref="NotImplementedException"/> except the two calls the toolset actually makes
/// (<c>PullRequest.Create</c>, <c>Issue.Comment.Create</c>), which record their arguments and return
/// a canned result. <c>PullRequest.Merge</c> is wired to flip a flag rather than throw, so a test can
/// assert positively that no merge was ever attempted (invariant 1).
/// </summary>
internal static class OctokitModels
{
    // Octokit response models keep their setters non-public; a parameterless ctor + reflected setter
    // is the least-brittle way to stamp just the one field these tests read.
    public static PullRequest PullRequest(string htmlUrl)
    {
        var pr = new PullRequest();
        typeof(PullRequest).GetProperty(nameof(Octokit.PullRequest.HtmlUrl))!.SetValue(pr, htmlUrl);
        return pr;
    }

    public static IssueComment IssueComment(string htmlUrl)
    {
        var c = new IssueComment();
        typeof(IssueComment).GetProperty(nameof(Octokit.IssueComment.HtmlUrl))!.SetValue(c, htmlUrl);
        return c;
    }
}

internal sealed record CreatePrCall(string Owner, string Name, NewPullRequest PullRequest);
internal sealed record CreateCommentCall(string Owner, string Name, int IssueNumber, string Body);

internal sealed class FakeGitHubClient : IGitHubClient
{
    public FakeGitHubClient(IIssuesClient issue, IPullRequestsClient pullRequest)
    {
        Issue = issue;
        PullRequest = pullRequest;
    }

    public global::Octokit.ApiInfo GetLastApiInfo() => throw new global::System.NotImplementedException();
    // ===== IGitHubClient surface (only Issue and PullRequest are wired) =====
    public global::Octokit.IConnection Connection { get; set; } = default!;
    public global::Octokit.IAuthorizationsClient Authorization { get; set; } = default!;
    public global::Octokit.IActivitiesClient Activity { get; set; } = default!;
    public global::Octokit.IActionsClient Actions { get; set; } = default!;
    public global::Octokit.IGitHubAppsClient GitHubApps { get; set; } = default!;
    public global::Octokit.IIssuesClient Issue { get; set; } = default!;
    public global::Octokit.IMigrationClient Migration { get; set; } = default!;
    public global::Octokit.IMiscellaneousClient Miscellaneous { get; set; } = default!;
    public global::Octokit.IOauthClient Oauth { get; set; } = default!;
    public global::Octokit.IOrganizationsClient Organization { get; set; } = default!;
    public global::Octokit.IPackagesClient Packages { get; set; } = default!;
    public global::Octokit.IPullRequestsClient PullRequest { get; set; } = default!;
    public global::Octokit.IRepositoriesClient Repository { get; set; } = default!;
    public global::Octokit.IGistsClient Gist { get; set; } = default!;
    public global::Octokit.IUsersClient User { get; set; } = default!;
    public global::Octokit.IGitDatabaseClient Git { get; set; } = default!;
    public global::Octokit.ISearchClient Search { get; set; } = default!;
    public global::Octokit.IEnterpriseClient Enterprise { get; set; } = default!;
    public global::Octokit.IReactionsClient Reaction { get; set; } = default!;
    public global::Octokit.IChecksClient Check { get; set; } = default!;
    public global::Octokit.IMetaClient Meta { get; set; } = default!;
    public global::Octokit.IRateLimitClient RateLimit { get; set; } = default!;
    public global::Octokit.IMarkdownClient Markdown { get; set; } = default!;
    public global::Octokit.IGitIgnoreClient GitIgnore { get; set; } = default!;
    public global::Octokit.ILicensesClient Licenses { get; set; } = default!;
    public global::Octokit.IEmojisClient Emojis { get; set; } = default!;
    public global::Octokit.ICodespacesClient Codespaces { get; set; } = default!;
    public global::Octokit.ICopilotClient Copilot { get; set; } = default!;
    public global::Octokit.IDependencyGraphClient DependencyGraph { get; set; } = default!;
    public void SetRequestTimeout(global::System.TimeSpan timeout) => throw new global::System.NotImplementedException();
}

internal sealed class FakeIssuesClient : IIssuesClient
{
    public FakeIssuesClient(IIssueCommentsClient comment) => Comment = comment;

    // ===== IIssuesClient surface (only Comment is wired) =====
    public global::Octokit.IAssigneesClient Assignee { get; set; } = default!;
    public global::Octokit.IIssuesEventsClient Events { get; set; } = default!;
    public global::Octokit.IMilestonesClient Milestone { get; set; } = default!;
    public global::Octokit.IIssuesLabelsClient Labels { get; set; } = default!;
    public global::Octokit.IIssueCommentsClient Comment { get; set; } = default!;
    public global::Octokit.IIssueTimelineClient Timeline { get; set; } = default!;
    public global::Octokit.ILockUnlockClient LockUnlock { get; set; } = default!;
    public global::System.Threading.Tasks.Task<global::Octokit.Issue> Get(global::System.String owner, global::System.String name, global::System.Int32 issueNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.Issue> Get(global::System.Int64 repositoryId, global::System.Int32 issueNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForCurrent() => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForCurrent(global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForCurrent(global::Octokit.IssueRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForCurrent(global::Octokit.IssueRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOwnedAndMemberRepositories() => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOwnedAndMemberRepositories(global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOwnedAndMemberRepositories(global::Octokit.IssueRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOwnedAndMemberRepositories(global::Octokit.IssueRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOrganization(global::System.String organization) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOrganization(global::System.String organization, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOrganization(global::System.String organization, global::Octokit.IssueRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForOrganization(global::System.String organization, global::Octokit.IssueRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.String owner, global::System.String name) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.Int64 repositoryId) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.RepositoryIssueRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.RepositoryIssueRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.RepositoryIssueRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.Issue>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.RepositoryIssueRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.Issue> Create(global::System.String owner, global::System.String name, global::Octokit.NewIssue newIssue) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.Issue> Create(global::System.Int64 repositoryId, global::Octokit.NewIssue newIssue) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.Issue> Update(global::System.String owner, global::System.String name, global::System.Int32 issueNumber, global::Octokit.IssueUpdate issueUpdate) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.Issue> Update(global::System.Int64 repositoryId, global::System.Int32 issueNumber, global::Octokit.IssueUpdate issueUpdate) => throw new global::System.NotImplementedException();
}

internal sealed class FakeIssueCommentsClient : IIssueCommentsClient
{
    public List<CreateCommentCall> Created { get; } = [];
    public string ReturnHtmlUrl { get; init; } = "https://github.com/o/r/issues/1#issuecomment-1";

    public global::System.Threading.Tasks.Task<global::Octokit.IssueComment> Create(global::System.String owner, global::System.String name, global::System.Int32 issueNumber, global::System.String newComment)
    {
        Created.Add(new CreateCommentCall(owner, name, issueNumber, newComment));
        return global::System.Threading.Tasks.Task.FromResult(OctokitModels.IssueComment(ReturnHtmlUrl));
    }

    // ===== rest of IIssueCommentsClient: unused =====
    public global::System.Threading.Tasks.Task<global::Octokit.IssueComment> Create(global::System.Int64 repositoryId, global::System.Int32 issueNumber, global::System.String newComment) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.IssueComment> Get(global::System.String owner, global::System.String name, global::System.Int64 commentId) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.IssueComment> Get(global::System.Int64 repositoryId, global::System.Int64 commentId) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.String owner, global::System.String name) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.Int64 repositoryId) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.IssueCommentRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.IssueCommentRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.IssueCommentRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.IssueCommentRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.String owner, global::System.String name, global::System.Int32 issueNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.Int64 repositoryId, global::System.Int32 issueNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.String owner, global::System.String name, global::System.Int32 issueNumber, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.Int64 repositoryId, global::System.Int32 issueNumber, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.String owner, global::System.String name, global::System.Int32 issueNumber, global::Octokit.IssueCommentRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.Int64 repositoryId, global::System.Int32 issueNumber, global::Octokit.IssueCommentRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.String owner, global::System.String name, global::System.Int32 issueNumber, global::Octokit.IssueCommentRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.IssueComment>> GetAllForIssue(global::System.Int64 repositoryId, global::System.Int32 issueNumber, global::Octokit.IssueCommentRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.IssueComment> Update(global::System.String owner, global::System.String name, global::System.Int64 commentId, global::System.String commentUpdate) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.IssueComment> Update(global::System.Int64 repositoryId, global::System.Int64 commentId, global::System.String commentUpdate) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task Delete(global::System.String owner, global::System.String name, global::System.Int64 commentId) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task Delete(global::System.Int64 repositoryId, global::System.Int64 commentId) => throw new global::System.NotImplementedException();
}

internal sealed class FakePullRequestsClient : IPullRequestsClient
{
    public List<CreatePrCall> Created { get; } = [];
    public bool MergeAttempted { get; private set; }
    public string ReturnHtmlUrl { get; init; } = "https://github.com/o/r/pull/1";

    public global::System.Threading.Tasks.Task<global::Octokit.PullRequest> Create(global::System.String owner, global::System.String name, global::Octokit.NewPullRequest newPullRequest)
    {
        Created.Add(new CreatePrCall(owner, name, newPullRequest));
        return global::System.Threading.Tasks.Task.FromResult(OctokitModels.PullRequest(ReturnHtmlUrl));
    }

    // Merge is wired (not throwing) purely so a test can assert it is NEVER reached. The platform has
    // no merge tool by design (invariant 1); this flag makes that provable, not just implicit.
    public global::System.Threading.Tasks.Task<global::Octokit.PullRequestMerge> Merge(global::System.String owner, global::System.String name, global::System.Int32 pullRequestNumber, global::Octokit.MergePullRequest mergePullRequest)
    {
        MergeAttempted = true;
        throw new global::System.InvalidOperationException("merge must never be called");
    }
    public global::System.Threading.Tasks.Task<global::Octokit.PullRequestMerge> Merge(global::System.Int64 repositoryId, global::System.Int32 pullRequestNumber, global::Octokit.MergePullRequest mergePullRequest)
    {
        MergeAttempted = true;
        throw new global::System.InvalidOperationException("merge must never be called");
    }

    // ===== rest of IPullRequestsClient: unused =====
    public global::Octokit.IPullRequestReviewsClient Review { get; set; } = default!;
    public global::Octokit.IPullRequestReviewCommentsClient ReviewComment { get; set; } = default!;
    public global::Octokit.IPullRequestReviewRequestsClient ReviewRequest { get; set; } = default!;
    public global::Octokit.ILockUnlockClient LockUnlock { get; set; } = default!;
    public global::System.Threading.Tasks.Task<global::Octokit.PullRequest> Get(global::System.String owner, global::System.String name, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.PullRequest> Get(global::System.Int64 repositoryId, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.String owner, global::System.String name) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.Int64 repositoryId) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.PullRequestRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.PullRequestRequest request) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.String owner, global::System.String name, global::Octokit.PullRequestRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequest>> GetAllForRepository(global::System.Int64 repositoryId, global::Octokit.PullRequestRequest request, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.PullRequest> Create(global::System.Int64 repositoryId, global::Octokit.NewPullRequest newPullRequest) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.PullRequest> Update(global::System.String owner, global::System.String name, global::System.Int32 pullRequestNumber, global::Octokit.PullRequestUpdate pullRequestUpdate) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::Octokit.PullRequest> Update(global::System.Int64 repositoryId, global::System.Int32 pullRequestNumber, global::Octokit.PullRequestUpdate pullRequestUpdate) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Boolean> Merged(global::System.String owner, global::System.String name, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Boolean> Merged(global::System.Int64 repositoryId, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequestCommit>> Commits(global::System.String owner, global::System.String name, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequestCommit>> Commits(global::System.Int64 repositoryId, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequestFile>> Files(global::System.String owner, global::System.String name, global::System.Int32 pullRequestNumber, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequestFile>> Files(global::System.String owner, global::System.String name, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequestFile>> Files(global::System.Int64 repositoryId, global::System.Int32 pullRequestNumber, global::Octokit.ApiOptions options) => throw new global::System.NotImplementedException();
    public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Octokit.PullRequestFile>> Files(global::System.Int64 repositoryId, global::System.Int32 pullRequestNumber) => throw new global::System.NotImplementedException();
}
