using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Harness.Policy;

/// <summary>One declared external MCP connector: a namespaced toolset the platform may mount.</summary>
/// <param name="Namespace">The toolset namespace; its tools are named <c>&lt;namespace&gt;.&lt;operation&gt;</c>.</param>
/// <param name="Description">Free-text description, or <see langword="null"/> when the config omits it.</param>
/// <param name="WriteCapable">Whether this connector's tools may attach to a write-capable node.</param>
/// <param name="Operations">The per-operation allowlist — the only operations that may be mounted.</param>
public sealed record ConnectorDeclaration(
    string Namespace,
    string? Description,
    bool WriteCapable,
    IReadOnlyList<string> Operations);

/// <summary>
/// The MCP connector registry (M7c): the declarative allowlist that decides which external MCP
/// toolsets the platform may mount, and — for each — exactly which operations, and whether they may
/// attach to a write-capable agent. A new toolset is <c>connectors.yaml</c> + review, never code
/// (product-vision §5a). This is the control that keeps MCP extensibility inside the audit and
/// policy story: an operation not on a connector's allowlist is never mounted, and an unreviewed
/// (or read-only) connector never reaches a write-capable node.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed throughout, matching <see cref="RepoAllowlist"/> and <see cref="PolicyFloor"/>. An
/// <b>absent</b> <c>connectors.yaml</c> is not an error — it simply means "no connectors declared",
/// yielding an empty registry that mounts nothing. A <b>present-but-malformed</b> file — bad YAML, a
/// duplicate namespace, a blank/empty namespace, a namespace that is not a single lowercase
/// identifier segment, a blank operation, or a namespace that shadows a built-in — throws at load,
/// so a broken config stops startup rather than silently permitting.
/// </para>
/// <para>
/// Deny-by-default everywhere: an empty or omitted <c>connectors:</c> list permits <b>no</b>
/// connector (never "allow all"), and <see cref="IsDeclared"/>/<see cref="IsAllowed"/>/
/// <see cref="IsWriteCapable"/> all return <see langword="false"/> for an unknown namespace or
/// operation. The registry only <em>narrows</em>: it adds no tool and no capability (invariant 1),
/// and a connector namespace may never equal a curated built-in namespace (invariant 7).
/// </para>
/// </remarks>
public sealed class McpConnectorRegistry
{
    internal const string Stage = "mcp-connector-registry";

    /// <summary>
    /// Built-in tool namespaces a connector may never claim, whatever the YAML says (invariants 1
    /// and 7). A connector that shadowed one of these could re-route a curated, catalogued tool
    /// name to an external, unreviewed server — so the guarantee is codified here rather than left
    /// to nobody ever editing the data file. Keep in sync with the platform's built-in toolsets
    /// (github.*, repo.*, codesearch.*).
    /// </summary>
    private static readonly HashSet<string> ReservedNamespaces =
        new(StringComparer.OrdinalIgnoreCase) { "github", "repo", "codesearch" };

    /// <summary>A namespace / operation segment: a single lowercase identifier. Lowercase letters,
    /// digits and underscore; must start with a letter. No dot, slash, backslash, whitespace or
    /// uppercase — those would let a namespace span segments or shadow a built-in. Bounded length
    /// keeps an adversarial config entry cheap to validate.</summary>
    private static readonly Regex Identifier = new(
        @"^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly Dictionary<string, ConnectorDeclaration> _byNamespace; // ns → declaration
    private readonly Dictionary<string, HashSet<string>> _operations;       // ns → allowed operations
    private readonly ConnectorDeclaration[] _connectors;

    private McpConnectorRegistry(
        Dictionary<string, ConnectorDeclaration> byNamespace,
        Dictionary<string, HashSet<string>> operations,
        ConnectorDeclaration[] connectors)
    {
        _byNamespace = byNamespace;
        _operations = operations;
        _connectors = connectors;
    }

    /// <summary>The declared connectors, in declaration order. Empty when nothing is declared.</summary>
    public IReadOnlyList<ConnectorDeclaration> Connectors => _connectors;

    /// <summary>
    /// Reads and parses a <c>connectors.yaml</c> from disk. A <b>missing</b> file is not an error —
    /// an absent connectors file means "no connectors declared", so an empty registry (that mounts
    /// nothing) is returned. A file that is present but unreadable or malformed is a fail-closed
    /// block.
    /// </summary>
    /// <exception cref="PolicyViolationException">The file exists but cannot be read, or its
    /// contents are malformed.</exception>
    public static McpConnectorRegistry FromFile(string path)
    {
        if (!File.Exists(path))
            return Empty();

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(
                Stage, $"connectors file '{path}' exists but could not be read: {ex.Message}");
        }

        return FromYaml(text);
    }

