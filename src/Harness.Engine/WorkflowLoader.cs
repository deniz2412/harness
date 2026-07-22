using Harness.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Engine;

public sealed class WorkflowLoader(string workflowsDir)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public WorkflowDefinition Load(string name)
    {
        var path = Path.Combine(workflowsDir, $"{name}.yaml");
        if (!File.Exists(path)) throw new FileNotFoundException($"Unknown workflow '{name}'", path);
        var wf = Yaml.Deserialize<WorkflowDefinition>(File.ReadAllText(path));
        Validate(wf);
        return wf;
    }

    private static void Validate(WorkflowDefinition wf)
    {
        var ids = wf.Nodes.Select(n => n.Id).ToHashSet();
        foreach (var n in wf.Nodes)
        {
            foreach (var d in n.DependsOn)
                if (!ids.Contains(d)) throw new InvalidOperationException($"Node '{n.Id}' depends on unknown node '{d}'.");
            if (n.Kind is "agent" or "agent-loop" && n.PromptRef is null)
                throw new InvalidOperationException($"Agent node '{n.Id}' requires prompt_ref.");
        }
    }
}
