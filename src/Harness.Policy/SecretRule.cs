using System.Text.RegularExpressions;

namespace Harness.Policy;

/// <summary>One compiled rule from <c>Rules/secret-rules.yaml</c>.</summary>
public sealed class SecretRule
{
    internal SecretRule(
        string id,
        string description,
        Regex pattern,
        int secretGroup,
        double? entropyThreshold,
        IReadOnlyList<Regex> allowlist)
    {
        Id = id;
        Description = description;
        Pattern = pattern;
        SecretGroup = secretGroup;
        EntropyThreshold = entropyThreshold;
        Allowlist = allowlist;
    }

    public string Id { get; }
    public string Description { get; }

    /// <summary>The rule's pattern source. Safe to log — it describes shapes, not values.</summary>
    public string RegexSource => Pattern.ToString();

    /// <summary>Capture group holding the secret itself; 0 means the whole match.</summary>
    public int SecretGroup { get; }

    /// <summary>Bits-per-character floor over the secret group, or null when the shape suffices.</summary>
    public double? EntropyThreshold { get; }

    internal Regex Pattern { get; }
    internal IReadOnlyList<Regex> Allowlist { get; }

    /// <summary>False-positive filter: placeholders, template expressions, doc examples.</summary>
    internal bool IsAllowlisted(string value)
    {
        foreach (var allow in Allowlist)
            if (allow.IsMatch(value))
                return true;
        return false;
    }
}

/// <summary>A single detection. Carries no plaintext — <see cref="Redacted"/> is a redaction token.</summary>
public sealed record SecretFinding(
    string RuleId,
    string Description,
    int Index,
    int Length,
    string Redacted)
{
    public override string ToString() => $"{RuleId} at offset {Index} {Redacted}";
}
