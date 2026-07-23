using System.Runtime.InteropServices;
using Harness.Contracts;
using Harness.Runner;
using Xunit;

namespace Harness.Runner.Tests;

/// <summary>
/// End-to-end tests for the subprocess runner, all offline: a local bare git repo stands in for
/// GitHub (no network, no docker). Covers worktree population, the allowlist (fail-closed), ordinary
/// non-zero exits as normal results, the timeout kill, the output cap, token scrubbing, and disposal.
/// </summary>
public sealed class SubprocessRunnerTests : IDisposable
{
    private readonly LocalGitOrigin _origin = LocalGitOrigin.Create();
    private readonly string _worktreeRoot =
        Path.Combine(Path.GetTempPath(), "harness-runner-tests", Guid.NewGuid().ToString("N"));

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static Run NewRun(string repo = "deniz2412/test-repo-harness") => new()
    {
        Workflow = "issue-to-pr",
        WorkflowSha = "test",
        Initiator = "runner-tests",
        Repo = repo,
    };

    private SubprocessRunnerFactory Factory(
        RunnerOptions? options = null,
        Func<string>? token = null,
        Func<Run, string>? remote = null) =>
        new(token ?? (() => "unused-in-local-clone"),
            options ?? new RunnerOptions { WorktreeRoot = _worktreeRoot },
            remote ?? (_ => _origin.BarePath)); // clone from the local bare repo, not github.com

    private RunnerOptions Opts(
        IReadOnlyCollection<string>? allow = null,
        TimeSpan? cmdTimeout = null,
        int? maxOutput = null) => new()
    {
        WorktreeRoot = _worktreeRoot,
        AllowedPrograms = allow ?? new[] { "git", "dotnet" },
        CommandTimeout = cmdTimeout ?? TimeSpan.FromMinutes(5),
        MaxOutputBytes = maxOutput ?? 1024 * 1024,
    };

    [Fact]
    public async Task Worktree_contains_the_cloned_file()
    {
        // baseRef "HEAD" exercises the default-branch sentinel (no branch-name assumption).
        await using var session = await Factory().CreateAsync(NewRun(), "HEAD", CancellationToken.None);

        var file = Path.Combine(session.WorktreePath, LocalGitOrigin.SeededFile);
        Assert.True(File.Exists(file), $"expected cloned file at {file}");
        // Normalize line endings: git's autocrlf may rewrite \n to \r\n on checkout under Windows.
        var content = (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n");
        Assert.Equal(LocalGitOrigin.SeededContent, content);
    }

    [Fact]
    public async Task Allowlisted_command_returns_its_real_exit_code()
    {
        await using var session = await Factory().CreateAsync(NewRun(), "HEAD", CancellationToken.None);

        var version = await session.RunAsync("git --version", CancellationToken.None);
        Assert.Equal(0, version.ExitCode);
        Assert.True(version.Success);
        Assert.Contains("git version", version.Stdout);

        var status = await session.RunAsync("git status --porcelain", CancellationToken.None);
        Assert.Equal(0, status.ExitCode);
    }

    [Fact]
    public async Task Ordinary_nonzero_exit_is_a_result_not_a_throw()
    {
        await using var session = await Factory().CreateAsync(NewRun(), "HEAD", CancellationToken.None);

        // A real git invocation that fails: an unknown subcommand exits non-zero. This must come back
        // as a CommandResult, NOT an exception (the interface contract).
        var result = await session.RunAsync("git no-such-subcommand-xyz", CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Non_allowlisted_program_is_refused_failclosed()
    {
        await using var session = await Factory().CreateAsync(NewRun(), "HEAD", CancellationToken.None);

        // 'python' is not on the default allowlist — refusal is an exception, never an exit 0.
        await Assert.ThrowsAsync<CommandNotAllowedException>(
            () => session.RunAsync("python --version", CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_kills_a_long_running_command()
    {
        // Allow a portable sleeper for this test only; give it 30s of work and a 1s budget.
        var (program, command) = IsWindows
            ? ("ping", "ping -n 30 127.0.0.1")
            : ("sleep", "sleep 30");

        var options = Opts(allow: new[] { "git", program }, cmdTimeout: TimeSpan.FromSeconds(1));
        await using var session = await Factory(options).CreateAsync(NewRun(), "HEAD", CancellationToken.None);

        var start = DateTime.UtcNow;
        var result = await session.RunAsync(command, CancellationToken.None);
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(SubprocessRunnerSession.TimeoutExitCode, result.ExitCode);
        Assert.Contains("timed out", result.Stderr);
        // Proof it was killed, not waited out: nowhere near the 30s the command asked for.
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"command was not killed promptly (took {elapsed}).");
    }

    [Fact]
    public async Task Output_over_the_cap_is_truncated_with_a_marker()
    {
        var options = Opts(maxOutput: 8); // 'git version ...' is comfortably longer than 8 bytes
        await using var session = await Factory(options).CreateAsync(NewRun(), "HEAD", CancellationToken.None);

        var result = await session.RunAsync("git --version", CancellationToken.None);

        Assert.StartsWith("git vers", result.Stdout);          // first 8 bytes kept
        Assert.Contains("truncated", result.Stdout);           // marker present
        Assert.Contains("bytes]", result.Stdout);
    }

    [Fact]
    public async Task Dispose_removes_the_worktree()
    {
        var session = await Factory().CreateAsync(NewRun(), "HEAD", CancellationToken.None);
        var path = session.WorktreePath;
        Assert.True(Directory.Exists(path));

        await session.DisposeAsync();
        Assert.False(Directory.Exists(path), "worktree should be deleted on dispose");
    }

    [Fact]
    public async Task Clone_failure_throws_and_leaves_no_worktree()
    {
        // A local path that is not a repo: clone fails fast, offline. Factory must throw (fail-closed)
        // and not leave a half-populated directory behind.
        var bogus = Path.Combine(_worktreeRoot, "not-a-repo");
        var factory = Factory(remote: _ => bogus);

        await Assert.ThrowsAsync<RunnerSetupException>(
            () => factory.CreateAsync(NewRun(), "HEAD", CancellationToken.None));

        // No leftover worktree subdir under the root (the bogus source dir itself never existed).
        var leftovers = Directory.Exists(_worktreeRoot)
            ? Directory.GetDirectories(_worktreeRoot)
            : Array.Empty<string>();
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task Clone_error_message_never_contains_the_token()
    {
        // Force a failing authenticated clone against a refused loopback port (offline, no egress) and
        // assert the token is scrubbed out of the thrown message even though git echoes the remote URL.
        const string secret = "SUPERSECRETTOKENVALUE";
        var factory = Factory(
            token: () => secret,
            remote: r => $"https://x-access-token:{secret}@127.0.0.1:1/{r.Repo}.git");

        var ex = await Assert.ThrowsAsync<RunnerSetupException>(
            () => factory.CreateAsync(NewRun(), "HEAD", CancellationToken.None));

        Assert.DoesNotContain(secret, ex.Message);
    }

    public void Dispose()
    {
        _origin.Dispose();
        WorktreeCleanupBestEffort(_worktreeRoot);
    }

    private static void WorktreeCleanupBestEffort(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch { /* best effort */ }
    }
}
