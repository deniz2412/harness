using Octokit;

namespace Harness.Tools.Tests.Fakes;

/// <summary>
/// A recording <see cref="ISearchClient"/> for the M3 read-only search tools, offline. It captures the
/// request the toolset builds (so a test can assert the scope qualifiers) and returns canned results,
/// or throws a scripted error so a test can prove a failed search becomes a refusal, not an exception
/// the agent loop spins on. Only <c>SearchCode</c>/<c>SearchRepo</c> are wired; the rest throw.
/// </summary>
internal sealed class FakeSearchClient : ISearchClient
{
    public SearchCodeRequest? LastCodeRequest { get; private set; }
    public SearchRepositoriesRequest? LastRepoRequest { get; private set; }

    public SearchCodeResult CodeResult { get; set; } = new(0, false, Array.Empty<SearchCode>());
    public SearchRepositoryResult RepoResult { get; set; } = new(0, false, Array.Empty<Repository>());

    public Exception? ThrowOnCode { get; set; }
    public Exception? ThrowOnRepo { get; set; }

    public Task<SearchCodeResult> SearchCode(SearchCodeRequest search)
    {
        LastCodeRequest = search;
        if (ThrowOnCode is not null) throw ThrowOnCode;
        return Task.FromResult(CodeResult);
    }

    public Task<SearchRepositoryResult> SearchRepo(SearchRepositoriesRequest search)
    {
        LastRepoRequest = search;
        if (ThrowOnRepo is not null) throw ThrowOnRepo;
        return Task.FromResult(RepoResult);
    }

    public Task<SearchUsersResult> SearchUsers(SearchUsersRequest search) => throw new NotImplementedException();
    public Task<SearchIssuesResult> SearchIssues(SearchIssuesRequest search) => throw new NotImplementedException();
    public Task<SearchLabelsResult> SearchLabels(SearchLabelsRequest search) => throw new NotImplementedException();
}

/// <summary>
/// Builders for Octokit search models. Their setters are non-public, so — like <c>OctokitModels</c> —
/// we stamp just the fields the toolset reads via reflection.
/// </summary>
internal static class SearchModels
{
    public static Repository Repo(string fullName, string? description = null, string defaultBranch = "main")
    {
        var parts = fullName.Split('/');
        var r = new Repository();
        Set(r, nameof(Repository.FullName), fullName);
        Set(r, nameof(Repository.Name), parts.Length == 2 ? parts[1] : fullName);
        Set(r, nameof(Repository.Description), description);
        Set(r, nameof(Repository.DefaultBranch), defaultBranch);
        return r;
    }

    // SearchCode has a public 7-arg constructor, so the only reflected part is its embedded Repository.
    public static SearchCode Code(string repoFullName, string path) =>
        new("file", path, "sha", "url", "giturl",
            $"https://github.com/{repoFullName}/blob/main/{path}", Repo(repoFullName));

    public static SearchCodeResult CodeResult(params SearchCode[] items) =>
        new(items.Length, false, items);

    public static SearchCodeResult CodeResult(int totalCount, IReadOnlyList<SearchCode> items) =>
        new(totalCount, false, items);

    public static SearchRepositoryResult RepoResult(params Repository[] items) =>
        new(items.Length, false, items);

    private static void Set(object o, string prop, object? val) =>
        o.GetType().GetProperty(prop)!.SetValue(o, val);
}
