using System.ClientModel;
using Harness.Audit;
using Harness.Engine;
using Harness.Policy;
using Harness.Tools;
using Microsoft.Agents.AI;
using OpenAI;

namespace Harness.Agents;

/// <summary>
/// Executes 'agent' workflow nodes: builds a MAF AIAgent with the node's versioned prompt and
/// only the tools the node lists, pointed at the model gateway (never a provider directly).
/// NOTE (M0): verify against MAF 1.6.1 API on first `dotnet build` — extension names may drift.
/// </summary>
public sealed class AgentNodeExecutor(
    GatewayOptions gateway, ToolRegistry tools, PolicyPipeline policy,
    AuditEmitter audit, string promptsDir) : INodeExecutor
{
    public string Kind => "agent";

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var prompt = await File.ReadAllTextAsync(Path.Combine(promptsDir, ctx.Node.PromptRef!), ct);

        // Upstream node outputs become context; everything model-bound is policy-scanned first.
        var upstream = string.Join("\n\n", ctx.UpstreamOutputs
            .Where(kv => ctx.Node.DependsOn.Contains(kv.Key))
            .Select(kv => $"## Output of '{kv.Key}'\n{kv.Value}"));
        var input = $"Repository: {ctx.Run.Repo}  PR: {ctx.Run.PullRequest}  Issue: {ctx.Run.Issue}\n\n{upstream}";
        policy.ScanOutbound("pre-model", prompt + input);

        var model = ctx.Node.Id is "gather" or "plan" ? gateway.CheapModel : gateway.StrongModel;
        var client = new OpenAIClient(
            new ApiKeyCredential(gateway.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(gateway.BaseUrl) });

        AIAgent agent = client.GetChatClient(model).CreateAIAgent(
            instructions: prompt,
            tools: [.. tools.Resolve(ctx.Node.Tools)]);

        await audit.EmitAsync(ctx.Run.Id, "model_call", ctx.Node.Id,
            payload: $"model={model} tools=[{string.Join(",", ctx.Node.Tools)}]", ct);

        var response = await agent.RunAsync(input, cancellationToken: ct);
        var output = response.Text;

        // Pre-write scan: node outputs may be posted externally by downstream tools.
        policy.ScanOutbound("pre-write", output);

        return new NodeResult(true, output);
    }
}
