using Harness.Audit;

namespace Harness.Engine;

/// <summary>
/// The slice of the audit stream the executor needs: append an event, and read back the outputs
/// of the nodes that already completed (used to resume a paused run).
/// <para>
/// This exists so the engine stays unit-testable without a database — <see cref="AuditEmitter"/>
/// is a concrete, Postgres-backed type. It is a seam, not a second implementation: the only
/// production implementation is <see cref="AuditEmitterLog"/>, which forwards verbatim.
/// </para>
/// </summary>
public interface IAuditLog
{
    Task EmitAsync(Guid runId, string type, string node, string payload, CancellationToken ct);

    /// <summary>node id → that node's output, from the <c>node_end</c> payloads of the run, latest wins.</summary>
    Task<IReadOnlyDictionary<string, string>> ReadNodeOutputsAsync(Guid runId, CancellationToken ct);
}

/// <summary>Production <see cref="IAuditLog"/> — the hash-chained emitter in Harness.Audit.</summary>
public sealed class AuditEmitterLog(AuditEmitter audit) : IAuditLog
{
    public Task EmitAsync(Guid runId, string type, string node, string payload, CancellationToken ct)
        => audit.EmitAsync(runId, type, node, payload, ct);

    public Task<IReadOnlyDictionary<string, string>> ReadNodeOutputsAsync(Guid runId, CancellationToken ct)
        => audit.ReadNodeOutputsAsync(runId, ct);   // added by the audit workstream (M1 WS1)
}
