using Harness.Contracts;
using YamlDotNet.Serialization;

namespace Harness.Engine;

/// <summary>
/// The inverse of <see cref="WorkflowLoader"/>: turns a <see cref="WorkflowDefinition"/> back into
/// canonical workflow YAML. The F4 visual builder emits with this so that what it produces is the SAME
/// artifact the loader and the workbench consume — the builder is sugar over the YAML, never a second
/// source of truth. Pure: it returns a string, executes nothing and writes nothing.
/// </summary>
/// <remarks>
/// Emits only the fields that are set/meaningful — no loader-stamped <c>sha</c>, no nulls, no empty
/// lists, no non-loop defaults — with snake_case keys matching the loader's
/// <c>UnderscoredNamingConvention</c>. A node that references a named agent (M7b) is emitted as just
/// its <c>agent_ref</c>: the loader MERGES the agent's prompt/tools/tier onto the node at load time and
/// leaves <c>agent_ref</c> set, so emitting the merged inline fields too would be the mutually-exclusive
/// combination the loader rejects. Emitting <c>agent_ref</c> alone round-trips (the loader re-resolves it).
/// </remarks>
public static class WorkflowYamlWriter
{
    // Dictionary keys are written verbatim (the naming convention only renames typed properties, which
    // we do not use here), so the snake_case keys below are canonical.
    private static readonly ISerializer Yaml = new SerializerBuilder().Build();

    public static string ToYaml(WorkflowDefinition wf)
    {
        var root = new Dictionary<string, object?> { ["name"] = wf.Name };
        if (!string.IsNullOrWhiteSpace(wf.Description)) root["description"] = wf.Description;
        if (wf.Permissions.Count > 0)
            root["permissions"] = new Dictionary<string, string>(wf.Permissions);

        var nodes = new List<Dictionary<string, object?>>();
        foreach (var n in wf.Nodes)
        {
            var node = new Dictionary<string, object?> { ["id"] = n.Id, ["kind"] = n.Kind };
            if (n.DependsOn.Count > 0) node["depends_on"] = new List<string>(n.DependsOn);

            if (!string.IsNullOrWhiteSpace(n.AgentRef))
            {
                // agent_ref fully defines the node's agent; the inline fields are the loader's merge.
                node["agent_ref"] = n.AgentRef;
            }
            else
            {
                if (n.PromptRef is not null) node["prompt_ref"] = n.PromptRef;
                if (n.Tools.Count > 0) node["tools"] = new List<string>(n.Tools);
                if (n.ModelTier is not null) node["model_tier"] = n.ModelTier;
                if (n.OutputSchema is not null) node["output_schema"] = n.OutputSchema;
            }

            if (n.Gate is not null) node["gate"] = n.Gate;
            if (n.Run is not null) node["run"] = n.Run;
            if (n.Kind == "agent-loop")
            {
                if (n.Until is not null) node["until"] = n.Until;
                node["max_iterations"] = n.MaxIterations;
                node["fresh_context"] = n.FreshContext;
            }
            if (n.Approvers.Count > 0) node["approvers"] = new List<string>(n.Approvers);

            nodes.Add(node);
        }
        root["nodes"] = nodes;

        return Yaml.Serialize(root);
    }
}
