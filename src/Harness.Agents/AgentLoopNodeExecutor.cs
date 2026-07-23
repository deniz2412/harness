using Harness.Audit;
using Harness.Engine;
using Harness.Policy;
using Harness.Tools;
using Microsoft.Agents.AI;

namespace Harness.Agents;

/// <summary>
/// The model boundary of one agent-loop iteration, seamed so the loop is unit-testable without a
/// live gateway. <paramref name="thread"/> carries continued-context state (null starts fresh); the
/// returned thread is reused on the next iteration when the node continues context.
/// </summary>
internal interface IAgentIteration
{
    Task<(string Text, object? Thread)> RunAsync(NodeContext ctx, object? thread, CancellationToken ct);
}

/// <summary>Production iteration: materialises and runs the node's MAF agent via <see cref="AgentInvoker"/>.</summary>
internal sealed class GatewayAgentIteration(
    GatewayOptions gateway, ToolRegistry tools, PolicyPipeline policy,
    AuditEmitter audit, string promptsDir) : IAgentIteration
{
    public async Task<(string Text, object? Thread)> RunAsync(
        NodeContext ctx, object? thread, CancellationToken ct)
    {
        var prompt = await File.ReadAllTextAsync(Path.Combine(promptsDir, ctx.Node.PromptRef!), ct);
        var input = AgentInvoker.BuildInput(ctx);
        var model = AgentInvoker.ModelFor(ctx, gateway);

        var (text, produced) = await AgentInvoker.RunTurnAsync(
            ctx, prompt, input, model, (AgentSession?)thread, gateway, tools, policy, audit, ct);
        return (text, produced);
    }
}

/// <summary>
/// Executes <c>kind: agent-loop</c> nodes: iterate an agent, up to <c>max_iterations</c>, until the
/// <c>until</c> predicate holds. M2 supports exactly one predicate, <c>validation_pass</c> — after
/// each agent turn the node's <c>run</c> validation command executes in the run's sandbox and exit 0
/// ends the loop with success. Fail-closed throughout: an unrecognised predicate, a missing
/// validation command, a missing sandbox, or exhausting every iteration without a pass all FAIL the
/// node — the loop never decides an attempt was "good enough" and proceeds.
/// </summary>
public sealed class AgentLoopNodeExecutor : INodeExecutor
{
    private readonly IAgentIteration _agent;
    private readonly IAuditLog _audit;

    /// <summary>Production wiring — Program.cs registers this from DI (see integration notes).</summary>
    public AgentLoopNodeExecutor(
        GatewayOptions gateway, ToolRegistry tools, PolicyPipeline policy,
        AuditEmitter modelAudit, IAuditLog audit, string promptsDir)
        : this(new GatewayAgentIteration(gateway, tools, policy, modelAudit, promptsDir), audit) { }

    /// <summary>Test seam — inject a fake agent boundary and audit log; no gateway, no DB.</summary>
    internal AgentLoopNodeExecutor(IAgentIteration agent, IAuditLog audit)
    {
        _agent = agent;
        _audit = audit;
    }

    public string Kind => "agent-loop";

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        // Only validation_pass is understood in M2. An unknown predicate fails closed rather than
        // looping on a condition we cannot evaluate (which would either never end or falsely pass).
        if (!string.Equals(ctx.Node.Until, "validation_pass", StringComparison.OrdinalIgnoreCase))
            return new NodeResult(false,
                $"agent-loop 'until' predicate '{ctx.Node.Until}' is not supported (only 'validation_pass')");

        var validation = ctx.Node.Run;
        if (string.IsNullOrWhiteSpace(validation))
            return new NodeResult(false,
                "agent-loop with until: validation_pass needs a 'run' validation command");

        // A write-path node in a write-capable workflow; no sandbox means misconfiguration.
        if (ctx.Runner is null)
            return new NodeResult(false,
                "agent-loop needs a runner session, but none was created for this run");

        var max = ctx.Node.MaxIterations;
        if (max < 1)
            return new NodeResult(false, $"agent-loop max_iterations must be at least 1 (was {max})");

        object? thread = null;
        for (var i = 1; i <= max; i++)
        {
            await _audit.EmitAsync(ctx.Run.Id, "iteration_start", ctx.Node.Id, $"iteration {i} of {max}", ct);

            // Fresh context (the M2 default) starts each iteration with no carried messages: the
            // shared worktree, not the conversation, is what accumulates progress across attempts.
            // Continued context reuses the thread the previous turn produced.
            var incoming = ctx.Node.FreshContext ? null : thread;
            var (_, produced) = await _agent.RunAsync(ctx, incoming, ct);
            if (!ctx.Node.FreshContext) thread = produced;

            var result = await ctx.Runner.RunAsync(validation, ct);
            await _audit.EmitAsync(ctx.Run.Id, "tool_result", ctx.Node.Id,
                $"iteration {i} validation: exit={result.ExitCode}", ct);

            if (result.Success)
                return new NodeResult(true, $"validation passed on iteration {i} of {max}");
        }

        // Bound exhausted without a pass. This is the invariant: an agent-loop that cannot satisfy
        // its predicate fails the node — it never falls through to the next node regardless.
        return new NodeResult(false,
            $"agent-loop exhausted {max} iterations without passing validation '{validation}'");
    }
}
