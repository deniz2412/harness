using Harness.Tools.Tests.Fakes;
using Octokit;
using Xunit;

namespace Harness.Tools.Tests;

/// <summary>
/// The M3 read-only search tools in isolation, fully offline. They prove the guarantees that matter
/// for cross-repo search: results are CONFINED to the injected allowlist scope, an empty scope makes
/// nothing searchable (fail-closed, not "search everything"), output is bounded, a failed/blank query
/// becomes a refusal rather than an exception the agent loop can spin on — and search NEVER reaches a
/// write, merge or create path.
/// </summary>
public sealed class SearchToolsTests
{
    // A toolset whose bound owner/repo is deliberately unrelated to the search scope: search ignores
    // the instance binding and works purely off the injected scope. The write/comment fakes come along
    // so a test can assert positively that search touched none of them.
    private static (GitHubToolset gh, FakeSearchClient search, FakePullRequestsClient prs, FakeIssueCommentsClient comments) Setup()
    {
        var search = new FakeSearchClient();
        var comments = new FakeIssueCommentsClient();
        var prs = new FakePullRequestsClient();
        var client = new FakeGitHubClient(new FakeIssuesClient(comments), prs) { Search = search };
        return (new GitHubToolset(client, "bound-owner", "bound-repo"), search, prs, comments);
    }

    private static string[] Lines(string output) =>
        output.Split('\n').Where(l => l.StartsWith("- ")).ToArray();

    // ---------- SearchCode ----------

    [Fact]
    public async Task SearchCode_confines_results_to_the_injected_scope()
    {
        var (gh, search, _, _) = Setup();
        // The fake returns hits from an in-scope repo AND an out-of-scope one; only in-scope may surface.
        search.CodeResult = SearchModels.CodeResult(
            SearchModels.Code("acme/widgets", "src/Discount.cs"),
            SearchModels.Code("evil/secrets", "src/Leak.cs"),
            SearchModels.Code("acme/widgets", "src/Tier.cs"));

        var output = await gh.SearchCode(["acme/widgets"], "discount");

        Assert.Contains("acme/widgets", output);
        Assert.Contains("src/Discount.cs", output);
        Assert.DoesNotContain("evil/secrets", output);
        Assert.DoesNotContain("Leak.cs", output);
    }

    [Fact]
    public async Task SearchCode_scopes_the_request_with_a_repo_qualifier_per_scoped_repo()
    {
        var (gh, search, _, _) = Setup();

        await gh.SearchCode(["acme/widgets", "acme/tools"], "foo");

        // The request the toolset built confines the API call itself to the scope (defence in depth
        // alongside the post-filter).
        Assert.NotNull(search.LastCodeRequest);
        Assert.Equal(2, search.LastCodeRequest!.Repos.Count);
    }

    [Fact]
    public async Task SearchCode_empty_scope_is_fail_closed_and_never_calls_the_api()
    {
        var (gh, search, _, _) = Setup();

        var output = await gh.SearchCode([], "anything");

        Assert.Contains("No repositories are in scope", output);
        Assert.Null(search.LastCodeRequest);   // nothing searchable ⇒ no GitHub call at all
    }

    [Fact]
    public async Task SearchCode_caps_the_number_of_results()
    {
        var (gh, search, _, _) = Setup();
        var many = Enumerable.Range(0, 50)
            .Select(i => SearchModels.Code("acme/widgets", $"src/File{i}.cs"))
            .ToArray();
        search.CodeResult = SearchModels.CodeResult(totalCount: 50, items: many);

        var output = await gh.SearchCode(["acme/widgets"], "foo");

        Assert.Equal(30, Lines(output).Length);       // hard cap
        Assert.Contains("of 50", output);             // and it says the set was larger
    }

    [Fact]
    public async Task SearchCode_blank_query_is_refused_without_throwing_or_calling_the_api()
    {
        var (gh, search, _, _) = Setup();

        var output = await gh.SearchCode(["acme/widgets"], "   ");

        Assert.Contains("non-empty search query", output);
        Assert.Null(search.LastCodeRequest);
    }

