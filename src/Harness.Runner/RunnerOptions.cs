namespace Harness.Runner;

/// <summary>
/// Bounds and policy for the subprocess runner. Every field has a safe default; the orchestrator
/// overrides <see cref="WorktreeRoot"/> to point at the mounted <c>worktrees</c> volume and may
/// tighten the allowlist or the bounds. All values are constructor-injected — nothing here is read
/// from the environment or a config file by this project.
/// </summary>
public sealed class RunnerOptions
{
    /// <summary>
    /// Directory under which each run's worktree is created as a unique subdirectory. Defaults to a
    /// folder in the system temp dir so tests and ad-hoc use need no configuration; the orchestrator
    /// points it at the per-run <c>worktrees</c> volume.
    /// </summary>
    public string WorktreeRoot { get; init; } =
        Path.Combine(Path.GetTempPath(), "harness-worktrees");

    /// <summary>
    /// Programs the runner may launch, matched against the first token of the command with
    /// <see cref="StringComparer.Ordinal"/> (fail-closed: exact, case-sensitive, bare program name —
    /// no path, no shell resolution beyond the OS PATH lookup). Defaults cover the write path
    /// (<c>git</c>, <c>dotnet</c>) plus the M6 security scanners' analyzer tooling: <c>gitleaks</c>
    /// (secret detection — read-only, produces findings). Every entry is a curated addition made by
    /// reviewed platform change (invariant 7); notably absent and never to be added: anything that
    /// merges or creates/deletes repos, or any offensive tool (invariant 1, vision §3 boundary).
    /// </summary>
    public IReadOnlyCollection<string> AllowedPrograms { get; init; } = new[] { "git", "dotnet", "gitleaks" };

    /// <summary>Per-command wall-clock budget. On overrun the process tree is killed and the command
    /// returns <see cref="SubprocessRunnerSession.TimeoutExitCode"/> (closes threat-model F7's time half).</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Wall-clock budget for the one-time clone in <see cref="SubprocessRunnerFactory.CreateAsync"/>.</summary>
    public TimeSpan CloneTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cap on captured bytes <em>per stream</em> (stdout and stderr independently). Output beyond the
    /// cap is drained but discarded, and the captured text ends with a <c>…[truncated N bytes]</c>
    /// marker so the caller knows it is not seeing everything (closes threat-model F7's size half).
    /// The child is never allowed to fill the pipe unbounded.
    /// </summary>
    public int MaxOutputBytes { get; init; } = 1024 * 1024; // 1 MiB per stream
}
