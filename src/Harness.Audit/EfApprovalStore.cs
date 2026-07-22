using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Audit;

/// <summary>
/// Thrown when a gate that already carries a decision is decided again. Fail-closed: a recorded
/// Approved/Rejected is final, and an attempt to overwrite it is an error the caller must see
/// (HTTP 409), never a silent replacement.
/// </summary>
public sealed class GateAlreadyDecidedException(Guid runId, string node, GateDecision decision)
    : InvalidOperationException(
        $"Gate '{node}' on run {runId} is already {decision}; a decided gate cannot be re-decided.")
{
    public Guid RunId { get; } = runId;
    public string Node { get; } = node;
    public GateDecision Decision { get; } = decision;
}

/// <summary>Thrown when a decision arrives for a gate nobody requested.</summary>
public sealed class GateNotFoundException(Guid runId, string node)
    : InvalidOperationException($"No gate '{node}' was requested for run {runId}.")
{
    public Guid RunId { get; } = runId;
    public string Node { get; } = node;
}

/// <summary>
/// EF-backed <see cref="IApprovalStore"/>. A gate decision is a governance act on the run, so it
/// is audited: the <c>gate_decision</c> event is emitted <em>before</em> the decision is persisted
/// (invariant 5). If the audit emit fails, nothing is written and the decision does not happen —
/// an over-recorded audit trail is recoverable, an unaudited approval is not.
/// </summary>
public sealed class EfApprovalStore(
    IDbContextFactory<HarnessDbContext> dbFactory, AuditEmitter audit) : IApprovalStore
{
    public async Task<GateApproval> RequestAsync(Guid runId, string node, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Idempotent: re-reaching the node (resume, retry) must not reset a decision already made.
        var existing = await db.Approvals.FirstOrDefaultAsync(a => a.RunId == runId && a.Node == node, ct);
        if (existing is not null) return existing;

        var approval = new GateApproval { RunId = runId, Node = node };
        db.Approvals.Add(approval);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race against a concurrent request; the unique index held, so re-read.
            await using var retry = await dbFactory.CreateDbContextAsync(ct);
            return await retry.Approvals.AsNoTracking()
                       .FirstOrDefaultAsync(a => a.RunId == runId && a.Node == node, ct)
                   ?? throw new GateNotFoundException(runId, node);
        }
        return approval;
    }

    public async Task<GateApproval?> GetAsync(Guid runId, string node, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Approvals.AsNoTracking()
            .FirstOrDefaultAsync(a => a.RunId == runId && a.Node == node, ct);
    }

    public async Task<GateApproval> DecideAsync(Guid runId, string node, GateDecision decision,
        string approver, string? reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        if (decision is not (GateDecision.Approved or GateDecision.Rejected))
            throw new ArgumentOutOfRangeException(nameof(decision),
                "A gate can only be decided Approved or Rejected.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.RunId == runId && a.Node == node, ct)
                       ?? throw new GateNotFoundException(runId, node);

        if (approval.Decision != GateDecision.Pending)
            throw new GateAlreadyDecidedException(runId, node, approval.Decision);

        // Audit before the write, so no decision can exist without a matching event.
        await audit.EmitAsync(runId, "gate_decision", node,
            $"{{\"decision\":\"{decision}\",\"approver\":{Json(approver)},\"reason\":{Json(reason)}}}", ct);

        approval.Decision = decision;
        approval.Approver = approver;
        approval.Reason = reason;
        approval.DecidedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The concurrency token on Decision caught a decider who got there first.
            var current = await GetAsync(runId, node, ct);
            throw new GateAlreadyDecidedException(runId, node, current?.Decision ?? GateDecision.Pending);
        }
        return approval;
    }

    private static string Json(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);
}
