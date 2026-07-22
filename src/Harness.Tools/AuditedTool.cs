using System.Text.Json;
using Harness.Audit;
using Harness.Policy;
using Microsoft.Extensions.AI;

namespace Harness.Tools;

/// <summary>
/// The one seam where a tool call meets the platform's guarantees, wrapped around every catalog
/// tool before the agent ever sees it.
///
/// M0 shipped none of this, as the exit check found: the pre-tool policy stage could not fail, tool
/// results reached the model unscanned, and the PR comment — the single externally visible write in
/// the whole workflow — left no audit event at all (invariant 5). All three failures shared this
/// point, so the fix is one wrapper rather than three.
/// </summary>
internal sealed class AuditedTool(
    AIFunction inner,
    string toolName,
    ToolCallContext ctx,
    PolicyPipeline policy,
    AuditEmitter audit) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string args;
        try
        {
            // Pre-tool, checked at the call and not only at resolve time: the node must declare
            // the tool and the workflow ceiling must permit it.
            policy.AssertToolAllowed(toolName, ctx.NodeTools, ctx.WorkflowPermissions);

            args = Describe(arguments);
            policy.ScanOutbound("pre-tool", args);

            // Before, not after: an externally visible write must be on the record even if the
            // call then fails, times out, or the process dies mid-flight.
            await audit.EmitAsync(ctx.RunId, "tool_call", ctx.NodeId,
                payload: $"tool={toolName}\n{args}", cancellationToken);
        }
        catch (Exception platformFailure)
        {
            // A blocked or unrecordable call must end the run, not become a hint the model routes
            // around — see ToolCallContext for why re-throwing here is not enough on its own.
            ctx.Latch(platformFailure);
            throw;
        }

        // Outside the latch: a 404, a bad path or a malformed argument is the world being messy,
        // not a broken guarantee. The model is allowed to see those and try again.
        var result = await base.InvokeCoreAsync(arguments, cancellationToken);

        try
        {
            // Tool results are untrusted content — a diff, a file, an issue body — on its way into
            // the model's context. ScanOutbound covered the prompt and the final output, never this.
            var text = result as string ?? JsonSerializer.Serialize(result);
            policy.ScanOutbound("post-tool", text);

            // The outcome is what makes the write attributable: for github.pr_comment this is the
            // URL of the comment the run posted.
            await audit.EmitAsync(ctx.RunId, "tool_result", ctx.NodeId,
                payload: $"tool={toolName}\n{text}", cancellationToken);
        }
        catch (Exception platformFailure)
        {
            ctx.Latch(platformFailure);
            throw;
        }

        return result;
    }

    /// <summary>
    /// Arguments as an auditable, scannable string. If they cannot be rendered they cannot be
    /// scanned or recorded either, so the call is blocked rather than allowed through unexamined.
    /// </summary>
    private static string Describe(AIFunctionArguments arguments)
    {
        try
        {
            return JsonSerializer.Serialize(
                arguments.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""));
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(
                "pre-tool", $"tool arguments could not be rendered for scanning ({ex.GetType().Name})");
        }
    }
}
