namespace Harness.Contracts;

// Appended-to only: the numeric values are persisted, so new members go at the end.
public enum RunStatus { Pending, Running, AwaitingApproval, Completed, Failed, PolicyBlocked, Rejected }

public sealed class Run
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Workflow { get; init; }
    public required string WorkflowSha { get; init; }   // git SHA of workflow defs at start
    public required string Initiator { get; init; }
    public required string Repo { get; init; }
    public int? PullRequest { get; init; }
    public int? Issue { get; init; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class RunEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public required Guid RunId { get; init; }
    public required long Seq { get; init; }
    public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// node_start | node_end | model_call | tool_call | tool_result | gate_request |
    /// gate_decision | policy_block
    /// </summary>
    public required string Type { get; init; }
    public required string Node { get; init; }
    public required string PayloadHash { get; init; }  // sha256(prev_hash + payload) — hash chain
    public string? PayloadRef { get; init; }           // file://audit-payloads/...
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public decimal CostUsd { get; init; }
}

public enum GateDecision { Pending, Approved, Rejected }

/// <summary>
/// A human gate on a node. Created when the run reaches a `gate: human` node and pauses;
/// resolved out-of-band by the initiator. The run cannot proceed past the node without it.
/// </summary>
public sealed class GateApproval
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid RunId { get; init; }
    public required string Node { get; init; }
    public GateDecision Decision { get; set; } = GateDecision.Pending;
    public string? Approver { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// Persists run state as it transitions. Without this a paused or failed run is invisible until
/// it terminates — the M0 code only wrote the row back at the very end.
/// </summary>
public interface IRunStore
{
    Task SaveAsync(Run run, CancellationToken ct = default);
    Task<Run?> GetAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>Human-gate decisions. Requesting a gate pauses the run until a decision is recorded.</summary>
public interface IApprovalStore
{
    Task<GateApproval> RequestAsync(Guid runId, string node, CancellationToken ct = default);
    Task<GateApproval?> GetAsync(Guid runId, string node, CancellationToken ct = default);
    Task<GateApproval> DecideAsync(Guid runId, string node, GateDecision decision,
        string approver, string? reason, CancellationToken ct = default);
}
