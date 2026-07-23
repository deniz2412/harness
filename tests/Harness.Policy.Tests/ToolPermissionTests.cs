using Xunit;

namespace Harness.Policy.Tests;

/// <summary>
/// The M1 defect fix: the permission ceiling is now actually enforced. Every test here is a case
/// the M0 implementation passed — it looped over the node's own tool list, so containment was
/// tautological and <c>permissions:</c> was read by nothing.
/// </summary>
public class ToolPermissionTests
{
    private static readonly PolicyPipeline Pipeline = new();

    /// <summary>Exactly what workflows/pr-review.yaml declares.</summary>
    private static Dictionary<string, string> PrReviewCeiling => new()
    {
        ["repo"] = "read",
        ["github"] = "comment",
    };

    private static void Allowed(string tool, string[] nodeTools, Dictionary<string, string> ceiling) =>
        Pipeline.AssertToolAllowed(tool, nodeTools, ceiling);

    private static PolicyViolationException Denied(
        string tool, string[] nodeTools, Dictionary<string, string> ceiling) =>
        Assert.Throws<PolicyViolationException>(() => Pipeline.AssertToolAllowed(tool, nodeTools, ceiling));

    // ---- the real workflow ----

    [Theory]
    [InlineData("github.pr_diff")]
    [InlineData("repo.read")]
    [InlineData("codesearch.query")]
    public void PrReview_gather_node_tools_are_allowed(string tool)
    {
        // github: comment outranks the github: read that pr_diff needs.
        Allowed(tool, ["github.pr_diff", "repo.read", "codesearch.query"], PrReviewCeiling);
    }

    [Fact]
    public void PrReview_post_node_may_comment()
    {
        Allowed("github.pr_comment", ["github.pr_comment"], PrReviewCeiling);
    }

