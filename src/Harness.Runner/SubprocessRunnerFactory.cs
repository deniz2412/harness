using Harness.Contracts;

namespace Harness.Runner;

/// <summary>
/// Prepares a per-run isolated worktree by shallow-cloning the run's repo, and hands back a
/// <see cref="SubprocessRunnerSession"/> over it (<see cref="IRunnerFactory"/>).
///
/// CONTAINER NOTE: this factory is the other half the future container implementation replaces
/// (alongside <see cref="ProcessExecutor"/>). Instead of cloning into a host temp directory, it would
/// start an ephemeral, non-root runner container with the worktree as its only mount and egress
/// restricted to gateway + GitHub (design-spec §2.3, threat-model TB8/F11), and clone inside it. The
/// interface, the token handling and the bounds are unchanged; only where the worktree lives changes.
/// RESIDUAL RISK (documented for the threat model): a subprocess has <b>no egress control</b> — F11
/// is not closed by this workstream and cannot be from user space. See the integration notes.
/// </summary>
public sealed class SubprocessRunnerFactory : IRunnerFactory
{
    /// <summary>Sentinel meaning "the repo's default branch — let git resolve it" (never assume "main").</summary>
    public const string DefaultBranchSentinel = "HEAD";

    private readonly Func<string> _tokenAccessor;
    private readonly RunnerOptions _options;
    private readonly Func<Run, string> _remoteUrlProvider;

    /// <param name="tokenAccessor">
    /// Supplies the GitHub token at clone time. The orchestrator wires this to the same source the
    /// rest of the platform uses (e.g. <c>() =&gt; Environment.GetEnvironmentVariable("GITHUB_TOKEN")!</c>).
    /// It is called only to build the authenticated remote URL and is never logged; this project
    /// never reads <c>.env</c> itself.
    /// </param>
    /// <param name="options">Bounds and allowlist. Defaults are safe; the orchestrator points
    /// <see cref="RunnerOptions.WorktreeRoot"/> at the mounted worktrees volume.</param>
    /// <param name="remoteUrlProvider">
    /// Test/extension seam that maps a <see cref="Run"/> to the git clone source. Defaults to the
    /// authenticated <c>https://github.com/{owner}/{name}.git</c> URL. Tests inject a local
    /// bare-repo path here so the whole suite runs offline with no real GitHub, no network, no docker.
    /// </param>
    public SubprocessRunnerFactory(
        Func<string> tokenAccessor,
        RunnerOptions? options = null,
        Func<Run, string>? remoteUrlProvider = null)
    {
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
        _options = options ?? new RunnerOptions();
        _remoteUrlProvider = remoteUrlProvider ?? DefaultRemoteUrl;
        Directory.CreateDirectory(_options.WorktreeRoot);
    }

    private string DefaultRemoteUrl(Run run) =>
        // owner/name is embedded as-is; the token is placed in the userinfo and never logged.
        $"https://x-access-token:{_tokenAccessor()}@github.com/{run.Repo}.git";

    public async Task<IRunnerSession> CreateAsync(Run run, string baseRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(baseRef))
            throw new ArgumentException("baseRef must be a branch/SHA or the \"HEAD\" sentinel.", nameof(baseRef));

        // The worktree path is deterministic per run (the run id), not random, so that RESUMING a run
        // finds the same worktree. A write workflow does its work (writes files into the tree) before
        // the human gate; those changes are uncommitted until push_branch. If resume re-cloned into a
        // fresh directory, the approved work would be gone and there would be nothing to push. So an
        // already-populated worktree for this run is reused as-is; the caller (DagExecutor) is what
        // keeps it alive across the pause by not disposing the session then.
        var target = Path.Combine(_options.WorktreeRoot, run.Id.ToString("N"));
        if (Directory.Exists(Path.Combine(target, ".git")))
            return new SubprocessRunnerSession(target, _options);

        // Not a resume (or a stale remnant): clear anything left behind, then clone fresh. git
        // requires `target` not to pre-exist as a non-empty directory.
        WorktreeCleanup.TryDelete(target);

        var remote = _remoteUrlProvider(run);

        // Shallow, single-branch clone. With the HEAD sentinel we omit --branch so git resolves the
        // repo's default branch itself (never assume "main"). git creates `target`.
        var args = new List<string> { "clone", "--depth", "1", "--single-branch" };
        if (!string.Equals(baseRef, DefaultBranchSentinel, StringComparison.Ordinal))
        {
            args.Add("--branch");
            args.Add(baseRef);
        }
        args.Add(remote);
        args.Add(target);

        ProcessOutcome outcome;
        try
        {
            outcome = await ProcessExecutor.ExecuteAsync(
                "git", args, _options.WorktreeRoot, _options.CloneTimeout, _options.MaxOutputBytes, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            WorktreeCleanup.TryDelete(target);
            throw;
        }
        catch (Exception ex)
        {
            WorktreeCleanup.TryDelete(target);
            throw new RunnerSetupException(
                $"git clone could not be started for {run.Repo} @ {baseRef}: {Scrub(ex.Message)}", ex);
        }

        if (outcome.TimedOut)
        {
            WorktreeCleanup.TryDelete(target);
            throw new RunnerSetupException(
                $"git clone timed out after {_options.CloneTimeout.TotalSeconds:0.###}s for {run.Repo} @ {baseRef}.");
        }

        if (outcome.ExitCode != 0)
        {
            WorktreeCleanup.TryDelete(target);
            throw new RunnerSetupException(
                $"git clone failed (exit {outcome.ExitCode}) for {run.Repo} @ {baseRef}: {Scrub(outcome.Stderr)}");
        }

        return new SubprocessRunnerSession(target, _options);
    }

    /// <summary>Removes the token from any text that becomes an exception message, in case git echoed
    /// the remote URL. The URL itself is never logged by this project.</summary>
    private string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        string token;
        try { token = _tokenAccessor(); }
        catch { return text; }
        return string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");
    }
}
