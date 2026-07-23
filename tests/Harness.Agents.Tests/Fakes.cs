using Harness.Agents;
using Harness.Contracts;
using Harness.Engine;

namespace Harness.Agents.Tests;

/// <summary>In-memory <see cref="IAuditLog"/> recording every event the loop emits.</summary>
internal sealed class FakeAuditLog : IAuditLog
{
    public List<(string Type, string Node, string Payload)> Events { get; } = [];

    public IEnumerable<string> TypesFor(string node) =>
        Events.Where(e => e.Node == node).Select(e => e.Type);

    public int CountOf(string type) => Events.Count(e => e.Type == type);

    public Task EmitAsync(Guid runId, string type, string node, string payload, CancellationToken ct)
    {
        Events.Add((type, node, payload));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ReadNodeOutputsAsync(Guid runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>
/// A sandbox whose <see cref="RunAsync"/> is scripted per call, so a test can make validation fail
/// a chosen number of times before it passes (or never pass at all).
/// </summary>
internal sealed class FakeRunnerSession(Func<string, CommandResult> script) : IRunnerSession
{
    public string WorktreePath => "/fake/worktree";
    public List<string> Commands { get; } = [];

    public Task<CommandResult> RunAsync(string command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.FromResult(script(command));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A sandbox whose validation always returns <paramref name="exitCode"/>.</summary>
    public static FakeRunnerSession Always(int exitCode) =>
        new(_ => new CommandResult(exitCode, "", ""));

    /// <summary>A sandbox whose validation fails <paramref name="failures"/> times, then passes.</summary>
    public static FakeRunnerSession FailsThenPasses(int failures)
    {
        var calls = 0;
        return new FakeRunnerSession(_ =>
            new CommandResult(++calls > failures ? 0 : 1, "", ""));
    }
}

/// <summary>
/// Stands in for the gateway-backed agent turn: counts invocations and records the thread token it
/// was handed each time, so a test can assert fresh-vs-continued context without a live model.
/// </summary>
internal sealed class FakeAgentIteration(string text = "did the work") : IAgentIteration
{
    public int Calls { get; private set; }
    public List<object?> ThreadsSeen { get; } = [];

    public Task<(string Text, object? Thread)> RunAsync(NodeContext ctx, object? thread, CancellationToken ct)
    {
        Calls++;
        ThreadsSeen.Add(thread);
        // Return a stable non-null thread token so a continued-context loop has something to carry.
        return Task.FromResult<(string, object?)>((text, "thread-token"));
    }
}
