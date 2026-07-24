using System.Security.Cryptography;
using System.Text;
using Harness.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Engine;

/// <summary>
/// Loads a workflow definition and stamps it with a deterministic content hash covering the YAML
/// and every prompt it references — that hash is what pins a run to the exact definition that
/// produced it (spec §2.6). Content, not git: workflows and prompts are mounted read-only into the
/// container from separate directories and git is not available inside it.
/// Fail-closed: a definition that references a prompt which does not exist fails to load rather
/// than failing mid-run.
/// </summary>
public sealed class WorkflowLoader(string workflowsDir, string promptsDir, string? agentsDir = null)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public WorkflowDefinition Load(string name)
    {
        var fileName = $"{name}.yaml";
        var path = Path.Combine(workflowsDir, fileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"Unknown workflow '{name}'", path);

        var wf = Yaml.Deserialize<WorkflowDefinition>(File.ReadAllText(path))
                 ?? throw new InvalidOperationException($"Workflow '{name}' is empty.");
        Validate(wf);

        // The content that pins a run: the workflow file, plus — for any node that references a named
        // agent (M7b) — the resolved agent definition and its prompt, plus every referenced prompt.
        // Keyed by path-tag so the same file counts once (an agent's prompt that is also a node prompt
        // collapses to one entry), and the sha is identical to the pre-M7b scheme for agent-less workflows.
        var content = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"workflow:{fileName.Replace('\\', '/')}"] = File.ReadAllBytes(path),
        };

        ResolveAgentRefs(wf, TeamOf(name), content);   // merges agents into nodes; folds their content in
        foreach (var (rel, full) in ResolvePrompts(wf))
            content[$"prompt:{rel}"] = File.ReadAllBytes(full);

        wf.Sha = ComputeSha(content);
        return wf;
    }

    private static void Validate(WorkflowDefinition wf)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in wf.Nodes)
            if (!ids.Add(n.Id)) throw new InvalidOperationException($"Duplicate node id '{n.Id}'.");

        foreach (var n in wf.Nodes)
        {
            foreach (var d in n.DependsOn)
                if (!ids.Contains(d)) throw new InvalidOperationException($"Node '{n.Id}' depends on unknown node '{d}'.");
            // An agent node is defined either inline (prompt_ref) or by reference (agent_ref) — one of
            // the two, never both (M7b). agent_ref carries the prompt, tools, tier and schema itself.
            var hasAgentRef = !string.IsNullOrWhiteSpace(n.AgentRef);
            if (n.Kind is "agent" or "agent-loop" && n.PromptRef is null && !hasAgentRef)
                throw new InvalidOperationException($"Agent node '{n.Id}' requires prompt_ref or agent_ref.");
            if (hasAgentRef && (n.PromptRef is not null || n.Tools.Count > 0
                                || n.ModelTier is not null || n.OutputSchema is not null))
                throw new InvalidOperationException(
                    $"Node '{n.Id}' sets agent_ref together with inline prompt_ref/tools/model_tier/"
                    + "output_schema; agent_ref fully defines the node's agent, so they are mutually exclusive.");
            // A typo here would otherwise silently downgrade a human gate to no gate at all.
            if (n.Gate is not null && n.Gate is not ("auto" or "human"))
                throw new InvalidOperationException(
                    $"Node '{n.Id}' has gate '{n.Gate}'; expected 'auto' or 'human'.");
        }
    }

    /// <summary>
    /// Maps every referenced prompt to its file, failing the load if one is missing or escapes the
    /// prompts directory. Returns relative-ref → absolute-path, deduplicated.
    /// </summary>
    private IReadOnlyDictionary<string, string> ResolvePrompts(WorkflowDefinition wf)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(promptsDir));
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var n in wf.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.PromptRef)) continue;
            var rel = n.PromptRef.Trim().Replace('\\', '/');
            var full = Path.GetFullPath(Path.Combine(root, rel));

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Node '{n.Id}' prompt_ref '{n.PromptRef}' resolves outside the prompts directory.");
            if (!File.Exists(full))
                throw new FileNotFoundException(
                    $"Node '{n.Id}' references prompt '{n.PromptRef}', which does not exist.", full);

            map[rel] = full;
        }
        return map;
    }

    /// <summary>
    /// sha256 over one path-tagged digest per file, sorted before combining: the same bytes under a
    /// different path change the hash, the order nodes happen to reference prompts in does not.
    /// </summary>
    private static string ComputeSha(IReadOnlyDictionary<string, byte[]> taggedContent)
    {
        var digests = new List<string>();
        foreach (var (tag, bytes) in taggedContent)
            digests.Add(FileDigest(tag, bytes));

        digests.Sort(StringComparer.Ordinal);
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", digests))));
    }

    /// <summary>
    /// M7b: resolve every node carrying an <c>agent_ref</c> against the agent registry and merge the
    /// named agent's prompt, tools, model tier and output schema onto the node — so the executors, which
    /// read those node fields, run a referenced agent unchanged. The resolved agent and its prompt are
    /// folded into <paramref name="content"/> so the run's sha pins the exact agent version, not just the
    /// workflow. The team is the one the workflow itself belongs to (a <c>teams/&lt;team&gt;/</c> workflow
    /// uses that team's agents; a default/flat workflow uses the org-default agents), so resolution is
    /// deterministic on resume from the stored workflow name alone — no separate team field to persist.
    /// Fail-closed: an unresolved agent_ref, or a workflow that references agents with no registry
    /// configured, throws rather than running an unpinned or wrong agent.
    /// </summary>
    private void ResolveAgentRefs(WorkflowDefinition wf, string? team, IDictionary<string, byte[]> content)
    {
        var agentNodes = wf.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.AgentRef)).ToList();
        if (agentNodes.Count == 0) return;
        if (agentsDir is null)
            throw new InvalidOperationException(
                "Workflow references named agents but no agent registry directory is configured.");

        var catalog = new AgentCatalog(agentsDir);
        var loader = new AgentLoader(agentsDir, promptsDir);

        foreach (var node in agentNodes)
        {
            string resolved;
            try { resolved = catalog.ResolveName(node.AgentRef!, team); }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Node '{node.Id}' references agent '{node.AgentRef}', which does not resolve.", ex);
            }

            var agent = loader.LoadWithDigests(resolved, out var digests);
            node.PromptRef = agent.PromptRef;
            node.Tools.Clear();
            node.Tools.AddRange(agent.Tools);
            node.ModelTier = agent.ModelTier;
            node.OutputSchema = agent.OutputSchema;   // agent_ref fully owns the node's agent config

            foreach (var (tag, bytes) in digests) content[tag] = bytes;
        }
    }

    /// <summary>The team a workflow belongs to, parsed from a <c>teams/&lt;team&gt;/…</c> loader name;
    /// null for a default/flat workflow. Agents resolve within this team's namespace.</summary>
    private static string? TeamOf(string workflowName)
    {
        var parts = workflowName.Replace('\\', '/').Split('/');
        return parts.Length >= 3 && parts[0] == "teams" ? parts[1] : null;
    }

    private static string FileDigest(string tag, byte[] content)
    {
        var tagBytes = Encoding.UTF8.GetBytes(tag + "\0");
        var buffer = new byte[tagBytes.Length + content.Length];
        tagBytes.CopyTo(buffer, 0);
        content.CopyTo(buffer, tagBytes.Length);
        return Hex(SHA256.HashData(buffer));
    }

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
