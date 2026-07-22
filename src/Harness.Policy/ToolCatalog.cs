using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Harness.Policy;

/// <summary>A permission scope and its levels, ordered least- to most-privileged.</summary>
public sealed class PermissionScope
{
    internal PermissionScope(string name, string description, IReadOnlyList<string> levels)
    {
        Name = name;
        Description = description;
        Levels = levels;
    }

    public string Name { get; }
    public string Description { get; }

    /// <summary>Ordered; index is the rank. e.g. repo: none &lt; read &lt; write-worktree.</summary>
    public IReadOnlyList<string> Levels { get; }

    /// <summary>Rank of a level, or -1 when the string is not a level of this scope (⇒ deny).</summary>
    public int Rank(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return -1;
        var trimmed = level.Trim();
        for (var i = 0; i < Levels.Count; i++)
            if (string.Equals(Levels[i], trimmed, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

/// <summary>One curated tool and the permission it requires.</summary>
public sealed record CatalogTool(string Name, string Scope, string Level, string Description)
{
    public override string ToString() => $"{Name} ({Scope}:{Level})";
}

/// <summary>
/// The curated tool catalog (<c>Rules/tool-catalog.yaml</c>, embedded): tool name → required
/// (scope, level), plus the ordered level lattice each workflow's <c>permissions:</c> ceiling is
/// expressed in. This is the authoritative mapping — <c>Harness.Tools</c> resolves against it
/// rather than keeping a second copy of the same knowledge.
/// </summary>
public sealed class ToolCatalog
{
    /// <summary>
    /// Names no catalog may contain, whatever the YAML says (invariants 1 and 7). Codifying it
    /// here means the guarantee does not rest on nobody ever editing the data file.
    /// </summary>
    private static readonly Regex ForbiddenToolName = new(
        @"(?i)(?:^|[._])(?:merge|create_repo|repo_create|delete_repo|repo_delete|create_repository|delete_repository)(?:$|[._])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Lazy<ToolCatalog> LazyDefault =
        new(() => FromYaml(PolicyData.ReadResource(PolicyData.ToolCatalogResource, "tool-catalog")),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, CatalogTool> _tools;
    private readonly Dictionary<string, PermissionScope> _scopes;

    private ToolCatalog(Dictionary<string, CatalogTool> tools, Dictionary<string, PermissionScope> scopes)
    {
        _tools = tools;
        _scopes = scopes;
    }

    /// <summary>The embedded catalog. Throws (permanently) if it is missing or malformed.</summary>
    public static ToolCatalog Default => LazyDefault.Value;

    /// <summary>Tool name → requirement. Case-insensitive; the YAML uses the workflow spelling.</summary>
    public IReadOnlyDictionary<string, CatalogTool> Tools => _tools;

    /// <summary>Scope name → ordered levels.</summary>
    public IReadOnlyDictionary<string, PermissionScope> Scopes => _scopes;

    /// <summary>Every catalogued tool name, in catalog order.</summary>
    public IReadOnlyCollection<string> ToolNames => _tools.Values.Select(t => t.Name).ToArray();

    public bool TryGetTool(string? name, [NotNullWhen(true)] out CatalogTool? tool)
    {
        tool = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _tools.TryGetValue(name.Trim(), out tool);
    }

    public bool TryGetScope(string? name, [NotNullWhen(true)] out PermissionScope? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _scopes.TryGetValue(name.Trim(), out scope);
    }

    /// <summary>Builds a catalog from YAML. Any defect — unknown scope, unknown level, duplicate
    /// name, forbidden name — is a block, not a skipped entry.</summary>
    public static ToolCatalog FromYaml(string yaml)
    {
        const string stage = "tool-catalog";

        CatalogFile? file;
        try
        {
            file = PolicyData.Deserializer.Deserialize<CatalogFile>(yaml);
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(stage, $"tool catalog is not valid YAML: {ex.Message}");
        }

        if (file?.Scopes is not { Count: > 0 })
            throw new PolicyViolationException(stage, "tool catalog declares no permission scopes");
        if (file.Tools is not { Count: > 0 })
            throw new PolicyViolationException(stage, "tool catalog declares no tools");

        var scopes = new Dictionary<string, PermissionScope>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in file.Scopes)
        {
            var name = raw.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new PolicyViolationException(stage, "tool catalog contains a scope without a name");
            if (raw.Levels is not { Count: > 1 })
                throw new PolicyViolationException(stage, $"scope '{name}' must declare at least two levels");
            var levels = raw.Levels.Select(l => l?.Trim() ?? "").ToArray();
            if (levels.Any(string.IsNullOrEmpty))
                throw new PolicyViolationException(stage, $"scope '{name}' has an empty level");
            if (levels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != levels.Length)
                throw new PolicyViolationException(stage, $"scope '{name}' has duplicate levels");
            if (!scopes.TryAdd(name, new PermissionScope(name, raw.Description?.Trim() ?? "", levels)))
                throw new PolicyViolationException(stage, $"tool catalog declares scope '{name}' twice");
        }

        var tools = new Dictionary<string, CatalogTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in file.Tools)
        {
            var name = raw.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new PolicyViolationException(stage, "tool catalog contains a tool without a name");
            if (ForbiddenToolName.IsMatch(name))
                throw new PolicyViolationException(
                    stage,
                    $"tool '{name}' names a forbidden capability (merge, or repository create/delete); " +
                    "the platform has no such tool by design");

            var scopeName = raw.Scope?.Trim();
            if (!scopes.TryGetValue(scopeName ?? "", out var scope))
                throw new PolicyViolationException(
                    stage, $"tool '{name}' declares unknown permission scope '{scopeName}'");

            var level = raw.Level?.Trim();
            if (scope.Rank(level) < 0)
                throw new PolicyViolationException(
                    stage, $"tool '{name}' declares level '{level}', which is not a level of scope '{scope.Name}'");
            if (string.Equals(level, scope.Levels[0], StringComparison.OrdinalIgnoreCase))
                throw new PolicyViolationException(
                    stage, $"tool '{name}' requires the null level '{level}'; a tool must require some permission");

            if (!tools.TryAdd(name, new CatalogTool(name, scope.Name, level!, raw.Description?.Trim() ?? "")))
                throw new PolicyViolationException(stage, $"tool catalog declares tool '{name}' twice");
        }

        return new ToolCatalog(tools, scopes);
    }

    // ---- YAML shapes (deserialization only) ----

    private sealed class CatalogFile
    {
        public int Version { get; set; }
        public List<ScopeFile>? Scopes { get; set; }
        public List<ToolFile>? Tools { get; set; }
    }

    private sealed class ScopeFile
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Levels { get; set; }
    }

    private sealed class ToolFile
    {
        public string? Name { get; set; }
        public string? Scope { get; set; }
        public string? Level { get; set; }
        public string? Description { get; set; }
    }
}
