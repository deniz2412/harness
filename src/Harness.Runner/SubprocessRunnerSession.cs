using Harness.Contracts;

namespace Harness.Runner;

/// <summary>
/// A run's isolated worktree plus bounded, allowlisted command execution against it
/// (<see cref="IRunnerSession"/>). Created by <see cref="SubprocessRunnerFactory"/>; the caller
/// disposes it, which deletes the worktree.
///
/// The M2 implementation is a local subprocess. What the future container implementation replaces is
/// noted on <see cref="ProcessExecutor"/> (the launch target) and on <see cref="SubprocessRunnerFactory"/>
/// (how the worktree is materialized). This session type itself — the allowlist gate, the command
/// parsing, the bounds — is behaviour the container version keeps.
/// </summary>
public sealed class SubprocessRunnerSession(string worktreePath, RunnerOptions options) : IRunnerSession
{
    /// <summary>Exit code returned (not thrown) when a command is killed for exceeding the time budget.
    /// 124 matches the convention used by coreutils <c>timeout</c>; the stderr also carries a marker.</summary>
    public const int TimeoutExitCode = 124;

    private readonly HashSet<string> _allowed = new(options.AllowedPrograms, StringComparer.Ordinal);
    private int _disposed;

    public string WorktreePath { get; } = worktreePath;

    public async Task<CommandResult> RunAsync(string command, CancellationToken ct)
    {
        var tokens = CommandLine.Split(command);
        if (tokens.Count == 0)
            throw new ArgumentException("Empty command.", nameof(command));

        var program = tokens[0];

        // Fail-closed allowlist gate (invariant 2). A refusal is an exception, never a CommandResult,
        // so it can never be read as an ordinary exit code by the caller.
        if (!_allowed.Contains(program))
            throw new CommandNotAllowedException(program, _allowed);

        var args = tokens.Skip(1).ToArray();

        var outcome = await ProcessExecutor.ExecuteAsync(
            program, args, WorktreePath, options.CommandTimeout, options.MaxOutputBytes, ct)
            .ConfigureAwait(false);

        if (outcome.TimedOut)
        {
            var marker = $"[timed out after {options.CommandTimeout.TotalSeconds:0.###}s; process tree killed]";
            var stderr = string.IsNullOrEmpty(outcome.Stderr) ? marker : $"{outcome.Stderr}\n{marker}";
            return new CommandResult(TimeoutExitCode, outcome.Stdout, stderr);
        }

        // An ordinary non-zero exit is a normal result — the caller decides what it means.
        return new CommandResult(outcome.ExitCode, outcome.Stdout, outcome.Stderr);
    }

    public ValueTask DisposeAsync()
    {
        // Best-effort teardown: a leftover temp dir is not a crash (per the interface contract).
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            WorktreeCleanup.TryDelete(WorktreePath);
        return ValueTask.CompletedTask;
    }
}
