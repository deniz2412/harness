using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Policy;

/// <summary>
/// Gitleaks-style secret detection over a ruleset expressed as data
/// (<c>Rules/secret-rules.yaml</c>, embedded).
/// </summary>
/// <remarks>
/// Fail-closed in three places, because all three are reachable from untrusted repo content:
/// a ruleset that will not load throws on every use (there is no degraded mode);
/// a rule whose regex exceeds <see cref="MatchTimeout"/> throws rather than reporting the text
/// clean — catastrophic backtracking is a denial of service *and* a scanner bypass;
/// and <see cref="Redact"/> withholds the whole string if it cannot redact it.
/// </remarks>
public sealed class SecretScanner
{
    /// <summary>Per-rule regex budget. Untrusted input, so this is a ceiling, not a hint.</summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounds work on adversarial input; the first finding already blocks the run.</summary>
    private const int MaxMatchesPerRule = 64;

    private static readonly Lazy<SecretScanner> LazyDefault =
        new(() => FromYaml(PolicyData.ReadResource(PolicyData.SecretRulesResource, "policy-ruleset")),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private SecretScanner(IReadOnlyList<SecretRule> rules, TimeSpan matchTimeout)
    {
        Rules = rules;
        MatchTimeout = matchTimeout;
    }

    /// <summary>
    /// The embedded ruleset. Throws <see cref="PolicyViolationException"/> — permanently, the
    /// failure is cached — if the ruleset is missing, empty or malformed.
    /// </summary>
    public static SecretScanner Default => LazyDefault.Value;

    public IReadOnlyList<SecretRule> Rules { get; }

    public TimeSpan MatchTimeout { get; }

    /// <summary>Builds a scanner from ruleset YAML. Any defect in the file is a block.</summary>
    public static SecretScanner FromYaml(string yaml, TimeSpan? matchTimeout = null)
    {
        const string stage = "policy-ruleset";
        var timeout = matchTimeout ?? DefaultMatchTimeout;
        if (timeout <= TimeSpan.Zero)
            throw new PolicyViolationException(stage, "match timeout must be positive");

        RuleSetFile? file;
        try
        {
            file = PolicyData.Deserializer.Deserialize<RuleSetFile>(yaml);
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(stage, $"secret ruleset is not valid YAML: {ex.Message}");
        }

        if (file?.Rules is not { Count: > 0 })
            throw new PolicyViolationException(stage, "secret ruleset contains no rules");

        var rules = new List<SecretRule>(file.Rules.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in file.Rules)
        {
            var id = raw.Id?.Trim();
            if (string.IsNullOrEmpty(id))
                throw new PolicyViolationException(stage, "secret ruleset contains a rule without an id");
            if (!seen.Add(id))
                throw new PolicyViolationException(stage, $"secret ruleset contains duplicate rule id '{id}'");
            if (string.IsNullOrWhiteSpace(raw.Description))
                throw new PolicyViolationException(stage, $"rule '{id}' has no description");
            if (string.IsNullOrWhiteSpace(raw.Regex))
                throw new PolicyViolationException(stage, $"rule '{id}' has no regex");
            if (raw.SecretGroup < 0)
                throw new PolicyViolationException(stage, $"rule '{id}' has a negative secret_group");
            if (raw.Entropy is < 0 or > 8)
                throw new PolicyViolationException(stage, $"rule '{id}' has an out-of-range entropy threshold");

            var pattern = Compile(raw.Regex!, id, stage, timeout);
            if (!pattern.GetGroupNumbers().Contains(raw.SecretGroup))
                throw new PolicyViolationException(
                    stage,
                    $"rule '{id}' declares secret_group {raw.SecretGroup} but its regex has no such group");

            var allowlist = (raw.Allowlist ?? [])
                .Select(a => Compile(a, id, stage, timeout))
                .ToArray();

            rules.Add(new SecretRule(id, raw.Description!.Trim(), pattern, raw.SecretGroup, raw.Entropy, allowlist));
        }

        return new SecretScanner(rules, timeout);
    }

    /// <summary>All findings in <paramref name="text"/>, ordered by position.</summary>
    /// <exception cref="PolicyViolationException">The scan could not be completed. Never "clean".</exception>
    public IReadOnlyList<SecretFinding> Scan(string? text, string stage = "secret-scan")
    {
        if (string.IsNullOrEmpty(text)) return [];

        var findings = new List<SecretFinding>();
        foreach (var rule in Rules)
        {
            try
            {
                var count = 0;
                for (var m = rule.Pattern.Match(text); m.Success; m = m.NextMatch())
                {
                    if (++count > MaxMatchesPerRule) break;

                    var capture = rule.SecretGroup > 0 && m.Groups[rule.SecretGroup].Success
                        ? m.Groups[rule.SecretGroup]
                        : (Capture)m;
                    var value = capture.Value;
                    if (value.Length == 0) continue;
                    if (rule.IsAllowlisted(value)) continue;
                    if (rule.EntropyThreshold is { } floor && Entropy.Shannon(value) < floor) continue;

                    findings.Add(new SecretFinding(
                        rule.Id, rule.Description, capture.Index, value.Length,
                        Redaction.Token(rule.Id, value)));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // ReDoS on untrusted content, or a rule that is simply too expensive. Either way
                // the content is unscannable, and unscannable is not clean.
                throw new PolicyViolationException(
                    stage,
                    $"secret scan exceeded its {MatchTimeout.TotalMilliseconds:0}ms budget on rule " +
                    $"'{rule.Id}'; content treated as unscannable",
                    rule.Id);
            }
            catch (Exception ex) when (ex is not PolicyViolationException)
            {
                throw new PolicyViolationException(
                    stage, $"secret scan failed on rule '{rule.Id}': {ex.GetType().Name}", rule.Id);
            }
        }

        findings.Sort((a, b) => a.Index.CompareTo(b.Index));
        return findings;
    }

    /// <summary>First finding, if any. Throws rather than reporting clean when the scan fails.</summary>
    public bool ContainsSecret(string? text, out SecretFinding? finding, string stage = "secret-scan")
    {
        var findings = Scan(text, stage);
        finding = findings.Count > 0 ? findings[0] : null;
        return finding is not null;
    }

    /// <summary>
    /// Masks every rule match in arbitrary text so callers can log, audit or echo it. Returns
    /// <see cref="Redaction.WithheldPlaceholder"/> if the scan itself failed — the one thing it
    /// must never do is hand back text it could not check.
    /// </summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        IReadOnlyList<SecretFinding> findings;
        try
        {
            findings = Scan(text, "redact");
        }
        catch (Exception)
        {
            return Redaction.WithheldPlaceholder;
        }

        if (findings.Count == 0) return text;

        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var f in findings)
        {
            if (f.Index < cursor) continue;   // overlapping match; already inside a masked span
            sb.Append(text, cursor, f.Index - cursor);
            sb.Append(f.Redacted);
            cursor = f.Index + f.Length;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    private static Regex Compile(string pattern, string ruleId, string stage, TimeSpan timeout)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, timeout);
        }
        catch (ArgumentException ex)
        {
            throw new PolicyViolationException(stage, $"rule '{ruleId}' has an invalid regex: {ex.Message}");
        }
    }

    // ---- YAML shapes (deserialization only) ----

    private sealed class RuleSetFile
    {
        public int Version { get; set; }
        public List<RuleFile>? Rules { get; set; }
    }

    private sealed class RuleFile
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public string? Regex { get; set; }
        public int SecretGroup { get; set; }
        public double? Entropy { get; set; }
        public List<string>? Allowlist { get; set; }
    }
}
