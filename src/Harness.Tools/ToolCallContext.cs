using System.Runtime.ExceptionServices;
using Harness.Contracts;

namespace Harness.Tools;

/// <summary>
/// The per-node context a tool call is policed and audited against. Tools are resolved once per
/// node execution, so the run/node identity and the permission envelope are known up front — the
/// agent only chooses <em>when</em> to call, never with what authority.
///
/// It also carries the node's fail-closed latch. The function-invoking loop underneath MAF treats
/// an exception from a tool as recoverable: it hands the error back to the model and lets it try
/// again, up to <c>MaximumConsecutiveErrorsPerRequest</c> (3) consecutive failures. That is right
/// for a missing file and wrong for a policy block — it would turn "this call is forbidden" into a
/// hint to try something else. So a platform failure is latched here and re-thrown by the executor
/// once the agent returns, and the node fails regardless of what the loop did with it.
/// </summary>
public sealed class ToolCallContext(
    Guid runId,
    string nodeId,
    IReadOnlyCollection<string> nodeTools,
    IReadOnlyDictionary<string, string> workflowPermissions,
    IRunnerSession? runner = null,
    string? repo = null)
{
    /// <summary>Run the call belongs to; every tool event is attributed to it.</summary>
    public Guid RunId { get; } = runId;

    /// <summary>
    /// The run's target repository as <c>owner/name</c> (from the run request). GitHub tools bind to
    /// this per run — M3 un-binds them from the single startup-configured repo — so a run acts on
    /// the repo it was launched against, within the allowlist. Null only for contexts that predate a
    /// repo (none in normal execution).
    /// </summary>
    public string? Repo { get; } = repo;

    /// <summary>Node whose declared tool list bounds this call.</summary>
    public string NodeId { get; } = nodeId;

    /// <summary>Tools the node declares in the workflow YAML.</summary>
    public IReadOnlyCollection<string> NodeTools { get; } = nodeTools;

    /// <summary>The workflow's `permissions:` ceiling — the outer bound on every node.</summary>
    public IReadOnlyDictionary<string, string> WorkflowPermissions { get; } = workflowPermissions;

    /// <summary>
    /// The run's sandbox, present only for write-capable nodes. Write tools (repo.write_worktree,
    /// github.push_branch) act through it; a read-only workflow leaves it null. A write tool that
    /// finds it null must fail closed — a write with nowhere isolated to happen does not happen.
    /// </summary>
    public IRunnerSession? Runner { get; } = runner;

    /// <summary>First platform failure seen during this node, if any. Null means clean.</summary>
    public Exception? Latched { get; private set; }

    /// <summary>
    /// Latches a failure of a platform guarantee — a policy block, or an audit emit that did not
    /// happen. Only the first is kept: it is the one that explains the run.
    /// </summary>
    public void Latch(Exception failure) => Latched ??= failure;

    /// <summary>
    /// Re-throws the latched failure with its original type and stack, so a swallowed policy block
    /// still lands on the run as a policy block rather than as a generic node failure.
    /// </summary>
    public void ThrowIfLatched()
    {
        if (Latched is not null) ExceptionDispatchInfo.Capture(Latched).Throw();
    }
}
