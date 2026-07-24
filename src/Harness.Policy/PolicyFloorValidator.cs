using Harness.Contracts;

namespace Harness.Policy;

/// <summary>Which floor rule a <see cref="PolicyFloorViolation"/> reports.</summary>
public enum PolicyFloorRule
{
    /// <summary>A node names a tool outside the floor's <see cref="PolicyFloor.AllowedTools"/> ceiling.</summary>
    ToolCeiling,

    /// <summary>A node names a gate-required tool with no human gate transitively upstream of it.</summary>
    GateRequirement,
}

/// <summary>One way a workflow breaks the floor: the offending node, the rule, and a safe message.
/// Named to pair with <see cref="PolicyFloorException"/>/<see cref="PolicyFloorRule"/> and to stay
/// distinct from the unrelated fail-closed <see cref="PolicyViolationException"/> (secret block /
/// malformed-policy load).</summary>
public sealed record PolicyFloorViolation(string NodeId, PolicyFloorRule Rule, string Message)
{
    public override string ToString() => $"[{Rule}] node '{NodeId}': {Message}";
}

/// <summary>
/// Thrown when a workflow fails the policy floor. Carries every <see cref="Violations">violation</see>
/// so the load path reports all of them at once. A fail-closed block on the load seam: a
/// non-compliant workflow never runs.
/// </summary>
/// <remarks>Standalone rather than a <see cref="PolicyViolationException"/> subclass because that type
/// is sealed; the load path catches this explicitly.</remarks>
public sealed class PolicyFloorException : Exception
{
    public PolicyFloorException(string workflowName, IReadOnlyList<PolicyFloorViolation> violations)
        : base(Format(workflowName, violations))
    {
        WorkflowName = workflowName;
        Violations = violations;
    }

    public string WorkflowName { get; }
    public IReadOnlyList<PolicyFloorViolation> Violations { get; }

    private static string Format(string workflowName, IReadOnlyList<PolicyFloorViolation> violations)
    {
        var body = violations.Count == 0
            ? "(no detail)"
            : string.Join("; ", violations.Select(v => v.ToString()));
        return $"Policy floor block: workflow '{workflowName}' violates the org policy floor: {body}";
    }
}

/// <summary>
/// Validates a workflow against an org <see cref="PolicyFloor"/> at load time (M7). Teams author
/// freely within the floor; this is the check that keeps them inside it.
/// </summary>
/// <remarks>
/// Two rules, both fail-closed:
/// <list type="number">
///   <item><description><b>Tool ceiling</b> — every tool named by every node must be in
///     <see cref="PolicyFloor.AllowedTools"/>.</description></item>
///   <item><description><b>Gate requirement</b> — any node naming a
///     <see cref="PolicyFloor.GateRequirements">gate-required</see> tool must have a
///     <c>gate: human</c> node transitively upstream (via <c>depends_on</c>). This generalises the M2
///     "a human gate precedes the PR-open node" invariant.</description></item>
/// </list>
/// The validator only reports; it never mutates the workflow and never grants a capability.
/// </remarks>
public sealed class PolicyFloorValidator(PolicyFloor floor)
{
    private readonly PolicyFloor _floor = floor;

    /// <summary>Returns every way <paramref name="wf"/> breaks the floor. Empty ⇒ compliant.</summary>
    public IReadOnlyList<PolicyFloorViolation> Validate(WorkflowDefinition wf)
    {
        var violations = new List<PolicyFloorViolation>();
        var byId = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        foreach (var node in wf.Nodes)
            byId[node.Id] = node;   // duplicate ids are a loader concern; last wins here, harmlessly

        foreach (var node in wf.Nodes)
        {
            foreach (var tool in node.Tools)
            {
                // Rule 1: tool ceiling.
                if (!_floor.AllowsTool(tool))
                    violations.Add(new PolicyFloorViolation(
                        node.Id, PolicyFloorRule.ToolCeiling,
                        $"tool '{tool}' is not in the org policy floor's allowed_tools ceiling"));

                // Rule 2: gate requirement.
                if (_floor.RequiresGate(tool) && !HasHumanGateUpstream(node, byId))
                    violations.Add(new PolicyFloorViolation(
                        node.Id, PolicyFloorRule.GateRequirement,
                        $"tool '{tool}' requires a human gate upstream, but no 'gate: human' node is a " +
                        "transitive dependency of this node"));
            }
        }

        return violations;
    }

    /// <summary>Fail-closed convenience for the load path: throws listing every violation, or returns
    /// quietly when the workflow is compliant.</summary>
    /// <exception cref="PolicyFloorException">The workflow breaks the floor.</exception>
    public void EnsureCompliant(WorkflowDefinition wf)
    {
        var violations = Validate(wf);
        if (violations.Count > 0)
            throw new PolicyFloorException(wf.Name, violations);
    }

    /// <summary>
    /// True when some <c>gate: human</c> node is a transitive ancestor (upstream via
    /// <c>depends_on</c>) of <paramref name="node"/>. Walks the dependency edges with a visited set,
    /// so a cyclic <c>depends_on</c> (which the loader does not itself reject) cannot loop forever.
    /// A node is not its own ancestor. O(nodes + edges).
    /// </summary>
    private static bool HasHumanGateUpstream(
        NodeDefinition node, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        foreach (var dep in node.DependsOn) stack.Push(dep);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!visited.Add(id)) continue;            // already seen ⇒ cycle guard
            if (!byId.TryGetValue(id, out var upstream)) continue;   // unknown dep: defensively skip

            if (IsHumanGate(upstream)) return true;
            foreach (var dep in upstream.DependsOn) stack.Push(dep);
        }

        return false;
    }

    private static bool IsHumanGate(NodeDefinition node) =>
        string.Equals(node.Kind, "gate", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(node.Gate, "human", StringComparison.OrdinalIgnoreCase);
}
