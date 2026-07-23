namespace Harness.Contracts;

/// <summary>The outcome of a sandboxed command: its exit code and captured output (size-capped).</summary>
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// An isolated workspace for the write-capable nodes of a single run: a checked-out worktree the
/// nodes read and write, plus bounded command execution against it. One session per run — the
/// worktree is shared across the run's implement / validate / push nodes because they all act on
/// the same branch.
///
/// This is the seam the spec's ephemeral runner *container* sits behind (design-spec §2.3/§2.4).
/// The M2 implementation is a sandboxed local subprocess with a temp worktree; the container
/// implementation is a drop-in replacement of this interface when the platform graduates off a
/// single workstation — nothing above this interface may assume which one it is.
/// </summary>
public interface IRunnerSession : IAsyncDisposable
{
    /// <summary>Absolute path to the run's worktree. Write tools and bash commands act here only.</summary>
    string WorktreePath { get; }

    /// <summary>
    /// Runs one command inside the sandbox. Fail-closed: a command outside the allowlist, or one
    /// that exceeds the time or output bound, is refused or terminated rather than run unbounded.
    /// Never throws for an ordinary non-zero exit — that is a <see cref="CommandResult"/>, which the
    /// caller (e.g. an agent-loop validation step) interprets.
    /// </summary>
    Task<CommandResult> RunAsync(string command, CancellationToken ct);
}

/// <summary>
/// Creates a per-run sandbox. Implemented by Harness.Runner, consumed by the write-path node
/// executors and, through the tool-call context, by the write tools. Read-only workflows
/// (e.g. pr-review) never call this — no worktree is created for a run that does not write.
/// </summary>
public interface IRunnerFactory
{
    /// <summary>
    /// Prepares an isolated worktree for <paramref name="run"/> at <paramref name="baseRef"/>
    /// (branch or SHA). The caller disposes the returned session, which tears the worktree down.
    /// </summary>
    Task<IRunnerSession> CreateAsync(Run run, string baseRef, CancellationToken ct);
}
