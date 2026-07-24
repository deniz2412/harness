namespace Harness.Engine;

/// <summary>The layer a resolved agent file came from, most-specific first.</summary>
public enum AgentScope
{
    /// <summary>A team override: <c>agents/teams/&lt;team&gt;/&lt;name&gt;.yaml</c>.</summary>
    Team,
    /// <summary>A shipped default: <c>agents/defaults/&lt;name&gt;.yaml</c>.</summary>
    Default,
    /// <summary>A flat, pre-namespace agent: <c>agents/&lt;name&gt;.yaml</c>.</summary>
    Flat
}

/// <summary>
/// One resolvable agent file. <paramref name="Team"/> is non-null only for <see cref="AgentScope.Team"/>.
/// </summary>
public sealed record AgentRef(string Name, string? Team, string Path, AgentScope Scope);

/// <summary>
/// Resolves an agent reference (name + optional team) to a concrete file path, layering M7 team
/// namespaces on top of a flat layout WITHOUT moving any files — the exact mirror of
/// <see cref="WorkflowCatalog"/>, rooted at <c>agents/</c> instead of <c>workflows/</c>. Precedence,
/// most-specific first:
/// <list type="number">
///   <item>team override: <c>agents/teams/&lt;team&gt;/&lt;name&gt;.yaml</c> (only when a team is given),</item>
///   <item>shipped default: <c>agents/defaults/&lt;name&gt;.yaml</c>,</item>
///   <item>flat / back-compat: <c>agents/&lt;name&gt;.yaml</c>.</item>
/// </list>
/// Fail-closed (invariant 2): a name that resolves nowhere throws <see cref="FileNotFoundException"/>,
/// exactly as <see cref="AgentLoader.Load"/> does; a <c>name</c>/<c>team</c> that is not a single
/// path segment, or that escapes <paramref name="agentsRoot"/>, throws before touching disk. Missing
/// <c>teams/</c> or <c>defaults/</c> directories are not errors — they simply resolve nothing.
///
/// Comparisons are ordinal everywhere (segment validation, override dedup): the override relationship
/// is decided on exact bytes, never a culture-folded match. File existence itself is left to the OS,
/// so a name's case must match the file on a case-sensitive filesystem just as the loader requires.
/// </summary>
public sealed class AgentCatalog
{
    private readonly string _root;

    public AgentCatalog(string agentsRoot)
    {
        if (string.IsNullOrWhiteSpace(agentsRoot))
            throw new ArgumentException("Agents root is required.", nameof(agentsRoot));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(agentsRoot));
    }

    private string TeamsDir => Path.Combine(_root, "teams");
    private string DefaultsDir => Path.Combine(_root, "defaults");

    /// <summary>
    /// Resolves <paramref name="name"/> (optionally within <paramref name="team"/>) to an absolute
    /// path per the class precedence, or throws <see cref="FileNotFoundException"/> if nothing matches.
    /// </summary>
    public string ResolvePath(string name, string? team = null)
    {
        var segment = ValidateSegment(name, nameof(name));
        var teamSegment = team is null ? null : ValidateSegment(team, nameof(team));
        var fileName = $"{segment}.yaml";

        if (teamSegment is not null)
        {
            var teamPath = SafeCombine(Path.Combine(TeamsDir, teamSegment), fileName);
            if (File.Exists(teamPath)) return teamPath;
        }

        var defaultPath = SafeCombine(DefaultsDir, fileName);
        if (File.Exists(defaultPath)) return defaultPath;

        var flatPath = SafeCombine(_root, fileName);
        if (File.Exists(flatPath)) return flatPath;

        var forWhom = teamSegment is null ? $"'{segment}'" : $"'{segment}' (team '{teamSegment}')";
        throw new FileNotFoundException($"Unknown agent {forWhom}", flatPath);
    }

    /// <summary>
    /// The loader name — the path relative to the agents root, without the <c>.yaml</c> extension and
    /// forward-slashed — for the file that (<paramref name="name"/>, <paramref name="team"/>) resolves
    /// to under the class precedence. This is what <see cref="AgentLoader.Load"/> takes, so a resume
    /// re-resolves the identical file (a team override selected at start is pinned, not re-decided).
    /// Throws <see cref="FileNotFoundException"/> when nothing matches — fail-closed like <see cref="ResolvePath"/>.
    /// </summary>
    public string ResolveName(string name, string? team = null)
    {
        var rel = Path.GetRelativePath(_root, ResolvePath(name, team));
        return rel[..^".yaml".Length].Replace('\\', '/');
    }

    /// <summary>Non-throwing <see cref="ResolvePath"/>: returns false instead of throwing.</summary>
    public bool TryResolve(string name, string? team, out string path)
    {
        try
        {
            path = ResolvePath(name, team);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            path = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Every resolvable agent across the flat root, <c>defaults/</c>, and every <c>teams/&lt;team&gt;/</c>.
    /// When a team file shadows a default/flat of the same name, BOTH entries are surfaced (distinct
    /// scopes) so tooling can see the override relationship — this is deliberate and not deduplicated
    /// away. Only identical <c>(name, team)</c> pairs are deduped (a given file appears once). Ordered
    /// by scope (Team, Default, Flat) then name then team, all ordinal, so the listing is stable.
    /// </summary>
    public IReadOnlyList<AgentRef> EnumerateAll()
    {
        var seen = new HashSet<(string Name, string? Team)>();
        var refs = new List<AgentRef>();

        void Add(string name, string? team, string path, AgentScope scope)
        {
            if (seen.Add((name, team))) refs.Add(new AgentRef(name, team, path, scope));
        }

        // Team overrides first (most specific).
        if (Directory.Exists(TeamsDir))
            foreach (var teamDir in Directory.EnumerateDirectories(TeamsDir))
            {
                var team = Path.GetFileName(teamDir);
                foreach (var file in EnumerateYaml(teamDir))
                    Add(NameOf(file), team, file, AgentScope.Team);
            }

        if (Directory.Exists(DefaultsDir))
            foreach (var file in EnumerateYaml(DefaultsDir))
                Add(NameOf(file), null, file, AgentScope.Default);

        // Flat root, excluding the teams/ and defaults/ subtrees themselves.
        foreach (var file in EnumerateYaml(_root))
            Add(NameOf(file), null, file, AgentScope.Flat);

        return refs
            .OrderBy(r => r.Scope)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Team ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> EnumerateYaml(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly)
            : Enumerable.Empty<string>();

    private static string NameOf(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// An agent/team reference must be a single path segment. Reject separators, traversal, rooted
    /// paths, and anything that resolves outside the root — the same guard shape as
    /// <see cref="AgentLoader"/>'s prompt-ref check, applied before any file is touched.
    /// </summary>
    private string ValidateSegment(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} is required.", paramName);

        var trimmed = value.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains("..")
            || Path.IsPathRooted(trimmed) || trimmed != Path.GetFileName(trimmed))
            throw new ArgumentException(
                $"{paramName} '{value}' must be a single path segment (no separators or traversal).", paramName);

        return trimmed;
    }

    /// <summary>
    /// Combines under <paramref name="baseDir"/> and re-checks the result stays inside the root — a
    /// defence-in-depth mirror of <see cref="AgentLoader"/>'s traversal guard, so even a segment that
    /// slipped the character check cannot escape.
    /// </summary>
    private string SafeCombine(string baseDir, string fileName)
    {
        var full = Path.GetFullPath(Path.Combine(baseDir, fileName));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{fileName}' resolves outside the agents root.");
        return full;
    }
}
