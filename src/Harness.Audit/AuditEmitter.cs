using System.Collections.Concurrent;
using System.Text;
using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Audit;

/// <summary>
/// Append-only, hash-chained audit stream. Each event's hash covers the previous event's hash
/// plus the payload — tampering with any event breaks the chain from that point forward.
/// Full payloads go to the audit volume; the DB row stores hash + reference.
/// </summary>
public sealed class AuditEmitter(IDbContextFactory<HarnessDbContext> dbFactory, string payloadDir)
{
    /// <summary>
    /// Appends are serialised per run. Two concurrent emits would both read the same predecessor
    /// hash and produce a chain that verification correctly calls broken — a false tamper alarm.
    /// This covers one process (the current single-instance API); a second harness instance would
    /// need a DB-level lock, which arrives with the background queue.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RunLocks = new();

    private readonly ChainVerifier _verifier = new(dbFactory);

    /// <summary>Sentinel node id for run-level events that belong to no node.</summary>
    public const string NoNode = "-";

    public async Task EmitAsync(Guid runId, string type, string node, string payload,
        CancellationToken ct, int tokensIn = 0, int tokensOut = 0, decimal costUsd = 0m)
    {
        var gate = RunLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Tracked (not AsNoTracking): the head-anchor update below rides in the SAME
            // SaveChanges — one transaction — as the event insert, so the anchor can never point
            // at an event that didn't commit, nor an event commit without its anchor advancing.
            // Null only if the run row is absent (a stray emit with no run); we still record the
            // event — it is the crown jewel — and let verification flag the missing run.
            var runRow = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);

            var prev = await db.Events.Where(e => e.RunId == runId)
                .OrderByDescending(e => e.Seq).FirstOrDefaultAsync(ct);
            var seq = (prev?.Seq ?? 0) + 1;
            var bytes = Encoding.UTF8.GetBytes(payload);
            // Truncate to the precision the DB preserves and both store and hash that same value,
            // so recomputation matches after a Postgres round trip (see ChainHash.NormalizeTs).
            var ts = ChainHash.NormalizeTs(DateTimeOffset.UtcNow);
            // Bind the security-relevant metadata (runId, seq, type, node, ts), not just the
            // payload, so a later retype/reattribute/renumber/backdate of the row breaks
            // verification. Emitter and verifier share this one definition — see ChainHash.
            var hash = ChainHash.Compute(
                prev?.PayloadHash ?? ChainHash.Genesis, runId, seq, type, node, ts, bytes);

            var dir = Path.Combine(payloadDir, runId.ToString());
            Directory.CreateDirectory(dir);
            var payloadPath = Path.Combine(dir, $"{seq:D6}.json");

            // CreateNew, not Create: a payload file is written exactly once. Overwriting an
            // existing one would silently break the chain of whoever wrote it first.
            await using (var fs = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await fs.WriteAsync(bytes, ct);
            }

            db.Events.Add(new RunEvent
            {
                RunId = runId, Seq = seq, Type = type, Node = node, Ts = ts,
                PayloadHash = hash, PayloadRef = $"file://{payloadPath}",
                TokensIn = tokensIn, TokensOut = tokensOut, CostUsd = costUsd
            });

            // Advance the chain-head anchor in lock-step with the event. Only these two columns are
            // touched, so this never reverts a Status/FinishedAt the executor set via EfRunStore —
            // which in turn leaves these two columns alone. The two writers own disjoint columns of
            // Runs and cannot clobber each other. This is what makes tail-deletion detectable: a
            // deleted tail leaves a shorter but internally consistent chain, and only the anchor
            // records where the chain was supposed to end.
            if (runRow is not null)
            {
                runRow.HeadSeq = seq;
                runRow.HeadHash = hash;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                // No row means no event: leave no orphan payload claiming a seq we did not take,
                // otherwise the next emit collides on CreateNew and the run cannot be audited.
                try { File.Delete(payloadPath); } catch (IOException) { /* best effort */ }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Recomputes the chain for a run; returns the first broken seq, or null if intact.
    /// A return of <see cref="ChainVerificationResult.ChainLevelSeq"/> (0) means the chain as a
    /// whole could not be verified (unknown run, or a run whose events were deleted wholesale) —
    /// broken, not intact. Callers wanting the per-event detail should use
    /// <see cref="VerifyAsync"/>.
    /// </summary>
    public async Task<long?> VerifyChainAsync(Guid runId, CancellationToken ct)
    {
        var result = await _verifier.VerifyAsync(runId, ct);
        return result.Intact ? null : result.FirstBrokenSeq ?? ChainVerificationResult.ChainLevelSeq;
    }

    /// <summary>Full verification result: per-event status plus a reason for the verdict.</summary>
    public Task<ChainVerificationResult> VerifyAsync(Guid runId, CancellationToken ct = default) =>
        _verifier.VerifyAsync(runId, ct);

    /// <summary>
    /// Node id → that node's output, read back from the run's <c>node_end</c> payloads; the latest
    /// event for a node wins, so a re-run overwrites an earlier attempt. A node whose payload is
    /// missing or unreadable is omitted rather than reported as empty: an absent entry makes the
    /// engine re-run the node, whereas an empty string would silently feed "" to its dependants.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadNodeOutputsAsync(
        Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var events = await db.Events.AsNoTracking()
            .Where(e => e.RunId == runId && e.Type == "node_end")
            .OrderBy(e => e.Seq).ToListAsync(ct);

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Node == NoNode) continue;   // run-level error event, not a node output

            var path = PayloadPath.Resolve(e.PayloadRef, runId, null);
            try
            {
                if (path is null || !File.Exists(path)) { outputs.Remove(e.Node); continue; }
                outputs[e.Node] = await File.ReadAllTextAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outputs.Remove(e.Node);
            }
        }
        return outputs;
    }
}
