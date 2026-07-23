namespace Harness.Runner;

/// <summary>
/// Splits a command string into a program and its arguments <b>without a shell</b>. This is both an
/// injection defense (invariant 4: untrusted worktree content must never reach <c>/bin/sh -c</c> or
/// <c>cmd /c</c>) and the cross-platform contract the write-tools workstream relies on.
///
/// Grammar, deliberately minimal:
/// <list type="bullet">
///   <item>tokens are separated by ASCII whitespace;</item>
///   <item>a double quote <c>"</c> toggles "inside quotes" — whitespace inside a quoted span is part
///         of the token and the quote characters themselves are removed;</item>
///   <item>there is <b>no</b> escaping, no single-quote handling, and no interpretation of
///         <c>&amp;&amp;</c>, <c>|</c>, <c>;</c>, <c>$</c>, redirection, globbing, or any other shell
///         metacharacter — they are ordinary literal characters within a token.</item>
/// </list>
/// So <c>git commit -m "a message with spaces"</c> yields four tokens; <c>rm -rf / &amp;&amp; echo x</c>
/// is parsed as the single program <c>rm</c> with literal arguments (and then refused, because <c>rm</c>
/// is not on the allowlist). The tokens become <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
/// entries, so each is passed to the child verbatim with no re-quoting.
/// </summary>
public static class CommandLine
{
    /// <summary>Tokenizes <paramref name="command"/>. Throws <see cref="ArgumentException"/> on an
    /// unbalanced quote (a malformed command is refused, not guessed at).</summary>
    public static IReadOnlyList<string> Split(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in command)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true; // an empty "" is still a (empty) token
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (inQuotes)
            throw new ArgumentException("Unbalanced double quote in command.", nameof(command));

        if (hasToken)
            tokens.Add(current.ToString());

        return tokens;
    }
}
