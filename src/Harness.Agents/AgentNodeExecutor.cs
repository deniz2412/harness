using Harness.Audit;
using Harness.Engine;
using Harness.Policy;
using Harness.Tools;

namespace Harness.Agents;

/// <summary>
/// Executes 'agent' workflow nodes: one turn of a MAF AIAgent built from the node's versioned
/// prompt and only the tools the node lists, pointed at the model gateway (never a provider
/// directly). The materialise-and-run mechanics — model tiering, the sandbox-aware tool context,
/// the fail-closed latch, the pre-model and pre-write scans — live in <see cref="AgentInvoker"/>,
/// shared with the agent-loop so both behave identically.
/// </summary>
public sealed class AgentNodeExecutor(
    GatewayOptions gateway, ToolRegistry tools, PolicyPipeline policy,
    AuditEmitter audit, string promptsDir) : INodeExecutor
{
    public string Kind => "agent";

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var prompt = await File.ReadAllTextAsync(Path.Combine(promptsDir, ctx.Node.PromptRef!), ct);
        var input = AgentInvoker.BuildInput(ctx);
        var model = AgentInvoker.ModelFor(ctx, gateway);

        // One turn, fresh session; a read-only agent node passes its (null) Runner straight through.
        var (output, _) = await AgentInvoker.RunTurnAsync(
            ctx, prompt, input, model, session: null, gateway, tools, policy, audit, ct);

        return new NodeResult(true, output);
    }
}
