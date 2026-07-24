using Xunit;

namespace Harness.Engine.Tests;

/// <summary>
/// The agent catalog layers M7 team namespaces over a flat layout: a team file overrides a same-named
/// default which overrides a same-named flat agent, without moving files. It must fail closed —
/// unresolvable names and traversal attempts throw — and never look outside the agents root. Every
/// fixture is built in a temp dir; nothing here touches the repo's real agents/.
/// </summary>
public sealed class AgentCatalogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "harness-agent-catalog-tests", Guid.NewGuid().ToString("N"));

    public AgentCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AgentCatalog Catalog() => new(_root);

    /// <summary>Writes a yaml file under the root at a '/'-separated relative path, creating dirs.</summary>
    private string Write(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"name: {Path.GetFileNameWithoutExtension(path)}\n");
        return path;
    }

    [Fact]
    public void Flat_agent_resolves_for_back_compat()
    {
        var expected = Write("security-reviewer.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("security-reviewer"));
    }

    [Fact]
    public void Default_agent_resolves()
    {
        var expected = Write("defaults/security-reviewer.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("security-reviewer"));
    }

    [Fact]
    public void Team_agent_resolves()
    {
        var expected = Write("teams/payments/security-reviewer.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("security-reviewer", team: "payments"));
    }

    [Fact]
    public void Default_beats_flat()
    {
        Write("security-reviewer.yaml");
        var expected = Write("defaults/security-reviewer.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("security-reviewer"));
    }

    [Fact]
    public void Team_beats_default_and_flat()
    {
        Write("security-reviewer.yaml");
        Write("defaults/security-reviewer.yaml");
        var expected = Write("teams/payments/security-reviewer.yaml");
        Assert.Equal(expected, Catalog().ResolvePath("security-reviewer", team: "payments"));
    }

    [Fact]
    public void Team_falls_back_to_default_when_team_has_no_override()
    {
        var expected = Write("defaults/security-reviewer.yaml");
        Write("teams/payments/something-else.yaml");
        // Team given but no payments/security-reviewer.yaml → falls through to the default.
        Assert.Equal(expected, Catalog().ResolvePath("security-reviewer", team: "payments"));
    }

    [Fact]
    public void Unknown_agent_throws()
    {
        Write("security-reviewer.yaml");
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
        Write("teams/payments/security-reviewer.yaml");
        Assert.Throws<ArgumentException>(() => Catalog().ResolvePath("security-reviewer", team));
    }

    [Fact]
    public void TryResolve_returns_true_with_path_when_found_and_false_otherwise()
    {
        var expected = Write("security-reviewer.yaml");

        Assert.True(Catalog().TryResolve("security-reviewer", null, out var path));
        Assert.Equal(expected, path);

        Assert.False(Catalog().TryResolve("missing", null, out var none));
        Assert.Equal(string.Empty, none);

        // A traversal attempt is a non-resolution, not an unhandled throw.
        Assert.False(Catalog().TryResolve("../escape", null, out _));
    }

    [Fact]
    public void ResolveName_round_trips_to_the_loader_name()
    {
        Write("security-reviewer.yaml");
        Write("defaults/coverage-triager.yaml");
        Write("teams/payments/nightly-scan.yaml");

        var catalog = Catalog();
        // Root-relative, forward-slashed, no .yaml — exactly what AgentLoader.Load takes.
        Assert.Equal("security-reviewer", catalog.ResolveName("security-reviewer"));
        Assert.Equal("defaults/coverage-triager", catalog.ResolveName("coverage-triager"));
        Assert.Equal("teams/payments/nightly-scan", catalog.ResolveName("nightly-scan", team: "payments"));
    }

    [Fact]
    public void ResolveName_throws_when_nothing_matches()
    {
        Assert.Throws<FileNotFoundException>(() => Catalog().ResolveName("does-not-exist"));
    }

    [Fact]
    public void EnumerateAll_finds_flat_default_and_team_entries()
    {
        Write("flat-only.yaml");
        Write("defaults/default-only.yaml");
        Write("teams/payments/team-only.yaml");

        var all = Catalog().EnumerateAll();

        Assert.Contains(all, r => r is { Name: "flat-only", Team: null, Scope: AgentScope.Flat });
        Assert.Contains(all, r => r is { Name: "default-only", Team: null, Scope: AgentScope.Default });
        Assert.Contains(all, r => r is { Name: "team-only", Team: "payments", Scope: AgentScope.Team });
    }

    [Fact]
    public void EnumerateAll_surfaces_both_sides_of_an_override_pair()
    {
        Write("defaults/security-reviewer.yaml");
        Write("teams/payments/security-reviewer.yaml");

        var all = Catalog().EnumerateAll();
        var reviewer = all.Where(r => r.Name == "security-reviewer").ToList();

        // Override relationship is visible: the default AND the team copy both appear.
        Assert.Equal(2, reviewer.Count);
        Assert.Contains(reviewer, r => r is { Scope: AgentScope.Default, Team: null });
        Assert.Contains(reviewer, r => r is { Scope: AgentScope.Team, Team: "payments" });
    }

    [Fact]
    public void EnumerateAll_dedupes_identical_name_team_pairs()
    {
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
        Assert.Equal(AgentScope.Flat, all[0].Scope);
    }

    [Fact]
    public void EnumerateAll_is_empty_for_an_empty_root()
    {
        Assert.Empty(Catalog().EnumerateAll());
    }
}
