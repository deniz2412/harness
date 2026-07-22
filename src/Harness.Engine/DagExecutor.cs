using Harness.Contracts;

namespace Harness.Engine;

/// <summary>
/// Topologically executes a workflow DAG. Fail-closed in both directions:
/// a node failure halts the run in a resumable state, and a node carrying <c>gate: human</c>
/// never executes without a recorded approval — an undecided gate pauses the run (a clean stop,
/// not an exception) until a decision is written to the <see cref="IApprovalStore"/>.
/// Every status transition is persisted through <see cref="IRunStore"/> so a paused or in-flight
/// run is observable from the API, not just a terminated one.
/// </summary>
public sealed class DagExecutor(
    IEnumerable<INodeExecutor> executors,
    IAuditLog audit,
    IApprovalStore approvals,
    IRunStore runs)
{
    private readonly Dictionary<string, INodeExecutor> _byKind =
        executors.ToDictionary(e => e.Kind, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<NodeDefinition> TopologicalOrder(WorkflowDefinition wf)
    {
        var order = new List<NodeDefinition>();
        var visited = new Dictionary<string, int>(); // 0=unseen 1=visiting 2=done
        var byId = wf.Nodes.ToDictionary(n => n.Id);

        void Visit(NodeDefinition n)
        {
            visited.TryGetValue(n.Id, out var state);
            if (state == 2) return;
            if (state == 1) throw new InvalidOperationException($"Cycle detected at node '{n.Id}'.");
            visited[n.Id] = 1;
            foreach (var dep in n.DependsOn) Visit(byId[dep]);
            visited[n.Id] = 2;
            order.Add(n);
        }

        foreach (var n in wf.Nodes) Visit(n);
        return order;
    }

    /// <summary>
    /// Runs the workflow, or resumes it. A run handed in as <see cref="RunStatus.AwaitingApproval"/>
    /// is a resume: the outputs of the nodes that already completed are read back from the audit
    /// trail, those nodes are skipped, and execution continues at the gate that paused it.
    /// Terminal runs are never re-entered.
    /// </summary>
    public async Task ExecuteAsync(Run run, WorkflowDefinition wf, CancellationToken ct)
    {
        if (run.Status is RunStatus.Completed or RunStatus.Rejected) return;

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

        // Resume is only offered from a gate pause. A failed or policy-blocked run is not silently
        // continued — deciding whether re-running a failed node is safe is a human call.
        if (run.Status is RunStatus.AwaitingApproval)
            foreach (var (node, output) in await audit.ReadNodeOutputsAsync(run.Id, ct))
                outputs[node] = output;

        await TransitionAsync(run, RunStatus.Running, ct);

        foreach (var node in TopologicalOrder(wf))
        {
            // Present in the map before the node has run ⇒ it completed before the pause.
            if (outputs.ContainsKey(node.Id)) continue;

            if (IsHumanGate(node))
            {
                var approval = await approvals.GetAsync(run.Id, node.Id, ct);
                switch (approval?.Decision)
                {
                    case GateDecision.Approved:
                        await audit.EmitAsync(run.Id, "gate_decision", node.Id,
                            $"approved by {approval.Approver ?? "unknown"}{Reason(approval)}", ct);
                        break;

                    case GateDecision.Rejected:
                        await audit.EmitAsync(run.Id, "gate_decision", node.Id,
                            $"rejected by {approval.Approver ?? "unknown"}{Reason(approval)}", ct);
                        run.FinishedAt = DateTimeOffset.UtcNow;
                        await TransitionAsync(run, RunStatus.Rejected, ct);
                        return;

                    default: // no record, or still Pending — pause, do not execute the node
                        await approvals.RequestAsync(run.Id, node.Id, ct);
                        await audit.EmitAsync(run.Id, "gate_request", node.Id,
                            $"awaiting human approval for node '{node.Id}' ({node.Kind}); approvers: "
                            + (node.Approvers.Count > 0 ? string.Join(", ", node.Approvers) : "initiator"), ct);
                        await TransitionAsync(run, RunStatus.AwaitingApproval, ct);
                        return;
                }
            }

            await audit.EmitAsync(run.Id, "node_start", node.Id, node.Kind, ct);

            if (!_byKind.TryGetValue(node.Kind, out var executor))
                throw new InvalidOperationException($"No executor registered for kind '{node.Kind}'.");

            var result = await executor.ExecuteAsync(new NodeContext(run, wf, node, outputs), ct);

            // The payload is the node's actual output, not a literal "ok": it is both the record a
            // reviewer reads and the source the resume path rebuilds upstream outputs from.
            // A failure is tagged so it can never be mistaken for a completed node's output.
            await audit.EmitAsync(run.Id, "node_end", node.Id,
                result.Success ? result.Output : $"failed: {result.Output}", ct);

            if (!result.Success)
            {
                run.FinishedAt = DateTimeOffset.UtcNow;
                await TransitionAsync(run, RunStatus.Failed, ct);
                return;
            }
            outputs[node.Id] = result.Output;
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        await TransitionAsync(run, RunStatus.Completed, ct);
    }

    private static bool IsHumanGate(NodeDefinition node) =>
        string.Equals(node.Gate, "human", StringComparison.OrdinalIgnoreCase);

    private static string Reason(GateApproval a) =>
        string.IsNullOrWhiteSpace(a.Reason) ? "" : $": {a.Reason}";

    private async Task TransitionAsync(Run run, RunStatus status, CancellationToken ct)
    {
        run.Status = status;
        await runs.SaveAsync(run, ct);
    }
}