    [Fact]
    public async Task SearchCode_turns_an_api_failure_into_a_refusal_not_an_exception()
    {
        var (gh, search, _, _) = Setup();
        search.ThrowOnCode = new ApiException("boom", System.Net.HttpStatusCode.UnprocessableEntity);

        // The agent loop must not receive a throw it can spin on; it gets a plain refusal string.
        var output = await gh.SearchCode(["acme/widgets"], "malformed:::query");

        Assert.Contains("could not be completed", output);
    }

    [Fact]
    public async Task SearchCode_never_touches_any_write_merge_or_create_path()
    {
        var (gh, _, prs, comments) = Setup();

        await gh.SearchCode(["acme/widgets"], "foo");

        Assert.False(prs.MergeAttempted);    // positive assertion: no merge, ever (invariant 1)
        Assert.Empty(prs.Created);           // no PR created
        Assert.Empty(comments.Created);      // no comment written
    }

    // ---------- SearchRepos ----------

    [Fact]
    public async Task SearchRepos_confines_results_to_the_injected_scope()
    {
        var (gh, search, _, _) = Setup();
        search.RepoResult = SearchModels.RepoResult(
            SearchModels.Repo("acme/widgets", "the widgets service", "main"),
            SearchModels.Repo("evil/secrets", "should never surface", "master"));

        var output = await gh.SearchRepos(["acme/widgets"], "widgets");

        Assert.Contains("acme/widgets", output);
        Assert.DoesNotContain("evil/secrets", output);
    }

    [Fact]
    public async Task SearchRepos_returns_name_description_and_default_branch()
    {
        var (gh, search, _, _) = Setup();
        search.RepoResult = SearchModels.RepoResult(
            SearchModels.Repo("acme/widgets", "the widgets service", "develop"));

        var output = await gh.SearchRepos(["acme/widgets"], "widgets");

        Assert.Contains("acme/widgets", output);
        Assert.Contains("the widgets service", output);
        Assert.Contains("develop", output);
    }

    [Fact]
    public async Task SearchRepos_empty_scope_is_fail_closed_and_never_calls_the_api()
    {
        var (gh, search, _, _) = Setup();

        var output = await gh.SearchRepos([], "anything");

        Assert.Contains("No repositories are in scope", output);
        Assert.Null(search.LastRepoRequest);
    }

    [Fact]
    public async Task SearchRepos_narrows_the_request_to_the_scoped_owners()
    {
        var (gh, search, _, _) = Setup();

        await gh.SearchRepos(["acme/widgets", "acme/tools"], "foo");

        Assert.NotNull(search.LastRepoRequest);
        // The two repos share one owner; the request carries that owner as a user: qualifier.
        Assert.Contains("user:acme", search.LastRepoRequest!.Term);
    }

    [Fact]
    public async Task SearchRepos_blank_query_is_refused_without_throwing()
    {
        var (gh, search, _, _) = Setup();

        var output = await gh.SearchRepos(["acme/widgets"], "");

        Assert.Contains("non-empty search query", output);
        Assert.Null(search.LastRepoRequest);
    }

    [Fact]
    public async Task SearchRepos_turns_an_api_failure_into_a_refusal_not_an_exception()
    {
        var (gh, search, _, _) = Setup();
        search.ThrowOnRepo = new ApiException("boom", System.Net.HttpStatusCode.UnprocessableEntity);

        var output = await gh.SearchRepos(["acme/widgets"], "foo");

        Assert.Contains("could not be completed", output);
    }

    [Fact]
    public async Task SearchRepos_never_touches_any_write_merge_or_create_path()
    {
        var (gh, _, prs, comments) = Setup();

        await gh.SearchRepos(["acme/widgets"], "foo");

        Assert.False(prs.MergeAttempted);
        Assert.Empty(prs.Created);
        Assert.Empty(comments.Created);
    }
}
