using Harness.Contracts;

namespace Harness.Engine;

public sealed record NodeContext(
    Run Run,
    WorkflowDefinition Workflow,
    NodeDefinition Node,
    IReadOnlyDictionary<string, string> UpstreamOutputs);

public sealed record NodeResult(bool Success, string Output);

public interface INodeExecutor
{
    /// <summary>Node kind this executor handles: agent | agent-loop | bash | gate.</summary>
    string Kind { get; }
    Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct);
}
