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
public sealed class WorkflowLoader(string workflowsDir, string promptsDir)
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

        var prompts = ResolvePrompts(wf);
        wf.Sha = ComputeSha(fileName, File.ReadAllBytes(path), prompts);
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
            if (n.Kind is "agent" or "agent-loop" && n.PromptRef is null)
                throw new InvalidOperationException($"Agent node '{n.Id}' requires prompt_ref.");
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
    private static string ComputeSha(string workflowFile, byte[] workflowBytes,
        IReadOnlyDictionary<string, string> prompts)
    {
        var digests = new List<string> { FileDigest($"workflow:{workflowFile.Replace('\\', '/')}", workflowBytes) };
        foreach (var (rel, full) in prompts)
            digests.Add(FileDigest($"prompt:{rel}", File.ReadAllBytes(full)));

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
