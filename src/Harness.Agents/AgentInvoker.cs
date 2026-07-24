using System.ClientModel;
using Harness.Audit;
using Harness.Engine;
using Harness.Policy;
using Harness.Tools;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;

namespace Harness.Agents;

/// <summary>
/// The one place a node's MAF <see cref="AIAgent"/> is materialised and run: the node's versioned
/// prompt, only the tools it lists (∩ ceiling, via <see cref="ToolRegistry"/>), an OpenAI-compatible
/// client pointed at the gateway (never a provider directly), and the run's sandbox threaded through
/// the <see cref="ToolCallContext"/>. Shared by <see cref="AgentNodeExecutor"/> (one turn) and the
/// agent-loop's iteration boundary (many turns), so both police, audit and fail-closed identically.
/// </summary>
internal static class AgentInvoker
{
    /// <summary>
    /// The model-bound input for a node: the run's coordinates plus the outputs of the nodes it
    /// depends on. Everything here is scanned pre-model by the caller.
    /// </summary>
    public static string BuildInput(NodeContext ctx)
    {
        var upstream = string.Join("\n\n", ctx.UpstreamOutputs
            .Where(kv => ctx.Node.DependsOn.Contains(kv.Key))
            .Select(kv => $"## Output of '{kv.Key}'\n{kv.Value}"));
        return $"Repository: {ctx.Run.Repo}  PR: {ctx.Run.PullRequest}  Issue: {ctx.Run.Issue}\n\n{upstream}";
    }

    /// <summary>
    /// Model tiering, config-driven, never hardcoded models. M7b: a node's declared tier wins — set
    /// inline or merged from a referenced agent (cheap|strong → the gateway model groups). When no tier
    /// is declared (null), fall back to the original node-id heuristic: cheap for the gather/plan
    /// reconnaissance nodes, strong for review/implement work.
    /// </summary>
    public static string ModelFor(NodeContext ctx, GatewayOptions gateway) =>
        ctx.Node.ModelTier switch
        {
            "cheap" => gateway.CheapModel,
            "strong" => gateway.StrongModel,
            _ => ctx.Node.Id is "gather" or "plan" ? gateway.CheapModel : gateway.StrongModel,
        };

    /// <summary>
    /// Runs one agent turn. <paramref name="session"/> carries conversation state for a continued
    /// loop; null starts a fresh one. Fail-closed: a policy/platform failure latched during a tool
    /// call is re-thrown here — the agent's own loop can absorb a refusal and "finish", but the node
    /// must not — and the output is scanned pre-write before any downstream tool can post it.
    /// </summary>
    public static async Task<(string Output, AgentSession Session)> RunTurnAsync(
        NodeContext ctx, string prompt, string input, string model, AgentSession? session,
        GatewayOptions gateway, ToolRegistry tools, PolicyPipeline policy, AuditEmitter audit,
        CancellationToken ct)
    {
        policy.ScanOutbound("pre-model", prompt + input);

        var client = new OpenAIClient(
            new ApiKeyCredential(gateway.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(gateway.BaseUrl) });

        // Tools carry the node's identity, permission envelope and the run's sandbox with them, so
        // each call is policed and audited at the point of invocation. Runner is null for read-only
        // nodes and non-null for write-path ones — the tool decides what that permits.
        var toolCtx = new ToolCallContext(
            ctx.Run.Id, ctx.Node.Id, ctx.Node.Tools, ctx.Workflow.Permissions, ctx.Runner, ctx.Run.Repo);

        AIAgent agent = client.GetChatClient(model).AsAIAgent(
            instructions: prompt,
            name: ctx.Node.Id,
            tools: tools.Resolve(toolCtx));

        // A null session means a fresh conversation — the M2 agent-loop default. A continued loop
        // hands back the session it got so the next iteration keeps the history.
        session ??= await agent.CreateSessionAsync(ct);

        await audit.EmitAsync(ctx.Run.Id, "model_call", ctx.Node.Id,
            payload: $"model={model} tools=[{string.Join(",", ctx.Node.Tools)}]", ct);

        var response = await agent.RunAsync(input, session, cancellationToken: ct);

        toolCtx.ThrowIfLatched();

        var output = response.Text;
        policy.ScanOutbound("pre-write", output);
        return (output, session);
    }
}
