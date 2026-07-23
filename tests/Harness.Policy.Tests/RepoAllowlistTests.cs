using Xunit;

namespace Harness.Policy.Tests;

/// <summary>
/// The M3 repo allowlist: the policy control that decides which existing repositories a run may act
/// on. Fail-closed — an unlisted, empty, or malformed target is refused, never allowed on error.
/// </summary>
public class RepoAllowlistTests
{
    private static RepoAllowlist List(params string[] entries) => new(entries);

    // ---- exact entries ----

    [Fact]
    public void Allowed_exact_repo_passes_IsAllowed_and_Assert()
    {
        var list = List("deniz2412/test-repo-harness", "octocat/hello-world");

        Assert.True(list.IsAllowed("deniz2412/test-repo-harness"));
        list.Assert("deniz2412/test-repo-harness");   // does not throw
        Assert.True(list.IsAllowed("octocat/hello-world"));
    }

    [Fact]
    public void Non_allowlisted_repo_is_denied()
    {
        var list = List("deniz2412/test-repo-harness");

        Assert.False(list.IsAllowed("deniz2412/other-repo"));
        var ex = Assert.Throws<PolicyViolationException>(() => list.Assert("deniz2412/other-repo"));
        Assert.Equal("repo-allowlist", ex.Stage);
        Assert.Contains("not on the run allowlist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var list = List("Deniz2412/Test-Repo-Harness");

        Assert.True(list.IsAllowed("deniz2412/test-repo-harness"));
        Assert.True(list.IsAllowed("DENIZ2412/TEST-REPO-HARNESS"));
        list.Assert("deniz2412/test-repo-harness");
    }

    [Fact]
    public void Whitespace_around_entries_and_targets_is_tolerated()
    {
        var list = List("  deniz2412/test-repo-harness  ");

        Assert.True(list.IsAllowed(" deniz2412/test-repo-harness "));
    }

    // ---- fail-closed: empty and malformed ----

    [Fact]
    public void Empty_allowlist_denies_everything()
    {
        var list = List();   // no entries

        Assert.Empty(list.Entries);
        Assert.False(list.IsAllowed("deniz2412/test-repo-harness"));
        Assert.Throws<PolicyViolationException>(() => list.Assert("deniz2412/test-repo-harness"));
    }

    [Fact]
    public void Null_entry_sequence_denies_everything()
    {
        var list = new RepoAllowlist(null);

        Assert.Empty(list.Entries);
        Assert.False(list.IsAllowed("deniz2412/test-repo-harness"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]                       // no owner/name separator
    [InlineData("a/b/c")]                   // too many segments
    [InlineData("a b/c")]                   // whitespace in a segment
    [InlineData("../etc")]                  // path traversal
    [InlineData("deniz2412/../secrets")]    // traversal in the name
    [InlineData("/name")]                   // empty owner
    [InlineData("owner/")]                  // empty name
    [InlineData("owner")]                   // no separator
    public void Malformed_or_null_target_is_denied(string? target)
    {
        // Entry list is valid; the *target* is what is malformed.
        var list = List("owner/name");

        Assert.False(list.IsAllowed(target));
        Assert.Throws<PolicyViolationException>(() => list.Assert(target));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("a/b/c")]
    [InlineData("a b/c")]
    [InlineData("../etc")]
    [InlineData("owner/")]
    [InlineData("")]
    public void Malformed_entry_in_the_allowlist_fails_closed_at_construction(string badEntry)
    {
        var ex = Assert.Throws<PolicyViolationException>(() => List("good/repo", badEntry));
        Assert.Equal("repo-allowlist", ex.Stage);
    }

    // ---- wildcard (owner/*) ----

    [Fact]
    public void Wildcard_matches_any_repo_under_that_owner()
    {
        var list = List("deniz2412/*");

        Assert.True(list.IsAllowed("deniz2412/test-repo-harness"));
        Assert.True(list.IsAllowed("deniz2412/anything-else"));
        Assert.True(list.IsAllowed("DENIZ2412/Whatever"));   // case-insensitive
        list.Assert("deniz2412/some-repo");
    }

    [Fact]
    public void Wildcard_does_not_leak_to_a_different_owner()
    {
        var list = List("deniz2412/*");

        Assert.False(list.IsAllowed("someoneelse/test-repo-harness"));
        Assert.Throws<PolicyViolationException>(() => list.Assert("someoneelse/anything"));
    }

    [Fact]
    public void A_wildcard_string_is_never_a_valid_run_target()
    {
        // "owner/*" is a scope in the allowlist, not a repository a run can act on.
        var list = List("deniz2412/*");

        Assert.False(list.IsAllowed("deniz2412/*"));
        Assert.Throws<PolicyViolationException>(() => list.Assert("deniz2412/*"));
    }

    // ---- Entries surface (for search scoping) ----

    [Fact]
    public void Entries_are_normalised_and_deduplicated_for_search_scoping()
    {
        var list = List("deniz2412/test-repo-harness", "Deniz2412/Test-Repo-Harness", "octocat/*");

        Assert.Contains("deniz2412/test-repo-harness", list.Entries);
        Assert.Contains("octocat/*", list.Entries);
        Assert.Equal(2, list.Entries.Count);   // the case-variant duplicate collapsed
    }

    // ---- YAML factory ----

    [Fact]
    public void FromYaml_reads_a_repos_list()
    {
        var list = RepoAllowlist.FromYaml(
            "repos:\n  - deniz2412/test-repo-harness\n  - octocat/*\n");

        Assert.True(list.IsAllowed("deniz2412/test-repo-harness"));
        Assert.True(list.IsAllowed("octocat/hello-world"));
        Assert.False(list.IsAllowed("evil/repo"));
    }

    [Fact]
    public void FromYaml_with_no_repos_denies_everything()
    {
        var list = RepoAllowlist.FromYaml("repos: []\n");

        Assert.Empty(list.Entries);
        Assert.False(list.IsAllowed("deniz2412/test-repo-harness"));
    }
}
