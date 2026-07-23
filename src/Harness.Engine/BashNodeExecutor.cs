namespace Harness.Engine;

/// <summary>
/// Executes <c>kind: bash</c> nodes: runs <c>node.Run</c> deterministically inside the run's
/// sandbox (no AI, no tools). Fail-closed on every degenerate case —
/// a missing runner session, an empty command, or a non-zero exit all fail the node so the run
/// halts resumably rather than proceeding on a command that never ran or failed. The captured exit
/// code and output become the node output so a reviewer sees exactly why it passed or failed.
/// </summary>
public sealed class BashNodeExecutor(IAuditLog audit) : INodeExecutor
{
    public string Kind => "bash";

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var command = ctx.Node.Run;
        if (string.IsNullOrWhiteSpace(command))
            return new NodeResult(false, "bash node has no 'run' command to execute");

        // A bash node is a write-path node: it only exists in a write-capable workflow, which is the
        // one that gets a sandbox. No session means the run is misconfigured — fail closed rather
        // than shelling out on the host.
        if (ctx.Runner is null)
            return new NodeResult(false, "bash node needs a runner session, but none was created for this run");

        await audit.EmitAsync(ctx.Run.Id, "tool_call", ctx.Node.Id, $"bash: {command}", ct);

        var result = await ctx.Runner.RunAsync(command, ct);
        var output = Format(result);

        await audit.EmitAsync(ctx.Run.Id, "tool_result", ctx.Node.Id, output, ct);

        // Exit 0 ⇒ node succeeds. Any non-zero exit fails the node (fail-closed), carrying the
        // captured output forward as the failure reason.
        return new NodeResult(result.Success, output);
    }

    private static string Format(Contracts.CommandResult r)
    {
        var text = $"exit={r.ExitCode}";
        if (!string.IsNullOrEmpty(r.Stdout)) text += $"\nstdout:\n{r.Stdout}";
        if (!string.IsNullOrEmpty(r.Stderr)) text += $"\nstderr:\n{r.Stderr}";
        return text;
    }
}
