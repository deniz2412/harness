namespace Harness.Contracts;

public enum RunStatus { Pending, Running, AwaitingApproval, Completed, Failed, PolicyBlocked }

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
    /// <summary>node_start | node_end | model_call | tool_call | gate_decision | policy_block</summary>
    public required string Type { get; init; }
    public required string Node { get; init; }
    public required string PayloadHash { get; init; }  // sha256(prev_hash + payload) — hash chain
    public string? PayloadRef { get; init; }           // file://audit-payloads/...
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public decimal CostUsd { get; init; }
}
