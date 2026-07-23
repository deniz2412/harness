using Harness.Audit;
using Harness.Contracts;
using Harness.Engine;
using Harness.Api.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Harness.Api.Tests;

/// <summary>InMemory <see cref="IDbContextFactory{T}"/>: every context shares one store (same name +
/// root), so data written through one context is visible to the next — the factory pattern the
/// services use, without Postgres.</summary>
internal sealed class InMemoryDbContextFactory : IDbContextFactory<HarnessDbContext>
{
    private readonly DbContextOptions<HarnessDbContext> _options;

    public InMemoryDbContextFactory()
    {
        _options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseInMemoryDatabase($"harness-{Guid.NewGuid()}", new InMemoryDatabaseRoot())
            .Options;
    }

    public HarnessDbContext CreateDbContext() => new(_options);
}

/// <summary>In-memory <see cref="IRunStore"/> — a dictionary, no tracking graph.</summary>
internal sealed class FakeRunStore : IRunStore
{
    private readonly Dictionary<Guid, Run> _runs = new();

    public Task SaveAsync(Run run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<Run?> GetAsync(Guid runId, CancellationToken ct = default) =>
        Task.FromResult(_runs.TryGetValue(runId, out var r) ? r : null);

    public bool Contains(Guid runId) => _runs.ContainsKey(runId);
}

/// <summary>
/// Configurable <see cref="IApprovalStore"/> for the coordinator's gate guards. It can be told to
/// throw <see cref="GateNotFoundException"/> or <see cref="GateAlreadyDecidedException"/>, and
/// otherwise records the decision so a test can assert it was recorded before the run resumed.
/// </summary>
internal sealed class FakeApprovalStore : IApprovalStore
{
    public bool ThrowNotFound { get; set; }
    public bool ThrowAlreadyDecided { get; set; }

    public GateDecision? LastDecision { get; private set; }
    public string? LastApprover { get; private set; }
    public string? LastReason { get; private set; }

    public Task<GateApproval> RequestAsync(Guid runId, string node, CancellationToken ct = default) =>
        Task.FromResult(new GateApproval { RunId = runId, Node = node });

    public Task<GateApproval?> GetAsync(Guid runId, string node, CancellationToken ct = default) =>
        Task.FromResult<GateApproval?>(null);

    public Task<GateApproval> DecideAsync(Guid runId, string node, GateDecision decision,
        string approver, string? reason, CancellationToken ct = default)
    {
        if (ThrowNotFound) throw new GateNotFoundException(runId, node);
        if (ThrowAlreadyDecided) throw new GateAlreadyDecidedException(runId, node, GateDecision.Approved);

        LastDecision = decision;
        LastApprover = approver;
        LastReason = reason;
        return Task.FromResult(new GateApproval
        {
            RunId = runId, Node = node, Decision = decision, Approver = approver, Reason = reason,
            DecidedAt = DateTimeOffset.UtcNow
        });
    }
}

/// <summary>Fake <see cref="IWorkflowRunner"/> — records that it ran (and lets a test observe state
/// at the moment it ran) instead of reaching a gateway. Optionally throws to exercise the
/// coordinator's error handling.</summary>
internal sealed class FakeWorkflowRunner : IWorkflowRunner
{
    private readonly Func<Run, WorkflowDefinition, Task>? _onExecute;

    public TaskCompletionSource<bool> Executed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeWorkflowRunner(Func<Run, WorkflowDefinition, Task>? onExecute = null) =>
        _onExecute = onExecute;

    public async Task ExecuteAsync(Run run, WorkflowDefinition wf, CancellationToken ct)
    {
        try
        {
            if (_onExecute is not null) await _onExecute(run, wf);
        }
        finally
        {
            Executed.TrySetResult(true);
        }
    }
}

/// <summary>Writes a minimal but valid workflow (+ empty prompts dir) to a temp location and hands
/// back a real <see cref="WorkflowLoader"/> plus the sha it stamps — so the coordinator's sha-match
/// guard is exercised against the genuine loader.</summary>
internal sealed class TempWorkflow : IDisposable
{
    public string Root { get; }
    public WorkflowLoader Loader { get; }
    public string Name => "wf";
    public string Sha { get; }

    public TempWorkflow()
    {
        Root = Path.Combine(Path.GetTempPath(), "harness-api-tests", Guid.NewGuid().ToString("N"));
        var workflowsDir = Path.Combine(Root, "workflows");
        var promptsDir = Path.Combine(Root, "prompts");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(promptsDir);

        // A single bash node: no prompt_ref required, no gateway involved — it just has to load.
        File.WriteAllText(Path.Combine(workflowsDir, "wf.yaml"),
            "name: wf\n" +
            "permissions:\n" +
            "  repo: read\n" +
            "nodes:\n" +
            "  - id: n1\n" +
            "    kind: bash\n" +
            "    run: echo hi\n");

        Loader = new WorkflowLoader(workflowsDir, promptsDir);
        Sha = Loader.Load(Name).Sha;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
