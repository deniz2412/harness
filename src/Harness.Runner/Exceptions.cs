namespace Harness.Runner;

/// <summary>
/// Thrown by <see cref="SubprocessRunnerSession.RunAsync"/> when the requested program is not on the
/// runner's allowlist. This is the fail-closed refusal (invariant 2): it is deliberately an
/// exception, not a <see cref="Harness.Contracts.CommandResult"/> with a non-zero exit, so a refusal
/// can never be mistaken for an ordinary command that happened to fail — the caller (bash node /
/// agent-loop validation) fails the run rather than interpreting an exit code.
/// </summary>
public sealed class CommandNotAllowedException(string program, IEnumerable<string> allowed)
    : InvalidOperationException(
        $"Command '{program}' is not on the runner allowlist. Allowed programs: {string.Join(", ", allowed)}.")
{
    /// <summary>The program that was refused (bare token, never the full command with its arguments).</summary>
    public string Program { get; } = program;
}

/// <summary>
/// Thrown by <see cref="SubprocessRunnerFactory.CreateAsync"/> when the isolated worktree cannot be
/// prepared (clone failed, timed out, or git could not be started). Fail-closed (invariant 2): the
/// factory throws rather than returning a session over an empty or half-populated worktree, so no
/// write-path node ever runs against an un-isolated tree. Any message here is token-scrubbed.
/// </summary>
public sealed class RunnerSetupException : Exception
{
    public RunnerSetupException(string message) : base(message) { }
    public RunnerSetupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when an allowlisted program cannot be launched at all (e.g. not installed / not on PATH).
/// Distinct from <see cref="CommandNotAllowedException"/> (a policy refusal) and from an ordinary
/// non-zero <see cref="Harness.Contracts.CommandResult"/> (the program ran and failed).
/// </summary>
public sealed class RunnerExecutionException(string message, Exception inner)
    : Exception(message, inner);
