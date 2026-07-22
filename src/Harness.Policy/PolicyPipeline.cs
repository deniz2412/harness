namespace Harness.Policy;

/// <summary>
/// Fail-closed policy checks. Called pre-model, pre-tool and pre-write.
/// </summary>
/// <remarks>
/// Both dependencies are data-driven and both fail closed on load: constructing the default
/// pipeline throws if the secret ruleset or the tool catalog is missing or malformed, so a broken
/// policy layer stops the process at startup instead of quietly permitting everything.
/// </remarks>
public sealed class PolicyPipeline
{
    /// <summary>Production pipeline: the embedded ruleset and the curated catalog.</summary>
    public PolicyPipeline() : this(SecretScanner.Default, ToolCatalog.Default) { }

    /// <summary>Explicit pipeline, for tests and for future per-tenant rulesets.</summary>
    public PolicyPipeline(SecretScanner scanner, ToolCatalog catalog)
    {
        Scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public SecretScanner Scanner { get; }

    /// <summary>The curated tool catalog: tool name → required (scope, level).</summary>
    public ToolCatalog Catalog { get; }

    /// <summary>
    /// Everything bound for the model, and everything about to be written externally, passes
    /// through here. Throws on a finding, and equally when the scan cannot be completed.
    /// </summary>
    public void ScanOutbound(string stage, string? content)
    {
        if (Scanner.ContainsSecret(content, out var finding, stage) && finding is not null)
            throw new PolicyViolationException(
                stage,
                $"secret detected: {finding.Description} (rule '{finding.RuleId}', offset {finding.Index})",
                finding.RuleId,
                finding.Redacted);
    }

    /// <summary>
    /// Masks every known secret shape in <paramref name="text"/> so callers can log it, audit it
    /// or put it in an error. Withholds the text entirely if it cannot be scanned.
    /// </summary>
    public string Redact(string? text) => Scanner.Redact(text);

    /// <summary>
    /// Pre-tool check. Denies unless the tool is declared on the node, is in the curated catalog,
    /// and the permission it requires is inside the workflow's declared ceiling.
    /// </summary>
    /// <param name="tool">Tool name as written in workflow YAML, e.g. <c>github.pr_diff</c>.</param>
    /// <param name="nodeTools">The <c>tools:</c> list of the node being executed.</param>
    /// <param name="workflowPermissions">The workflow's <c>permissions:</c> ceiling, scope → level.</param>
    /// <remarks>
    /// The whole ceiling is validated, not just the entry this tool needs: a workflow that declares
    /// a scope or level the platform does not know has declared something nobody can enforce, and
    /// an unenforceable ceiling denies. That is also what keeps a future scope (say <c>bash</c>)
    /// from being usable before the catalog is extended by reviewed change.
    /// </remarks>
    /// <exception cref="PolicyViolationException">Any of the three conditions fails.</exception>
    public void AssertToolAllowed(
        string tool,
        IReadOnlyCollection<string> nodeTools,
        IReadOnlyDictionary<string, string> workflowPermissions)
    {
        const string stage = "pre-tool";

        if (string.IsNullOrWhiteSpace(tool))
            throw new PolicyViolationException(stage, "tool name is empty");

        var name = tool.Trim();

        // 1. Declared on this node. (Callers must not pass the node list as the tool source —
        //    that is the M0 defect this replaces.)
        if (nodeTools is null || nodeTools.Count == 0)
            throw new PolicyViolationException(stage, $"tool '{name}' denied: this node declares no tools");
        if (!nodeTools.Any(t => string.Equals(t?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            throw new PolicyViolationException(stage, $"tool '{name}' is not declared on this node");

        // 2. In the curated catalog (invariant 7): unknown ⇒ deny.
        if (!Catalog.TryGetTool(name, out var entry))
            throw new PolicyViolationException(
                stage, $"tool '{name}' is not in the curated tool catalog");

        // 3. Within the workflow's permission ceiling.
        if (workflowPermissions is null || workflowPermissions.Count == 0)
            throw new PolicyViolationException(
                stage, $"tool '{name}' denied: the workflow declares no permission ceiling");

        foreach (var declared in workflowPermissions)
        {
            if (!Catalog.TryGetScope(declared.Key, out var declaredScope))
                throw new PolicyViolationException(
                    stage,
                    $"workflow permission scope '{declared.Key}' is not a recognised scope; " +
                    "an unenforceable ceiling denies");
            if (declaredScope.Rank(declared.Value) < 0)
                throw new PolicyViolationException(
                    stage,
                    $"workflow permission '{declared.Key}: {declared.Value}' is not a recognised level of " +
                    $"scope '{declaredScope.Name}' (expected one of: {string.Join(", ", declaredScope.Levels)})");
        }

        var scope = Catalog.Scopes[entry.Scope];
        var required = scope.Rank(entry.Level);
        if (required < 0)   // unreachable via the catalog loader; still not a pass.
            throw new PolicyViolationException(
                stage, $"tool '{name}' requires level '{entry.Level}', which scope '{scope.Name}' does not define");

        if (!TryGetCeiling(workflowPermissions, scope.Name, out var ceilingLevel))
            throw new PolicyViolationException(
                stage,
                $"tool '{name}' requires '{scope.Name}: {entry.Level}' but the workflow declares no " +
                $"'{scope.Name}' permission; an undeclared scope is not permission");

        var granted = scope.Rank(ceilingLevel);
        if (granted < required)
            throw new PolicyViolationException(
                stage,
                $"tool '{name}' requires '{scope.Name}: {entry.Level}' but the workflow ceiling is " +
                $"'{scope.Name}: {ceilingLevel}'");
    }

    /// <summary>Ceiling lookup is case-insensitive; workflow YAML is hand-written.</summary>
    private static bool TryGetCeiling(
        IReadOnlyDictionary<string, string> permissions, string scope, out string level)
    {
        foreach (var kv in permissions)
        {
            if (!string.Equals(kv.Key?.Trim(), scope, StringComparison.OrdinalIgnoreCase)) continue;
            level = kv.Value?.Trim() ?? "";
            return true;
        }
        level = "";
        return false;
    }
}
