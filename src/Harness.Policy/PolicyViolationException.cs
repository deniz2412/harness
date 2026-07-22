namespace Harness.Policy;

/// <summary>
/// A fail-closed block. Thrown for a detected secret, a denied tool, and equally for the scanner
/// or the catalog being unable to do its job — an unusable ruleset is a block, never a pass.
/// </summary>
/// <remarks>
/// The message is safe to log: <see cref="RedactedMatch"/> is a <see cref="Redaction"/> token, and
/// no other member carries content derived from the offending value.
/// </remarks>
public sealed class PolicyViolationException : Exception
{
    public PolicyViolationException(
        string stage, string reason, string? ruleId = null, string? redactedMatch = null)
        : base(Format(stage, reason, redactedMatch))
    {
        Stage = stage;
        Reason = reason;
        RuleId = ruleId;
        RedactedMatch = redactedMatch;
    }

    /// <summary>pre-model | pre-tool | pre-write | policy-ruleset | tool-catalog | ...</summary>
    public string Stage { get; }

    /// <summary>Why the block fired. Never contains secret material.</summary>
    public string Reason { get; }

    /// <summary>Secret rule id, or the denied tool's required permission, when applicable.</summary>
    public string? RuleId { get; }

    /// <summary>A <see cref="Redaction.Token"/>, never the matched text.</summary>
    public string? RedactedMatch { get; }

    private static string Format(string stage, string reason, string? redacted) =>
        redacted is null
            ? $"Policy block at {stage}: {reason}"
            : $"Policy block at {stage}: {reason} {redacted}";
}
