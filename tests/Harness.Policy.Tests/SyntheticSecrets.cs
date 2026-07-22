namespace Harness.Policy.Tests;

/// <summary>
/// Shape-correct, value-fake credentials for the scanner tests.
/// </summary>
/// <remarks>
/// Every value here is invented: the alphabets and lengths match the real formats — that is the
/// whole point of a shape-matching scanner — but none of them is, or ever was, a live credential.
/// The two AWS strings are the placeholders AWS itself publishes in its documentation.
/// Nothing in this repository may ever hold a real secret, test fixtures least of all.
/// </remarks>
internal static class SyntheticSecrets
{
    private const string S9 = "SYNTHETIC";                       // 9 chars, used to hit exact lengths

    public const string AnthropicKey = "sk-ant-api03-" + S9 + S9 + S9 + S9 + "0000";
    public const string GitHubClassicToken = "ghp_" + S9 + S9 + S9 + S9;                 // 36
    public const string GitHubOAuthToken = "gho_" + S9 + S9 + S9 + S9;                   // 36
    public const string GitHubFineGrainedPat = "github_pat_" + S9 + S9 + "0000" + "_" + S9 + S9 + S9 + S9 + S9 + S9 + "00000";
    public const string GitHubAppInstallationToken = "v1.abcdef0123456789abcdef0123456789abcdef01";

    public const string AwsAccessKeyId = "AKIAIOSFODNN7EXAMPLE";  // AWS documentation placeholder
    public const string AwsSecretAssignment =
        "aws_secret_access_key = \"Kq7m2Zv9Lp4Xn8Tb1Rc6Ws3Yd5Gf0Hj7Kl2Mn9Pq\"";
    /// <summary>AWS's own documented example value — must be filtered as a false positive.</summary>
    public const string AwsDocumentedExampleAssignment =
        "aws_secret_access_key = \"wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY\"";

    public const string RsaPrivateKeyHeader = "-----BEGIN RSA PRIVATE KEY-----";
    public const string EcPrivateKeyHeader = "-----BEGIN EC PRIVATE KEY-----";
    public const string OpenSshPrivateKeyHeader = "-----BEGIN OPENSSH PRIVATE KEY-----";
    public const string PgpPrivateKeyHeader = "-----BEGIN PGP PRIVATE KEY BLOCK-----";

    public const string SlackBotToken = "xoxb-0000000000-0000000000000-" + S9 + S9 + S9;
    public const string SlackWebhookUrl =
        "https://hooks.slack.com/services/T00000000/B00000000/" + S9 + S9 + "SYN";

    public const string GoogleApiKey = "AIza" + S9 + S9 + S9 + "00000000";               // AIza + 35

    public const string AzureStorageConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=synthacct;AccountKey=" +
        "U1lOVEhFVElDU1lOVEhFVElDU1lOVEhFVElDU1lOVEhFVElDU1lOVEhFVElDU1lOVEhFVElD" +
        ";EndpointSuffix=core.windows.net";
    public const string AzureAdClientSecret = "abc8Q~" + S9 + S9 + S9 + "1234567";       // 3+1+Q~+34

    public const string JsonWebToken =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.c3ludGhldGljLXNpZ25hdHVyZQ";

    /// <summary>20 distinct characters ⇒ 4.32 bits/char, comfortably over the generic gate.</summary>
    public const string HighEntropyApiKeyAssignment = "api_key = \"Xu8kQ2vLp9WnZ4tR7yBc\"";
    public const string HighEntropyPasswordAssignment = "password: \"9fK3xQ7wL2mZ8pR4tV6y\"";
    public const string HighEntropyClientSecretAssignment = "client_secret=\"Vb4nD7sJ1qW9zX3cM6kT\"";
}
