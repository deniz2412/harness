using Xunit;

namespace Harness.Engine.Tests;

/// <summary>
/// The catalog layers M7 team namespaces over the existing flat layout: a team file overrides a
/// same-named default which overrides a same-named flat workflow, all without moving files. It must
/// fail closed — unresolvable names and traversal attempts throw — and it must never look outside the
/// workflows root. Every fixture is built in a temp dir; nothing here touches the repo's real workflows/.
/// </summary>
public sealed class WorkflowCatalogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "harness-catalog-tests", Guid.NewGuid().ToString("N"));

    public WorkflowCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WorkflowCatalog Catalog() => new(_root);

    /// <summary>Writes a yaml file under the root at a '/'-separated relative path, creating dirs.</summary>
    private string Write(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"name: {Path.GetFileNameWithoutExtension(path)}\nnodes: []\n");
        return path;
    }

    [Fact]
    public void Flat_workflow_resolves_for_back_compat()
    {
        var expected = Write("pr-review.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("pr-review"));
    }

    [Fact]
    public void Default_workflow_resolves()
    {
        var expected = Write("defaults/coverage-gap-analysis.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("coverage-gap-analysis"));
    }

    [Fact]
    public void Team_workflow_resolves()
    {
        var expected = Write("teams/payments/nightly-scan.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("nightly-scan", team: "payments"));
    }

    [Fact]
    public void Default_beats_flat()
    {
        Write("pr-review.yaml");
        var expected = Write("defaults/pr-review.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("pr-review"));
    }

    [Fact]
    public void Team_beats_default_and_flat()
    {
        Write("pr-review.yaml");
        Write("defaults/pr-review.yaml");
        var expected = Write("teams/payments/pr-review.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("pr-review", team: "payments"));
    }

    [Fact]
    public void Team_falls_back_to_default_when_team_has_no_override()
    {
        var expected = Write("defaults/pr-review.yaml");
        Write("teams/payments/something-else.yaml");
        // Team given but no payments/pr-review.yaml → falls through to the default.
        Assert.Equal(expected, Catalog().ResolvePath("pr-review", team: "payments"));
    }

    [Fact]
    public void Unknown_workflow_throws()
    {
        Write("pr-review.yaml");
        Assert.Throws<FileNotFoundException>(() => Catalog().ResolvePath("does-not-exist"));
        // Team given but neither the team nor the flat/default layers have this name → still throws.
        Assert.Throws<FileNotFoundException>(() => Catalog().ResolvePath("does-not-exist", team: "no-such-team"));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    public void Traversal_in_name_throws(string name)
    {
        Assert.Throws<ArgumentException>(() => Catalog().ResolvePath(name));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void Traversal_in_team_throws(string team)
    {
        Write("teams/payments/pr-review.yaml");
        Assert.Throws<ArgumentException>(() => Catalog().ResolvePath("pr-review", team));
    }

    [Fact]
    public void TryResolve_returns_true_with_path_when_found_and_false_otherwise()
    {
        var expected = Write("pr-review.yaml");

        Assert.True(Catalog().TryResolve("pr-review", null, out var path));
        Assert.Equal(expected, path);

        Assert.False(Catalog().TryResolve("missing", null, out var none));
        Assert.Equal(string.Empty, none);

        // A traversal attempt is a non-resolution, not an unhandled throw.
        Assert.False(Catalog().TryResolve("../escape", null, out _));
    }

    [Fact]
    public void EnumerateAll_finds_flat_default_and_team_entries()
    {
        Write("flat-only.yaml");
        Write("defaults/default-only.yaml");
        Write("teams/payments/team-only.yaml");

        var all = Catalog().EnumerateAll();

        Assert.Contains(all, r => r is { Name: "flat-only", Team: null, Scope: WorkflowScope.Flat });
        Assert.Contains(all, r => r is { Name: "default-only", Team: null, Scope: WorkflowScope.Default });
        Assert.Contains(all, r => r is { Name: "team-only", Team: "payments", Scope: WorkflowScope.Team });
    }

    [Fact]
    public void EnumerateAll_surfaces_both_sides_of_an_override_pair()
    {
        Write("defaults/pr-review.yaml");
        Write("teams/payments/pr-review.yaml");

        var all = Catalog().EnumerateAll();
        var prReview = all.Where(r => r.Name == "pr-review").ToList();

        // Override relationship is visible: the default AND the team copy both appear.
        Assert.Equal(2, prReview.Count);
        Assert.Contains(prReview, r => r is { Scope: WorkflowScope.Default, Team: null });
        Assert.Contains(prReview, r => r is { Scope: WorkflowScope.Team, Team: "payments" });
    }

    [Fact]
    public void EnumerateAll_dedupes_identical_name_team_pairs()
    {
        // Only one physical file per (name, team) can exist; confirm a plain listing has no dupes.
        Write("a.yaml");
        Write("defaults/b.yaml");
        Write("teams/x/c.yaml");

        var all = Catalog().EnumerateAll();
        Assert.Equal(all.Select(r => (r.Name, r.Team)).Distinct().Count(), all.Count);
    }

    [Fact]
    public void EnumerateAll_does_not_crash_when_teams_and_defaults_dirs_are_missing()
    {
        Write("only-flat.yaml"); // no teams/ or defaults/ directories created

        var all = Catalog().EnumerateAll();
        Assert.Single(all);
        Assert.Equal("only-flat", all[0].Name);
        Assert.Equal(WorkflowScope.Flat, all[0].Scope);
    }

    [Fact]
    public void EnumerateAll_is_empty_for_an_empty_root()
    {
        Assert.Empty(Catalog().EnumerateAll());
    }
}
