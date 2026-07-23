using Xunit;

namespace Harness.Policy.Tests;

/// <summary>
/// M3 catalogs two read-only cross-repo search tools. They must be present at (github, read), be
/// usable under a plain <c>github: read</c> ceiling, and — like every catalogued tool — not trip the
/// no-merge / no-repo-lifecycle guard.
/// </summary>
public class SearchToolCatalogTests
{
    private static readonly PolicyPipeline Pipeline = new();

    [Theory]
    [InlineData("github.search_code")]
    [InlineData("github.search_repos")]
    public void Search_tools_are_catalogued_at_github_read(string tool)
    {
        Assert.True(Pipeline.Catalog.TryGetTool(tool, out var entry), tool);
        Assert.Equal("github", entry!.Scope);
        Assert.Equal("read", entry.Level);
    }

    [Theory]
    [InlineData("github.search_code")]
    [InlineData("github.search_repos")]
    public void Search_tools_are_allowed_under_a_read_only_github_ceiling(string tool)
    {
        // Read-only: a workflow needs only github: read (or higher) to use them.
        Pipeline.AssertToolAllowed(
            tool,
            new[] { tool },
            new Dictionary<string, string> { ["github"] = "read" });
    }

    [Theory]
    [InlineData("github.search_code")]
    [InlineData("github.search_repos")]
    public void Search_tools_do_not_trip_the_forbidden_name_guard(string tool)
    {
        // Loading a catalog whose only tool is a search tool must succeed — the read-only search
        // names are not merge/create/delete and must not be mistaken for them.
        var yaml = $"""
            version: 1
            scopes:
              - name: github
                levels: [none, read, comment, open_pr+issues]
            tools:
              - name: {tool}
                scope: github
                level: read
                description: read-only search
            """;

        var catalog = ToolCatalog.FromYaml(yaml);
        Assert.True(catalog.TryGetTool(tool, out _));
    }

    [Fact]
    public void The_no_merge_no_repo_lifecycle_guarantee_still_holds()
    {
        // Adding the search tools must not have introduced any merge or repo-lifecycle capability.
        Assert.DoesNotContain(Pipeline.Catalog.ToolNames,
            n => n.Contains("merge", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("create_repo", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("delete_repo", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("fork", StringComparison.OrdinalIgnoreCase));
    }
}