    [Fact]
    public void Comment_tool_is_denied_under_a_read_only_github_ceiling()
    {
        var ex = Denied(
            "github.pr_comment",
            ["github.pr_comment"],
            new Dictionary<string, string> { ["repo"] = "read", ["github"] = "read" });

        Assert.Equal("pre-tool", ex.Stage);
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_names_are_matched_case_insensitively()
    {
        Allowed("GitHub.PR_Diff", ["github.pr_diff"], PrReviewCeiling);
    }

    // ---- the three denial conditions ----

    [Fact]
    public void Tool_not_declared_on_the_node_is_denied()
    {
        var ex = Denied("github.pr_comment", ["repo.read"], PrReviewCeiling);
        Assert.Contains("not declared on this node", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("github.merge_pr")]        // does not exist, and never will (invariant 1)
    [InlineData("github.create_repo")]
    [InlineData("shell.exec")]
    [InlineData("repo.delete_file")]       // a plausible-shaped name that is deliberately NOT catalogued
    public void Tool_outside_the_curated_catalog_is_denied_even_when_the_node_lists_it(string tool)
    {
        // The node listing it is the attack: in M0 this was the only check, and it was circular.
        var ex = Denied(tool, [tool], new Dictionary<string, string>
        {
            ["repo"] = "write-worktree",
            ["github"] = "open_pr+issues",
        });

        Assert.Contains("curated tool catalog", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_absent_from_the_ceiling_denies()
    {
        var ex = Denied("repo.read", ["repo.read"], new Dictionary<string, string> { ["github"] = "comment" });
        Assert.Contains("declares no 'repo' permission", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_ceiling_denies()
    {
        var ex = Denied("repo.read", ["repo.read"], new Dictionary<string, string>());
        Assert.Contains("no permission ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("readonly")]     // plausible typo
    [InlineData("write")]        // a level of another system, not of this scope
    [InlineData("comment")]      // a level of another scope
    [InlineData("")]
    public void Unrecognised_level_string_denies(string level)
    {
        var ceiling = new Dictionary<string, string> { ["repo"] = level, ["github"] = "comment" };

        var ex = Denied("repo.read", ["repo.read"], ceiling);
        Assert.Contains("not a recognised level", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ceiling_scopes_and_levels_tolerate_case_and_stray_whitespace()
    {
        // Workflow YAML is hand-written; "Repo: READ " is a formatting slip, not a policy change.
        Allowed("repo.read", ["repo.read"],
            new Dictionary<string, string> { ["Repo"] = " READ ", ["GITHUB"] = "comment" });
    }

    [Fact]
    public void Unrecognised_scope_in_the_ceiling_denies_every_tool()
    {
        // An unenforceable ceiling is not a permissive one — including for tools in known scopes.
        var ceiling = new Dictionary<string, string>
        {
            ["repo"] = "read",
            ["github"] = "comment",
            ["kubernetes"] = "admin",
        };

        var ex = Denied("repo.read", ["repo.read"], ceiling);
        Assert.Contains("not a recognised scope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_none_ceiling_denies_read_tools()
    {
        var ex = Denied(
            "repo.read", ["repo.read"],
            new Dictionary<string, string> { ["repo"] = "none", ["github"] = "none" });

        Assert.Contains("ceiling is 'repo: none'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_tool_name_and_empty_node_list_deny()
    {
        Assert.Throws<PolicyViolationException>(
            () => Pipeline.AssertToolAllowed("", ["repo.read"], PrReviewCeiling));
        Assert.Throws<PolicyViolationException>(
            () => Pipeline.AssertToolAllowed("repo.read", [], PrReviewCeiling));
    }

    // ---- the catalog itself ----

    [Fact]
    public void Catalog_maps_the_tool_surface_to_scope_and_level()
    {
        var expected = new (string Tool, string Scope, string Level)[]
        {
            // M0/M1 read + comment surface
            ("github.pr_diff", "github", "read"),
            ("github.get_issue", "github", "read"),
            ("github.pr_comment", "github", "comment"),
            ("repo.read", "repo", "read"),
            ("repo.list", "repo", "read"),
            ("codesearch.query", "repo", "read"),
            // M2 write surface — the levels the write workflows declare as their ceiling
            ("github.issue_comment", "github", "comment"),
            ("github.push_branch", "github", "open_pr+issues"),
            ("github.open_pr", "github", "open_pr+issues"),
            ("repo.write_worktree", "repo", "write-worktree"),
            // M3 read-only cross-repo search surface
            ("github.search_code", "github", "read"),
            ("github.search_repos", "github", "read"),
        };

        foreach (var (tool, scope, level) in expected)
        {
            Assert.True(Pipeline.Catalog.TryGetTool(tool, out var entry), tool);
            Assert.Equal(scope, entry!.Scope);
            Assert.Equal(level, entry.Level);
        }

        Assert.Equal(expected.Length, Pipeline.Catalog.Tools.Count);
    }

    [Fact]
    public void Levels_are_ordered_as_the_workflow_yaml_assumes()
    {
        Assert.Equal(new[] { "none", "read", "write-worktree" }, Pipeline.Catalog.Scopes["repo"].Levels);
        Assert.Equal(new[] { "none", "read", "comment", "open_pr+issues" }, Pipeline.Catalog.Scopes["github"].Levels);
        Assert.True(Pipeline.Catalog.Scopes["github"].Rank("comment")
                    > Pipeline.Catalog.Scopes["github"].Rank("read"));
        Assert.Equal(-1, Pipeline.Catalog.Scopes["github"].Rank("merge"));
    }

    [Fact]
    public void Catalog_contains_no_merge_and_no_repo_lifecycle_tool()
    {
        Assert.DoesNotContain(Pipeline.Catalog.ToolNames,
            n => n.Contains("merge", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("create_repo", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("delete_repo", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("github.merge_pr")]
    [InlineData("github.pr_merge")]
    [InlineData("github.create_repo")]
    [InlineData("github.delete_repository")]
    [InlineData("github.fork")]            // invariant 1 names fork explicitly (M3 review)
    [InlineData("repo.fork_repo")]
    public void A_catalog_that_declares_a_forbidden_capability_is_rejected(string forbidden)
    {
        var yaml = $"""
            version: 1
            scopes:
              - name: github
                levels: [none, read, comment, open_pr+issues]
            tools:
              - name: {forbidden}
                scope: github
                level: comment
                description: should never load
            """;

        var ex = Assert.Throws<PolicyViolationException>(() => ToolCatalog.FromYaml(yaml));
        Assert.Equal("tool-catalog", ex.Stage);
        Assert.Contains("forbidden capability", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version: 1\nscopes: []\ntools: []")]
    [InlineData("version: 1\nscopes:\n  - name: repo\n    levels: [none, read]\ntools: []")]
    // tool in an undeclared scope
    [InlineData("version: 1\nscopes:\n  - name: repo\n    levels: [none, read]\ntools:\n  - name: x.y\n    scope: nope\n    level: read")]
    // tool at a level the scope does not define
    [InlineData("version: 1\nscopes:\n  - name: repo\n    levels: [none, read]\ntools:\n  - name: x.y\n    scope: repo\n    level: admin")]
    // tool requiring the null level
    [InlineData("version: 1\nscopes:\n  - name: repo\n    levels: [none, read]\ntools:\n  - name: x.y\n    scope: repo\n    level: none")]
    // duplicate tool
    [InlineData("version: 1\nscopes:\n  - name: repo\n    levels: [none, read]\ntools:\n  - name: x.y\n    scope: repo\n    level: read\n  - name: x.y\n    scope: repo\n    level: read")]
    public void Malformed_catalog_fails_to_load_rather_than_loading_empty(string yaml)
    {
        var ex = Assert.Throws<PolicyViolationException>(() => ToolCatalog.FromYaml(yaml));
        Assert.Equal("tool-catalog", ex.Stage);
    }
}
