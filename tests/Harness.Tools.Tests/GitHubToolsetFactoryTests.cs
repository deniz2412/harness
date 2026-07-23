using Harness.Tools.Tests.Fakes;
using Xunit;

namespace Harness.Tools.Tests;

/// <summary>
/// The M3 per-run toolset factory, offline. It proves the factory binds a toolset to the exact
/// <c>owner/name</c> it is given (so a run acts on its own repo, not a startup-configured singleton),
/// and fails FAST on a blank or malformed value — a toolset over a blank repo would 404 silently
/// mid-run, the M0-class bug this guards against.
/// </summary>
public sealed class GitHubToolsetFactoryTests
{
    private static (GitHubToolsetFactory factory, FakeIssueCommentsClient comments) Setup()
    {
        var comments = new FakeIssueCommentsClient();
        var client = new FakeGitHubClient(new FakeIssuesClient(comments), new FakePullRequestsClient());
        return (new GitHubToolsetFactory(client), comments);
    }

    [Fact]
    public async Task ForRepo_binds_the_toolset_to_the_given_owner_and_repo()
    {
        var (factory, comments) = Setup();

        // A repo-bound call records the owner/name the toolset targets on the fake client.
        await factory.ForRepo("acme/widgets").IssueComment(1, "note");

        var call = Assert.Single(comments.Created);
        Assert.Equal("acme", call.Owner);
        Assert.Equal("widgets", call.Name);
    }

    [Fact]
    public async Task ForRepo_trims_surrounding_whitespace()
    {
        var (factory, comments) = Setup();

        await factory.ForRepo("  acme/widgets  ").IssueComment(1, "note");

        var call = Assert.Single(comments.Created);
        Assert.Equal("acme", call.Owner);
        Assert.Equal("widgets", call.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForRepo_fails_fast_on_a_blank_value(string blank)
    {
        var (factory, _) = Setup();
        Assert.Throws<ArgumentException>(() => factory.ForRepo(blank));
    }

    [Theory]
    [InlineData("garbage")]        // no owner/name separator
    [InlineData("owner/")]         // empty name
    [InlineData("/name")]          // empty owner
    [InlineData("a/b/c")]          // too many segments
    [InlineData("owner name")]     // space, no slash
    public void ForRepo_fails_fast_on_a_malformed_value(string malformed)
    {
        var (factory, _) = Setup();
        Assert.Throws<ArgumentException>(() => factory.ForRepo(malformed));
    }
}
