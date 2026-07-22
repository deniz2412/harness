using Harness.Audit;
using Harness.Contracts;

namespace Harness.Engine;

/// <summary>Topologically executes a workflow DAG. Fail-closed: any error halts the run.</summary>
public sealed class DagExecutor(IEnumerable<INodeExecutor> executors, AuditEmitter audit)
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

    public async Task ExecuteAsync(Run run, WorkflowDefinition wf, CancellationToken ct)
    {
        var outputs = new Dictionary<string, string>();
        run.Status = RunStatus.Running;

        foreach (var node in TopologicalOrder(wf))
        {
            await audit.EmitAsync(run.Id, "node_start", node.Id, payload: node.Kind, ct);

            if (!_byKind.TryGetValue(node.Kind, out var executor))
                throw new InvalidOperationException($"No executor registered for kind '{node.Kind}'.");

            var result = await executor.ExecuteAsync(new NodeContext(run, wf, node, outputs), ct);

            await audit.EmitAsync(run.Id, "node_end", node.Id,
                payload: result.Success ? "ok" : "failed", ct);

            if (!result.Success)
            {
                run.Status = RunStatus.Failed;   // resumable state — M2 adds resume
                run.FinishedAt = DateTimeOffset.UtcNow;
                return;
            }
            outputs[node.Id] = result.Output;
        }

        run.Status = RunStatus.Completed;
        run.FinishedAt = DateTimeOffset.UtcNow;
    }
}
