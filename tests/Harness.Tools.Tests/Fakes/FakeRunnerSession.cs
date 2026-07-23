using Harness.Contracts;

namespace Harness.Tools.Tests.Fakes;

/// <summary>
/// A recording <see cref="IRunnerSession"/>: it captures the exact command strings the toolset issues
/// and returns scripted <see cref="CommandResult"/>s, so a test can assert the git sequence and drive
/// a non-zero exit at a chosen step. It runs no process — the write-path tests stay fully offline.
/// </summary>
internal sealed class FakeRunnerSession : IRunnerSession
{
    private readonly Func<string, CommandResult> _script;

    public FakeRunnerSession(string worktreePath, Func<string, CommandResult> script)
    {
        WorktreePath = worktreePath;
        _script = script;
    }

    /// <summary>Every command the toolset ran, in order.</summary>
    public List<string> Commands { get; } = [];

    public string WorktreePath { get; }

    public Task<CommandResult> RunAsync(string command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.FromResult(_script(command));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A runner whose every command succeeds with empty output.</summary>
    public static FakeRunnerSession AlwaysOk(string worktreePath = "/work") =>
        new(worktreePath, _ => new CommandResult(0, "", ""));

    /// <summary>A runner that fails the first command whose text contains <paramref name="failToken"/>.</summary>
    public static FakeRunnerSession FailingAt(string failToken, string stderr, string worktreePath = "/work") =>
        new(worktreePath, cmd => cmd.Contains(failToken)
            ? new CommandResult(1, "", stderr)
            : new CommandResult(0, "", ""));
}
