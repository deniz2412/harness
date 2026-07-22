namespace Harness.Contracts;

// Appended-to only: the numeric values are persisted, so new members go at the end.
public enum RunStatus { Pending, Running, AwaitingApproval, Completed, Failed, PolicyBlocked, Rejected }

public sealed class Run
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Workflow { get; init; }
    /// <summary>Content hash of the workflow YAML + every prompt it references (WorkflowLoader);
    /// not a git SHA — the definitions are mounted read-only with no git in the container.</summary>
    public required string WorkflowSha { get; init; }
    public required string Initiator { get; init; }
    public required string Repo { get; init; }
    public int? PullRequest { get; init; }
    public int? Issue { get; init; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Chain-head anchor: the seq and hash of the latest audit event, updated as each event is
    /// emitted. Verification checks the chain terminates exactly here, so deleting the tail (or
    /// every event) of a run is caught instead of leaving a shorter, internally consistent chain
    /// that still verifies. HeadSeq 0 means no events yet.
    /// (Tamper-*evidence*; true resistance to a DB-owner rewrite of this anchor is the append-only
    /// runtime role at graduation — see docs/threat-model.md F4.)
    /// </summary>
    public long HeadSeq { get; set; }
    public string? HeadHash { get; set; }
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
    /// <summary>Chain hash binding the previous hash and this event's RunId/Seq/Type/Node/Ts +
    /// payload (see Harness.Audit.ChainHash). Any of those changing breaks the chain from here on.</summary>
    public required string PayloadHash { get; init; }
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
