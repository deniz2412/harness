namespace Harness.Policy;

/// <summary>
/// The org/team <b>policy floor</b> (M7): a <c>policy.yaml</c> that fixes the ceiling every workflow
/// authored under it must fit inside. Teams author freely <em>within</em> a boundary the org sets;
/// the engine validates each workflow against this floor at load time (see
/// <see cref="PolicyFloorValidator"/>). This is the control that makes self-service safe in a bank.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed throughout, matching <see cref="RepoAllowlist"/>'s philosophy. A malformed
/// <c>policy.yaml</c> — bad YAML, a negative budget, a repo entry that is not <c>owner/name</c> or
/// <c>owner/*</c>, a blank tool name — throws at load, so a broken policy stops startup rather than
/// silently permitting. An <b>empty</b> (or omitted) <c>allowed_tools</c> permits <b>no</b> tools
/// (deny-all), never "allow everything on error". Loading is deterministic and pure.
/// </para>
/// <para>
/// The floor <b>only restricts</b>. It never adds a capability (invariant 1): it cannot grant a tool
/// the platform does not have, and its tool ceiling is a per-org <em>subset</em> of the platform-wide
/// <see cref="ToolCatalog"/>. Repo matching is <b>not</b> this type's job — the M3
/// <see cref="RepoAllowlist"/> enforces the target repo at run start. The floor merely carries the
/// allowlist as validated data; the budget cap is advisory (enforcement is gateway-side).
/// </para>
/// </remarks>
public sealed class PolicyFloor
{
    internal const string Stage = "policy-floor";

    private readonly HashSet<string> _allowedTools;      // the tool-name ceiling, case-insensitive
    private readonly HashSet<string> _gateRequirements;  // tools that demand a human gate upstream

    /// <summary>
    /// Builds a floor from operator-supplied fields. Any present-but-malformed field is a fail-closed
    /// block. A <see langword="null"/> or empty <paramref name="allowedTools"/> yields a floor that
    /// permits no tools.
    /// </summary>
    /// <exception cref="PolicyViolationException">A tool name is blank, a repo entry is not a valid
    /// <c>owner/name</c>/<c>owner/*</c> reference, or the budget is negative.</exception>
    public PolicyFloor(
        IEnumerable<string>? allowedTools,
        IEnumerable<string>? repoAllowlist,
        IEnumerable<string>? gateRequirements,
        decimal? maxRunBudgetUsd)
    {
        _allowedTools = NormaliseToolNames(allowedTools, "allowed_tools");
        _gateRequirements = NormaliseToolNames(gateRequirements, "gate_requirements");

        // Reuse the M3 allowlist for validation + normalisation: identical shape guarantees, and a
        // malformed entry fails closed exactly as it does at run start. We store the entries only —
        // matching a run target against them is RepoAllowlist's job, not the floor's.
        RepoAllowlist = new RepoAllowlist(repoAllowlist).Entries;

        if (maxRunBudgetUsd is < 0m)
            throw new PolicyViolationException(
                Stage, "max_run_budget_usd must be >= 0; a negative budget cap is malformed");
        MaxRunBudgetUsd = maxRunBudgetUsd;
    }

    /// <summary>The tool-name ceiling. A workflow may name a tool only if it appears here.
    /// Case-insensitive; empty means deny-all.</summary>
    public IReadOnlyCollection<string> AllowedTools => _allowedTools;

    /// <summary>The repo allowlist patterns (<c>owner/name</c> / <c>owner/*</c>), normalised and
    /// de-duplicated. Carried as data for the M3 <see cref="RepoAllowlist"/> to enforce at run start;
    /// the floor does not match run targets itself.</summary>
    public IReadOnlyCollection<string> RepoAllowlist { get; }

    /// <summary>Tools that require a human gate upstream in any workflow that uses them. Represents
    /// exactly what the policy lists — an omitted list means no tool is gate-required (the shipped
    /// policy names the write tools).</summary>
    public IReadOnlyCollection<string> GateRequirements => _gateRequirements;

    /// <summary>Optional advisory per-run budget cap in USD. Enforcement is gateway-side.</summary>
    public decimal? MaxRunBudgetUsd { get; }

    /// <summary>True when the floor permits a workflow to name <paramref name="toolName"/>.
    /// False for null/blank and anything outside the ceiling (deny-all when the ceiling is empty).</summary>
    public bool AllowsTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _allowedTools.Contains(toolName.Trim());

    /// <summary>True when using <paramref name="toolName"/> obliges a human gate upstream of the
    /// using node (per <see cref="GateRequirements"/>).</summary>
    public bool RequiresGate(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _gateRequirements.Contains(toolName.Trim());

    /// <summary>
    /// Builds a floor from <c>policy.yaml</c> text (<c>allowed_tools</c>, <c>repo_allowlist</c>,
    /// <c>gate_requirements</c>, <c>max_run_budget_usd</c>). A YAML defect or any malformed field is a
    /// fail-closed block.
    /// </summary>
    /// <exception cref="PolicyViolationException">The YAML will not parse or a field is malformed.</exception>
    public static PolicyFloor FromYaml(string yaml)
    {
        FloorFile? file;
        try
        {
            file = PolicyData.Deserializer.Deserialize<FloorFile>(yaml);
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(Stage, $"policy.yaml is not valid YAML: {ex.Message}");
        }

        return new PolicyFloor(
            file?.AllowedTools,
            file?.RepoAllowlist,
            file?.GateRequirements,
            file?.MaxRunBudgetUsd);
    }

    /// <summary>Reads and parses a <c>policy.yaml</c> from disk. A missing or unreadable file is a
    /// fail-closed block — a policy that cannot be loaded must stop startup, never widen the ceiling.</summary>
    /// <exception cref="PolicyViolationException">The file is missing/unreadable, or its contents are malformed.</exception>
    public static PolicyFloor FromFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(Stage, $"policy floor '{path}' could not be read: {ex.Message}");
        }

        return FromYaml(text);
    }

    /// <summary>Trims, rejects blanks, and de-duplicates (case-insensitively) a list of tool names.</summary>
    private static HashSet<string> NormaliseToolNames(IEnumerable<string>? names, string field)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new PolicyViolationException(
                    Stage, $"the policy floor's {field} contains a blank tool name; a blank entry is malformed");
            set.Add(raw.Trim());
        }
        return set;
    }

    // ---- YAML shape (deserialization only) ----

    private sealed class FloorFile
    {
        public List<string>? AllowedTools { get; set; }
        public List<string>? RepoAllowlist { get; set; }
        public List<string>? GateRequirements { get; set; }
        public decimal? MaxRunBudgetUsd { get; set; }
    }
}
