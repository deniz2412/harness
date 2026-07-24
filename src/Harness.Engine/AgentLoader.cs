using System.Security.Cryptography;
using System.Text;
using Harness.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Engine;

/// <summary>
/// Loads an agent definition (M7b — the named agent registry) and stamps it with a deterministic
/// content hash covering the YAML and the prompt it references — the same content-hash discipline as
/// <see cref="WorkflowLoader"/>. That hash is what lets a workflow which references the agent fold the
/// agent's exact identity into the run's pin (see <see cref="LoadWithDigests"/>).
/// Content, not git: agents and prompts are mounted read-only into the container from separate
/// directories and git is not available inside it.
/// Fail-closed (invariant 2): an agent with a missing/escaping prompt, a bad model tier, or no name
/// fails to load rather than failing mid-run.
/// </summary>
public sealed class AgentLoader(string agentsDir, string promptsDir)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>The model tiers an agent may name — the gateway model groups, nothing else.</summary>
    private static readonly string[] Tiers = ["cheap", "strong"];

    /// <summary>
    /// Resolves <c>agentsDir/name.yaml</c>, deserializes, validates, stamps <see cref="AgentDefinition.Sha"/>,
    /// and returns it. Throws <see cref="FileNotFoundException"/> for an unknown agent or missing prompt,
    /// and <see cref="InvalidOperationException"/> for a structurally invalid one.
    /// </summary>
    public AgentDefinition Load(string name) => LoadWithDigests(name, out _);

    /// <summary>
    /// Same as <see cref="Load"/>, but also yields the tag→content map that went into the agent's Sha:
    /// <c>agent:&lt;name&gt;.yaml</c> → the agent YAML bytes, and <c>prompt:&lt;rel&gt;</c> → the referenced
    /// prompt bytes. This is the seam for Workstream B: after resolving a node's <c>agent_ref</c>, the
    /// workflow loader folds these tagged entries into its own <c>ComputeSha</c> so the run pins the
    /// exact agent version, not just the workflow. Tags are already path-tagged and collision-safe;
    /// the caller only needs to hash each (tag, bytes) pair the same way and add them to its digest set.
    /// </summary>
    public AgentDefinition LoadWithDigests(string name, out IReadOnlyDictionary<string, byte[]> taggedContent)
    {
        var fileName = $"{name}.yaml";
        var path = Path.Combine(agentsDir, fileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"Unknown agent '{name}'", path);

        var agent = Yaml.Deserialize<AgentDefinition>(File.ReadAllText(path))
                    ?? throw new InvalidOperationException($"Agent '{name}' is empty.");
        Validate(agent);

        var promptRel = agent.PromptRef.Trim().Replace('\\', '/');
        var promptFull = ResolvePrompt(agent, promptRel);

        // Sorted, path-tagged: the same map (and therefore the same Sha) regardless of insertion order.
        var content = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"agent:{fileName.Replace('\\', '/')}"] = File.ReadAllBytes(path),
            [$"prompt:{promptRel}"] = File.ReadAllBytes(promptFull),
        };

        agent.Sha = ComputeSha(content);
        taggedContent = content;
        return agent;
    }

    private static void Validate(AgentDefinition agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Name))
            throw new InvalidOperationException("Agent requires a name.");
        if (string.IsNullOrWhiteSpace(agent.PromptRef))
            throw new InvalidOperationException($"Agent '{agent.Name}' requires prompt_ref.");
        // A typo here would otherwise silently pick a tier; the tier names a gateway model group.
        if (!Tiers.Contains(agent.ModelTier, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' has model_tier '{agent.ModelTier}'; expected 'cheap' or 'strong'.");
    }

    /// <summary>
    /// Resolves the agent's prompt to its file, failing the load if it is missing or escapes the
    /// prompts directory — the same traversal guard as <see cref="WorkflowLoader"/>'s prompt check.
    /// </summary>
    private string ResolvePrompt(AgentDefinition agent, string promptRel)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(promptsDir));
        var full = Path.GetFullPath(Path.Combine(root, promptRel));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' prompt_ref '{agent.PromptRef}' resolves outside the prompts directory.");
        if (!File.Exists(full))
            throw new FileNotFoundException(
                $"Agent '{agent.Name}' references prompt '{agent.PromptRef}', which does not exist.", full);

        return full;
    }

    /// <summary>
    /// sha256 over one path-tagged digest per file, sorted before combining — byte-for-byte the same
    /// scheme as <see cref="WorkflowLoader"/>: the same bytes under a different path (or tag) change
    /// the hash, the order they were added does not.
    /// </summary>
    private static string ComputeSha(IReadOnlyDictionary<string, byte[]> taggedContent)
    {
        var digests = new List<string>();
        foreach (var (tag, content) in taggedContent)
            digests.Add(FileDigest(tag, content));

        digests.Sort(StringComparer.Ordinal);
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", digests))));
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
