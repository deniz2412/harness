using System.Text.RegularExpressions;

namespace Harness.Policy;

/// <summary>Minimal secret detection for M0. M1 replaces/augments with a gitleaks-style ruleset.</summary>
public static partial class SecretScanner
{
    [GeneratedRegex(@"(sk-ant-[A-Za-z0-9\-_]{20,}|ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,}|AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----)")]
    private static partial Regex SecretPattern();

    public static bool ContainsSecret(string text, out string match)
    {
        var m = SecretPattern().Match(text);
        match = m.Success ? m.Value[..Math.Min(12, m.Value.Length)] + "…" : "";
        return m.Success;
    }
}
