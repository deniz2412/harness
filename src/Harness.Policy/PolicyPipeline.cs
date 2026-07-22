namespace Harness.Policy;

public sealed class PolicyViolationException(string stage, string reason)
    : Exception($"Policy block at {stage}: {reason}");

/// <summary>Fail-closed policy checks. Called pre-model, pre-tool and pre-write.</summary>
public sealed class PolicyPipeline
{
    /// <summary>Everything bound for the model or an external write passes through here.</summary>
    public void ScanOutbound(string stage, string content)
    {
        if (SecretScanner.ContainsSecret(content, out var match))
            throw new PolicyViolationException(stage, $"secret detected ({match})");
    }

    /// <summary>Tool must be listed on the node AND allowed by the workflow permission ceiling.</summary>
    public void AssertToolAllowed(string tool, IReadOnlyCollection<string> nodeTools)
    {
        if (!nodeTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            throw new PolicyViolationException("pre-tool", $"tool '{tool}' not allowed on this node");
    }
}
