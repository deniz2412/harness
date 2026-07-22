using Xunit;

namespace Harness.Policy.Tests;

/// <summary>The default ruleset: every rule fires on its shape and stays quiet on near-misses.</summary>
public class SecretScannerTests
{
    private static readonly SecretScanner Scanner = SecretScanner.Default;

    [Fact]
    public void Default_ruleset_loads_with_unique_rule_ids()
    {
        Assert.NotEmpty(Scanner.Rules);
        Assert.All(Scanner.Rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
        });
        Assert.Equal(
            Scanner.Rules.Count,
            Scanner.Rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Ruleset_covers_the_families_M1_requires()
    {
        string[] required =
        [
            "anthropic-api-key", "github-token", "github-pat-fine-grained", "aws-access-key-id",
            "aws-secret-access-key", "private-key", "slack-token", "google-api-key",
            "azure-storage-connection-string", "azure-ad-client-secret",
            "generic-high-entropy-assignment"
        ];
        var ids = Scanner.Rules.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(required, id => Assert.Contains(id, ids));
    }

    // ---- positives: rule id is asserted, so a match by the wrong rule is a failure ----

    [Theory]
    [InlineData(SyntheticSecrets.AnthropicKey, "anthropic-api-key")]
    [InlineData(SyntheticSecrets.GitHubClassicToken, "github-token")]
    [InlineData(SyntheticSecrets.GitHubOAuthToken, "github-token")]
    [InlineData(SyntheticSecrets.GitHubFineGrainedPat, "github-pat-fine-grained")]
    [InlineData(SyntheticSecrets.GitHubAppInstallationToken, "github-app-installation-token")]
    [InlineData(SyntheticSecrets.AwsAccessKeyId, "aws-access-key-id")]
    [InlineData(SyntheticSecrets.AwsSecretAssignment, "aws-secret-access-key")]
    [InlineData(SyntheticSecrets.RsaPrivateKeyHeader, "private-key")]
    [InlineData(SyntheticSecrets.EcPrivateKeyHeader, "private-key")]
    [InlineData(SyntheticSecrets.OpenSshPrivateKeyHeader, "private-key")]
    [InlineData(SyntheticSecrets.PgpPrivateKeyHeader, "private-key")]
    [InlineData(SyntheticSecrets.SlackBotToken, "slack-token")]
    [InlineData(SyntheticSecrets.SlackWebhookUrl, "slack-webhook-url")]
    [InlineData(SyntheticSecrets.GoogleApiKey, "google-api-key")]
    [InlineData(SyntheticSecrets.AzureStorageConnectionString, "azure-storage-connection-string")]
    [InlineData(SyntheticSecrets.AzureAdClientSecret, "azure-ad-client-secret")]
    [InlineData(SyntheticSecrets.JsonWebToken, "json-web-token")]
    [InlineData(SyntheticSecrets.HighEntropyApiKeyAssignment, "generic-high-entropy-assignment")]
    [InlineData(SyntheticSecrets.HighEntropyPasswordAssignment, "generic-high-entropy-assignment")]
    [InlineData(SyntheticSecrets.HighEntropyClientSecretAssignment, "generic-high-entropy-assignment")]
    public void Detects_synthetic_secret(string sample, string expectedRuleId)
    {
        var findings = Scanner.Scan($"config value: {sample}\nnext line");
        Assert.Contains(findings, f => f.RuleId == expectedRuleId);
    }

    // ---- negatives: near-misses of each shape, and ordinary code ----

    [Theory]
    // truncated / wrong-length variants of each family
    [InlineData("sk-ant-api03-short")]
    [InlineData("ghp_tooShortToBeAToken")]
    [InlineData("ghx_SYNTHETICSYNTHETICSYNTHETICSYNTHETIC")]   // not a GitHub token prefix
    [InlineData("github_pat_1234")]
    [InlineData("AKIAIOSFODNN7EXAMPL")]                        // 19 chars, one short
    [InlineData("akiaiosfodnn7example")]                        // lowercase
    [InlineData("-----BEGIN CERTIFICATE-----")]
    [InlineData("-----BEGIN PUBLIC KEY-----")]
    [InlineData("xoxb-short")]
    [InlineData("https://hooks.slack.com/services/")]
    [InlineData("AIzaSyShort")]
    [InlineData("abc8Q~short")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9")]                        // header only, no JWT dots
    [InlineData("DefaultEndpointsProtocol=https;AccountName=synthacct;EndpointSuffix=core.windows.net")]
    // ordinary source and prose
    [InlineData("public decimal ApplyDiscount(int quantity, decimal price)\n{\n    if (quantity > 100) price *= 0.85m;\n    return price;\n}")]
    [InlineData("The API key rotation policy is documented in the runbook.")]
    [InlineData("")]
    public void Ignores_near_miss(string sample)
    {
        Assert.Empty(Scanner.Scan(sample));
    }

    // ---- the entropy gate on the generic rule ----

    [Theory]
    [InlineData("api_key = \"aaaaaaaaaaaaaaaaaaaa\"")]          // zero entropy
    [InlineData("password = \"password_password\"")]            // repetitive, below the floor
    [InlineData("api_key = \"short\"")]                          // under the length floor
    [InlineData("api_key = \"${MY_SERVICE_TOKEN}\"")]            // template expression
    [InlineData("api_key = \"your_api_key_here_xx\"")]           // documentation placeholder
    [InlineData("client_secret = \"<client-secret-value>\"")]    // angle-bracket placeholder
    [InlineData("password = \"process.env.DB_PASSWORD\"")]       // an indirection, not a value
    public void Generic_rule_does_not_fire_on_low_entropy_or_placeholders(string sample)
    {
        Assert.DoesNotContain(
            Scanner.Scan(sample),
            f => f.RuleId == "generic-high-entropy-assignment");
    }

    [Fact]
    public void Generic_rule_fires_once_entropy_clears_the_floor()
    {
        var low = Scanner.Scan("api_key = \"abababababababababab\"");
        var high = Scanner.Scan(SyntheticSecrets.HighEntropyApiKeyAssignment);

        Assert.DoesNotContain(low, f => f.RuleId == "generic-high-entropy-assignment");
        Assert.Contains(high, f => f.RuleId == "generic-high-entropy-assignment");
    }

    [Fact]
    public void Aws_rule_filters_the_value_aws_itself_publishes_as_an_example()
    {
        Assert.DoesNotContain(
            Scanner.Scan(SyntheticSecrets.AwsDocumentedExampleAssignment),
            f => f.RuleId == "aws-secret-access-key");
    }

    [Fact]
    public void Shannon_entropy_orders_values_as_expected()
    {
        Assert.Equal(0d, Entropy.Shannon("aaaaaaaaaaaa"), 6);
        Assert.Equal(0d, Entropy.Shannon(""), 6);
        Assert.True(Entropy.Shannon("Xu8kQ2vLp9WnZ4tR7yBc") > 4.0);
        Assert.True(Entropy.Shannon("passwordpassword") < 3.5);
    }

    [Fact]
    public void A_realistic_untrusted_diff_scans_clean()
    {
        // Fail-closed only helps if it fires on secrets and not on the review workload itself:
        // this is the shape of what github.pr_diff hands the gather node on the test repo.
        const string diff = """
            diff --git a/src/Pricing/DiscountCalculator.cs b/src/Pricing/DiscountCalculator.cs
            --- a/src/Pricing/DiscountCalculator.cs
            +++ b/src/Pricing/DiscountCalculator.cs
            @@ -12,6 +12,14 @@ public sealed class DiscountCalculator
            +    public decimal Apply(int quantity, decimal unitPrice)
            +    {
            +        var total = quantity * unitPrice;
            +        if (quantity > 10)  total *= 0.95m;
            +        if (quantity > 50)  total *= 0.90m;
            +        if (quantity > 100) total *= 0.85m;
            +        return Math.Round(total, 2);
            +    }
            +
            +    // TODO: coupon codes are parsed with try/catch and no validation - see issue #7
            diff --git a/appsettings.Development.json b/appsettings.Development.json
            +  "ConnectionStrings": {
            +    "Default": "Host=localhost;Database=pricing;Username=app;Password=${DB_PASSWORD}"
            +  },
            +  "Logging": { "LogLevel": { "Default": "Information" } }
            """;

        Assert.Empty(Scanner.Scan(diff));
    }

    [Fact]
    public void Findings_are_ordered_by_position_and_all_secrets_are_found()
    {
        var text = $"first {SyntheticSecrets.GitHubClassicToken} then {SyntheticSecrets.AnthropicKey} end";
        var findings = Scanner.Scan(text);

        Assert.Equal(2, findings.Count);
        Assert.Equal("github-token", findings[0].RuleId);
        Assert.Equal("anthropic-api-key", findings[1].RuleId);
        Assert.True(findings[0].Index < findings[1].Index);
    }
}
