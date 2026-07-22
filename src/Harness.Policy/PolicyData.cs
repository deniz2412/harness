using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Policy;

/// <summary>Loads the embedded policy data files. Any failure is a block, never an empty ruleset.</summary>
internal static class PolicyData
{
    internal const string SecretRulesResource = "Harness.Policy.Rules.secret-rules.yaml";
    internal const string ToolCatalogResource = "Harness.Policy.Rules.tool-catalog.yaml";

    internal static IDeserializer Deserializer { get; } = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    internal static string ReadResource(string name, string stage)
    {
        var assembly = typeof(PolicyData).Assembly;
        var stream = assembly.GetManifestResourceStream(name)
                     ?? ResolveBySuffix(assembly, name);
        if (stream is null)
            throw new PolicyViolationException(
                stage, $"embedded policy data '{name}' is missing from the assembly");

        using (stream)
        {
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text))
                throw new PolicyViolationException(stage, $"embedded policy data '{name}' is empty");
            return text;
        }
    }

    /// <summary>Tolerates a different RootNamespace/logical-name mapping without going quiet.</summary>
    private static Stream? ResolveBySuffix(Assembly assembly, string name)
    {
        var leaf = name[(name.LastIndexOf('.', name.LastIndexOf('.') - 1) + 1)..];
        var match = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(leaf, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : assembly.GetManifestResourceStream(match);
    }
}
