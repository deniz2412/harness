using Harness.Runner;
using Xunit;

namespace Harness.Runner.Tests;

/// <summary>The no-shell parser the write-tools workstream relies on: quoting groups, metacharacters
/// stay literal, malformed input is refused.</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void Splits_on_whitespace()
    {
        Assert.Equal(new[] { "git", "status", "--porcelain" }, CommandLine.Split("git status --porcelain"));
    }

    [Fact]
    public void Double_quotes_group_and_are_removed()
    {
        Assert.Equal(
            new[] { "git", "commit", "-m", "a message with spaces" },
            CommandLine.Split("git commit -m \"a message with spaces\""));
    }

    [Fact]
    public void Collapses_runs_of_whitespace_and_trims()
    {
        Assert.Equal(new[] { "git", "log" }, CommandLine.Split("   git    log   "));
    }

    [Fact]
    public void Shell_metacharacters_stay_literal_tokens()
    {
        // No shell: '&&', '|', ';', '$(...)' are ordinary characters, not operators. The program token
        // is still just the first word (which the allowlist then judges).
        var tokens = CommandLine.Split("git status && rm -rf /");
        Assert.Equal("git", tokens[0]);
        Assert.Contains("&&", tokens);
        Assert.Contains("rm", tokens);
    }

    [Fact]
    public void Unbalanced_quote_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Split("git commit -m \"unterminated"));
    }

    [Fact]
    public void Empty_string_yields_no_tokens()
    {
        Assert.Empty(CommandLine.Split("   "));
    }
}
