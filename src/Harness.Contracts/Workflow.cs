namespace Harness.Contracts;

/// <summary>Declarative workflow definition, loaded from workflows/*.yaml.</summary>
public sealed class WorkflowDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Permission ceiling for every node (e.g. repo: read, github: comment).</summary>
    public Dictionary<string, string> Permissions { get; init; } = [];
    public required List<NodeDefinition> Nodes { get; init; }

    /// <summary>
    /// Content hash of the definition and every prompt it references, stamped by the loader.
    /// This is what pins a run to the exact workflow+prompt versions that produced it — the audit
    /// chain proves events were not tampered with, this proves *what definition* ran.
    /// Set by the loader, never present in the YAML (Contracts stays dependency-free).
    /// </summary>
    public string Sha { get; set; } = "";
}

public sealed class NodeDefinition
{
    public required string Id { get; init; }
    /// <summary>agent | agent-loop | bash | gate</summary>
    public required string Kind { get; init; }
    public List<string> DependsOn { get; init; } = [];
    /// <summary>The node's persona prompt. Inline, OR populated by the loader from an <see cref="AgentRef"/>.
    /// Settable so the loader can merge a resolved agent's prompt in (M7b).</summary>
    public string? PromptRef { get; set; }
    public List<string> Tools { get; init; } = [];
    /// <summary>M7b — reference a named agent from the registry (agents/&lt;name&gt;.yaml) instead of
    /// spelling out prompt_ref/tools/model_tier inline. Mutually exclusive with those inline fields; the
    /// loader resolves it and merges the agent's prompt, tools, tier and output schema into this node.</summary>
    public string? AgentRef { get; init; }
    /// <summary>M7b — the gateway model group for this node: cheap | strong. Inline, OR populated from an
    /// agent. Null falls back to the node-id heuristic (AgentInvoker.ModelFor).</summary>
    public string? ModelTier { get; set; }
    /// <summary>auto | human — writes require a gate.</summary>
    public string? Gate { get; init; }
    public string? OutputSchema { get; set; }
    public string? Run { get; init; }              // bash nodes
    public string? Until { get; init; }            // agent-loop
    public int MaxIterations { get; init; } = 5;   // agent-loop bound
    public bool FreshContext { get; init; }
    public List<string> Approvers { get; init; } = [];
}
