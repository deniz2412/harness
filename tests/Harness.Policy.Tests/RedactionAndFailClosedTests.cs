using Xunit;

namespace Harness.Policy.Tests;

/// <summary>
/// Two properties that matter more than detection itself: a block must not leak what it blocked,
/// and a scanner that cannot do its job must deny rather than pass.
/// </summary>
public class RedactionAndFailClosedTests
{
    private static readonly PolicyPipeline Pipeline = new();

    // ---- redaction leaks nothing ----

    [Fact]
    public void Redact_masks_every_match_and_leaks_no_prefix()
    {
        var text =
            $"log line one\ntoken={SyntheticSecrets.GitHubClassicToken}\n" +
            $"anthropic={SyntheticSecrets.AnthropicKey}\ntrailing text";

        var redacted = Pipeline.Redact(text);

        Assert.DoesNotContain(SyntheticSecrets.GitHubClassicToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecrets.AnthropicKey, redacted, StringComparison.Ordinal);
        // not even a prefix: 12 characters of an API key is a real leak, which is what M0 did.
        Assert.DoesNotContain(SyntheticSecrets.GitHubClassicToken[..12], redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecrets.AnthropicKey[..12], redacted, StringComparison.Ordinal);

        Assert.Contains("[REDACTED:github-token:", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:anthropic-api-key:", redacted, StringComparison.Ordinal);
        // surrounding text survives — redaction is not a bucket of paint
        Assert.Contains("log line one", redacted, StringComparison.Ordinal);
        Assert.Contains("trailing text", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_masks_only_the_value_of_a_keyword_anchored_rule()
    {
        var redacted = Pipeline.Redact(SyntheticSecrets.HighEntropyApiKeyAssignment);

        Assert.DoesNotContain("Xu8kQ2vLp9WnZ4tR7yBc", redacted, StringComparison.Ordinal);
        Assert.Contains("api_key", redacted, StringComparison.Ordinal);   // the name is not the secret
    }

    [Fact]
    public void Redact_leaves_clean_text_untouched()
    {
        const string clean = "public decimal Total(int qty) => qty * 9.99m;";
        Assert.Equal(clean, Pipeline.Redact(clean));
    }

    [Fact]
    public void Violation_carries_the_rule_id_and_a_redacted_match_only()
    {
        var ex = Assert.Throws<PolicyViolationException>(
            () => Pipeline.ScanOutbound("pre-write", $"here it is: {SyntheticSecrets.AnthropicKey}"));

        Assert.Equal("pre-write", ex.Stage);
        Assert.Equal("anthropic-api-key", ex.RuleId);
        Assert.NotNull(ex.RedactedMatch);
        Assert.StartsWith("[REDACTED:anthropic-api-key:", ex.RedactedMatch, StringComparison.Ordinal);

        Assert.DoesNotContain(SyntheticSecrets.AnthropicKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecrets.AnthropicKey[..12], ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecrets.AnthropicKey[..12], ex.RedactedMatch!, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprints_correlate_occurrences_without_revealing_them()
    {
        var a = Redaction.Token("rule", "SYNTHETIC-VALUE-ONE");
        var b = Redaction.Token("rule", "SYNTHETIC-VALUE-ONE");
        var c = Redaction.Token("rule", "SYNTHETIC-VALUE-TWO");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.DoesNotContain("SYNTHETIC", a, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanOutbound_passes_clean_content()
    {
        Pipeline.ScanOutbound("pre-model", "Review this diff for tier-boundary errors.");
        Pipeline.ScanOutbound("pre-model", null);
    }

    // ---- fail-closed: unscannable is not clean ----

    private const string RedosRuleSet = """
        version: 1
        rules:
          - id: redos-bait
            description: Catastrophically backtracking rule
            regex: |-
              ^(a+)+$
        """;

    private static string RedosInput => new string('a', 60) + "!";

    [Fact]
    public void Regex_timeout_blocks_rather_than_reporting_clean()
    {
        var scanner = SecretScanner.FromYaml(RedosRuleSet, TimeSpan.FromMilliseconds(25));

        var ex = Assert.Throws<PolicyViolationException>(() => scanner.Scan(RedosInput, "pre-model"));

        Assert.Equal("pre-model", ex.Stage);
        Assert.Equal("redos-bait", ex.RuleId);
        Assert.Contains("unscannable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_withholds_content_it_could_not_scan()
    {
        var scanner = SecretScanner.FromYaml(RedosRuleSet, TimeSpan.FromMilliseconds(25));

        var redacted = scanner.Redact(RedosInput);

        Assert.Equal(Redaction.WithheldPlaceholder, redacted);
        Assert.DoesNotContain("aaaa", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_built_on_a_redos_ruleset_blocks_the_run()
    {
        var pipeline = new PolicyPipeline(
            SecretScanner.FromYaml(RedosRuleSet, TimeSpan.FromMilliseconds(25)),
            ToolCatalog.Default);

        Assert.Throws<PolicyViolationException>(() => pipeline.ScanOutbound("pre-model", RedosInput));
    }

    [Theory]
    [InlineData("")]                                    // empty file
    [InlineData("version: 1\nrules: []")]               // no rules
    [InlineData("version: 1\nrules:\n  - description: no id\n    regex: 'x'")]
    [InlineData("version: 1\nrules:\n  - id: no-regex\n    description: d")]
    [InlineData("version: 1\nrules:\n  - id: dup\n    description: d\n    regex: 'a'\n  - id: dup\n    description: d\n    regex: 'b'")]
    [InlineData("version: 1\nrules:\n  - id: bad-regex\n    description: d\n    regex: '[unclosed'")]
    [InlineData("version: 1\nrules:\n  - id: bad-group\n    description: d\n    regex: 'abc'\n    secret_group: 2")]
    [InlineData("version: 1\nrules:\n  - id: bad-entropy\n    description: d\n    regex: 'abc'\n    entropy: 99")]
    [InlineData(": : : not yaml : :")]
    public void Malformed_ruleset_fails_to_load_rather_than_loading_empty(string yaml)
    {
        var ex = Assert.Throws<PolicyViolationException>(() => SecretScanner.FromYaml(yaml));
        Assert.Equal("policy-ruleset", ex.Stage);
    }

    [Fact]
    public void Ruleset_with_a_non_positive_timeout_is_rejected()
    {
        Assert.Throws<PolicyViolationException>(
            () => SecretScanner.FromYaml(RedosRuleSet, TimeSpan.Zero));
    }
}
