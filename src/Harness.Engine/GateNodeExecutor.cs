namespace Harness.Engine;

/// <summary>
/// Executes <c>kind: gate</c> nodes. The approval MECHANICS live in <see cref="DagExecutor"/>,
/// which inspects a node's <c>gate:</c> attribute before ANY node runs and pauses/resumes/rejects
/// on the recorded decision. So by the time this executor runs, the human decision (if the node
/// carried <c>gate: human</c>) has already been made and recorded — this is a deliberate
/// pass-through that marks the checkpoint cleared, never a second place approval is decided.
/// </summary>
public sealed class GateNodeExecutor : INodeExecutor
{
    public string Kind => "gate";

    public Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct) =>
        Task.FromResult(new NodeResult(true, "gate cleared"));
}
