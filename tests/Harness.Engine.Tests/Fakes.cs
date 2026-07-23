using Harness.Contracts;

namespace Harness.Engine.Tests;

/// <summary>
/// In-memory audit log. <see cref="ReadNodeOutputsAsync"/> mirrors the contract of the real
/// emitter's method — node id → that node's output, from the run's <c>node_end</c> payloads,
/// latest wins — so the resume tests exercise the real round trip: what the executor wrote as a
/// payload is what it reads back.
/// </summary>
internal sealed class FakeAuditLog : IAuditLog
{
    public List<(string Type, string Node, string Payload)> Events { get; } = [];

    public IEnumerable<string> TypesFor(string node) =>
        Events.Where(e => e.Node == node).Select(e => e.Type);

    public string? PayloadOf(string type, string node) =>
        Events.LastOrDefault(e => e.Type == type && e.Node == node).Payload;

    public Task EmitAsync(Guid runId, string type, string node, string payload, CancellationToken ct)
    {
        Events.Add((type, node, payload));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ReadNodeOutputsAsync(Guid runId, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in Events.Where(e => e.Type == "node_end")) map[e.Node] = e.Payload;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(map);
    }
}

/// <summary>Records every status the executor persisted, in order.</summary>
internal sealed class FakeRunStore : IRunStore
{
    private readonly Dictionary<Guid, Run> _runs = [];
    public List<RunStatus> Transitions { get; } = [];

    public Task SaveAsync(Run run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        Transitions.Add(run.Status);
        return Task.CompletedTask;
    }

    public Task<Run?> GetAsync(Guid runId, CancellationToken ct = default) =>
        Task.FromResult(_runs.GetValueOrDefault(runId));
}

internal sealed class FakeApprovalStore : IApprovalStore
{
    private readonly Dictionary<(Guid, string), GateApproval> _approvals = [];

    public IReadOnlyCollection<GateApproval> All => _approvals.Values;

    /// <summary>Seeds a decision as if a human had already made it, out of band.</summary>
    public FakeApprovalStore With(Guid runId, string node, GateDecision decision, string approver = "deniz")
    {
        _approvals[(runId, node)] = new GateApproval
        {
            RunId = runId, Node = node, Decision = decision, Approver = approver,
            DecidedAt = decision == GateDecision.Pending ? null : DateTimeOffset.UtcNow
        };
        return this;
    }

    public Task<GateApproval> RequestAsync(Guid runId, string node, CancellationToken ct = default)
    {
        if (!_approvals.TryGetValue((runId, node), out var existing))
            _approvals[(runId, node)] = existing = new GateApproval { RunId = runId, Node = node };
        return Task.FromResult(existing);
    }

    public Task<GateApproval?> GetAsync(Guid runId, string node, CancellationToken ct = default) =>
        Task.FromResult(_approvals.GetValueOrDefault((runId, node)));

    public Task<GateApproval> DecideAsync(Guid runId, string node, GateDecision decision,
        string approver, string? reason, CancellationToken ct = default)
    {
        var a = _approvals.TryGetValue((runId, node), out var existing)
            ? existing
            : _approvals[(runId, node)] = new GateApproval { RunId = runId, Node = node };
        a.Decision = decision;
        a.Approver = approver;
        a.Reason = reason;
        a.DecidedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(a);
    }
}

/// <summary>Records which nodes it was asked to run, and what upstream outputs they saw.</summary>
internal sealed class FakeNodeExecutor(string kind = "agent", Func<NodeContext, NodeResult>? behaviour = null)
    : INodeExecutor
{
    public string Kind { get; } = kind;
    public List<string> Executed { get; } = [];
    public Dictionary<string, IReadOnlyDictionary<string, string>> UpstreamSeen { get; } = [];

    /// <summary>The runner session each node saw — null for a read-only run, non-null for a write one.</summary>
    public Dictionary<string, IRunnerSession?> RunnerSeen { get; } = [];

    public Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        Executed.Add(ctx.Node.Id);
        UpstreamSeen[ctx.Node.Id] = new Dictionary<string, string>(ctx.UpstreamOutputs, StringComparer.Ordinal);
        RunnerSeen[ctx.Node.Id] = ctx.Runner;
        return Task.FromResult(behaviour?.Invoke(ctx) ?? new NodeResult(true, $"{ctx.Node.Id}-output"));
    }
}

/// <summary>
/// A sandbox over a real temp directory with a scripted <see cref="RunAsync"/>: each command maps to
/// a chosen exit code (default 0). Records the commands it ran and whether it was disposed, so a test
/// can assert the session was both created and torn down.
/// </summary>
internal sealed class FakeRunnerSession : IRunnerSession
{
    private readonly Func<string, CommandResult> _script;

    public FakeRunnerSession(Func<string, CommandResult>? script = null)
    {
        _script = script ?? (cmd => new CommandResult(0, $"ran: {cmd}", ""));
        WorktreePath = Directory.CreateTempSubdirectory("harness-fake-runner-").FullName;
    }

    public string WorktreePath { get; }
    public List<string> Commands { get; } = [];
    public bool Disposed { get; private set; }

    public Task<CommandResult> RunAsync(string command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.FromResult(_script(command));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        try { Directory.Delete(WorktreePath, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Records every <see cref="CreateAsync"/> call and hands back a <see cref="FakeRunnerSession"/>.
/// A read-only run must never touch this — tests assert <see cref="Created"/> stays empty for
/// pr-review-shaped permissions.
/// </summary>
internal sealed class FakeRunnerFactory(Func<string, CommandResult>? script = null) : IRunnerFactory
{
    public List<(Run Run, string BaseRef)> Created { get; } = [];
    public List<FakeRunnerSession> Sessions { get; } = [];

    public Task<IRunnerSession> CreateAsync(Run run, string baseRef, CancellationToken ct)
    {
        Created.Add((run, baseRef));
        var session = new FakeRunnerSession(script);
        Sessions.Add(session);
        return Task.FromResult<IRunnerSession>(session);
    }
}
