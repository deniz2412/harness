namespace Harness.Contracts;

/// <summary>
/// Declarative agent definition, loaded from agents/*.yaml (M7b — the named agent registry).
/// Makes an agent first-class: a persona prompt, the tools it may use, its model tier, and an
/// optional output schema are defined once and referenced from any workflow node via
/// <c>agent_ref</c>, instead of being spelled out inline on every node. Agents stay bounded to
/// runs — this is data the loader resolves, never a standing autonomous agent.
/// </summary>
public sealed class AgentDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The persona prompt; resolves under prompts/ exactly like a node's prompt_ref.</summary>
    public required string PromptRef { get; init; }

    /// <summary>The tools this agent may use — YAML catalog names like <c>github.pr_diff</c>, <c>repo.read</c>.</summary>
    public List<string> Tools { get; init; } = [];

    /// <summary>
    /// The gateway model group this agent runs against: <c>cheap</c> or <c>strong</c> (matching
    /// appsettings Gateway:CheapModel/StrongModel, which are literally "cheap"/"strong"). Never a
    /// provider or concrete model — the gateway is the only path to models (invariant 3).
    /// </summary>
    public required string ModelTier { get; init; }

    /// <summary>Optional schema ref for the agent's structured output, like NodeDefinition.OutputSchema.</summary>
    public string? OutputSchema { get; init; }

    /// <summary>
    /// Content hash of the agent definition and the prompt it references, stamped by the loader.
    /// This lets a workflow that references the agent fold the agent's exact identity into the run's
    /// pin — a run proves not only which workflow ran, but which agent version it ran with.
    /// Set by the loader, never present in the YAML (Contracts stays dependency-free).
    /// </summary>
    public string Sha { get; set; } = "";
}
