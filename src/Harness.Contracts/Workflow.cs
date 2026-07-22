namespace Harness.Contracts;

/// <summary>Declarative workflow definition, loaded from workflows/*.yaml.</summary>
public sealed class WorkflowDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Permission ceiling for every node (e.g. repo: read, github: comment).</summary>
    public Dictionary<string, string> Permissions { get; init; } = [];
    public required List<NodeDefinition> Nodes { get; init; }
}

public sealed class NodeDefinition
{
    public required string Id { get; init; }
    /// <summary>agent | agent-loop | bash | gate</summary>
    public required string Kind { get; init; }
    public List<string> DependsOn { get; init; } = [];
    public string? PromptRef { get; init; }
    public List<string> Tools { get; init; } = [];
    /// <summary>auto | human — writes require a gate.</summary>
    public string? Gate { get; init; }
    public string? OutputSchema { get; init; }
    public string? Run { get; init; }              // bash nodes
    public string? Until { get; init; }            // agent-loop
    public int MaxIterations { get; init; } = 5;   // agent-loop bound
    public bool FreshContext { get; init; }
    public List<string> Approvers { get; init; } = [];
}