    /// <summary>
    /// Builds a registry from <c>connectors.yaml</c> text. A YAML defect or any malformed connector
    /// declaration is a fail-closed block. An empty or omitted <c>connectors:</c> list yields an
    /// empty registry that mounts nothing.
    /// </summary>
    /// <exception cref="PolicyViolationException">The YAML will not parse, or a declaration is malformed.</exception>
    public static McpConnectorRegistry FromYaml(string yaml)
    {
        ConnectorsFile? file;
        try
        {
            file = PolicyData.Deserializer.Deserialize<ConnectorsFile>(yaml);
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(Stage, $"connectors.yaml is not valid YAML: {ex.Message}");
        }

        var byNamespace = new Dictionary<string, ConnectorDeclaration>(StringComparer.OrdinalIgnoreCase);
        var operations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ConnectorDeclaration>();

        foreach (var raw in file?.Connectors ?? [])
        {
            if (raw is null)
                throw new PolicyViolationException(
                    Stage, "connectors.yaml contains an empty connector declaration");

            var ns = raw.Namespace?.Trim();
            if (string.IsNullOrEmpty(ns))
                throw new PolicyViolationException(
                    Stage, "a connector declares a blank namespace; a blank namespace is malformed");
            // Reserved-namespace check first, and case-insensitively, so a case-variant of a
            // built-in (e.g. "GitHub") is reported as a shadowing attempt rather than merely a
            // casing defect — the more informative message for a reviewer.
            if (ReservedNamespaces.Contains(ns))
                throw new PolicyViolationException(
                    Stage,
                    $"connector namespace '{ns}' shadows a curated built-in toolset; a connector " +
                    "must never re-route a built-in tool namespace");
            if (!Identifier.IsMatch(ns))
                throw new PolicyViolationException(
                    Stage,
                    $"connector namespace '{ns}' is not a single lowercase identifier segment " +
                    "(lowercase letters, digits and underscore; no dot, slash or whitespace)");

            // Trim, reject blanks, de-duplicate the per-operation allowlist. An empty operations
            // list is permitted at load (a declared-but-mount-nothing connector); IsAllowed then
            // denies every operation for it, consistent with deny-by-default.
            var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var opOrder = new List<string>();
            foreach (var rawOp in raw.Operations ?? [])
            {
                var op = rawOp?.Trim();
                if (string.IsNullOrEmpty(op))
                    throw new PolicyViolationException(
                        Stage,
                        $"connector '{ns}' has a blank operation in its allowlist; a blank operation is malformed");
                if (!Identifier.IsMatch(op))
                    throw new PolicyViolationException(
                        Stage,
                        $"connector '{ns}' operation '{op}' is not a single lowercase identifier segment");
                if (ops.Add(op)) opOrder.Add(op);
            }

            var declaration = new ConnectorDeclaration(
                ns, raw.Description?.Trim(), raw.WriteCapable, opOrder.AsReadOnly());

            if (!byNamespace.TryAdd(ns, declaration))
                throw new PolicyViolationException(
                    Stage, $"connectors.yaml declares namespace '{ns}' twice");
            operations[ns] = ops;
            ordered.Add(declaration);
        }

        return new McpConnectorRegistry(byNamespace, operations, ordered.ToArray());
    }

    /// <summary>An empty registry — no connectors declared, so it mounts nothing.</summary>
    public static McpConnectorRegistry Empty() => new(
        new Dictionary<string, ConnectorDeclaration>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
        []);

    /// <summary>True when <paramref name="ns"/> is a declared connector namespace. False for
    /// <see langword="null"/>, blank, or unknown (deny-by-default).</summary>
    public bool IsDeclared(string? ns) =>
        !string.IsNullOrWhiteSpace(ns) && _byNamespace.ContainsKey(ns.Trim());

    /// <summary>
    /// True when <paramref name="ns"/> is a declared connector <b>and</b> <paramref name="operation"/>
    /// is on that connector's per-operation allowlist. False for <see langword="null"/>/blank, an
    /// unknown namespace, or an operation that connector did not declare (deny-by-default).
    /// </summary>
    public bool IsAllowed(string? ns, string? operation)
    {
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(operation))
            return false;
        return _operations.TryGetValue(ns.Trim(), out var ops) && ops.Contains(operation.Trim());
    }

    /// <summary>
    /// True when a declared connector's tools may attach to a write-capable node. False for an
    /// unknown namespace and for any read-only connector — an unreviewed or read-only connector
    /// never reaches a write-capable agent (product-vision §5a).
    /// </summary>
    public bool IsWriteCapable(string? ns) =>
        !string.IsNullOrWhiteSpace(ns)
        && _byNamespace.TryGetValue(ns.Trim(), out var declaration)
        && declaration.WriteCapable;

    /// <summary>The declaration for <paramref name="ns"/>, or <see langword="false"/> when unknown.</summary>
    public bool TryGet(string? ns, [NotNullWhen(true)] out ConnectorDeclaration? declaration)
    {
        declaration = null;
        if (string.IsNullOrWhiteSpace(ns)) return false;
        return _byNamespace.TryGetValue(ns.Trim(), out declaration);
    }

    /// <summary>
    /// Splits a namespaced tool name into its namespace and operation on the <b>first</b> dot —
    /// <c>docs.search</c> → (<c>docs</c>, <c>search</c>). This is how a caller (e.g. the tool
    /// registry) tells a connector tool from a built-in and looks it up here. Returns
    /// <see langword="false"/> for <see langword="null"/>/blank, a name with no dot, or a name with
    /// an empty namespace or operation part. Splitting on the first dot lets an operation itself
    /// contain a dot; the namespace never can (a namespace is a single identifier segment).
    /// </summary>
    public static bool TryParseToolName(
        string? toolName,
        [NotNullWhen(true)] out string? ns,
        [NotNullWhen(true)] out string? op)
    {
        ns = null;
        op = null;
        if (string.IsNullOrWhiteSpace(toolName)) return false;

        var trimmed = toolName.Trim();
        var dot = trimmed.IndexOf('.');
        if (dot <= 0 || dot >= trimmed.Length - 1) return false;   // no dot, or an empty part

        var nsPart = trimmed[..dot];
        var opPart = trimmed[(dot + 1)..];
        if (nsPart.Length == 0 || opPart.Length == 0) return false;

        ns = nsPart;
        op = opPart;
        return true;
    }

    // ---- YAML shape (deserialization only) ----

    private sealed class ConnectorsFile
    {
        public List<ConnectorFile?>? Connectors { get; set; }
    }

    private sealed class ConnectorFile
    {
        public string? Namespace { get; set; }
        public string? Description { get; set; }
        public bool WriteCapable { get; set; }   // default false
        public List<string?>? Operations { get; set; }
    }
}
