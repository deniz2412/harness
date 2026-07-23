using Harness.Contracts;

namespace Harness.Engine;

public sealed record NodeContext(
    Run Run,
    WorkflowDefinition Workflow,
    NodeDefinition Node,
    IReadOnlyDictionary<string, string> UpstreamOutputs,
    /// <summary>
    /// The run's sandbox, present only for write-capable runs — workflows whose permission ceiling
    /// declares <c>repo: write-worktree</c>. <c>bash</c> and <c>agent-loop</c> validation act
    /// through it; read-only runs (e.g. pr-review) leave it null and behave exactly as before.
    /// A write-path node that finds it null must fail closed.
    /// </summary>
    IRunnerSession? Runner = null);

public sealed record NodeResult(bool Success, string Output);

public interface INodeExecutor
{
    /// <summary>Node kind this executor handles: agent | agent-loop | bash | gate.</summary>
    string Kind { get; }
    Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct);
}
