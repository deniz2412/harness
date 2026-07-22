using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Audit;

/// <summary>Why a single event failed verification. <see cref="Ok"/> is the only good value.</summary>
public enum ChainStatus
{
    Ok,
    /// <summary>The DB row carries no payload reference at all — nothing to hash against.</summary>
    PayloadRefMissing,
    /// <summary>The referenced payload file is gone. Deleting a payload is tampering.</summary>
    PayloadMissing,
    /// <summary>The payload file exists but could not be read (permissions, IO error).</summary>
    PayloadUnreadable,
    /// <summary>The recomputed hash does not match the stored one — payload or hash was altered.</summary>
    HashMismatch,
    /// <summary>A seq is missing before this event — an event was removed or renumbered.</summary>
    SeqGap,
    /// <summary>This seq was already seen — events were duplicated or reordered.</summary>
    DuplicateSeq
}

/// <param name="Status">Ok, or the first problem found for this event.</param>
public sealed record ChainEventCheck(
    long Seq, string Type, string Node, DateTimeOffset Ts, ChainStatus Status, string? Detail)
{
    public bool Ok => Status == ChainStatus.Ok;
}

/// <param name="FirstBrokenSeq">
/// The seq of the first failing event, or <see cref="ChainVerificationResult.ChainLevelSeq"/> (0)
/// when the problem is the chain as a whole rather than one event (unknown run, wholesale
/// deletion). Never null when <c>Intact</c> is false — a caller must always be able to report a
/// number rather than fall back to "looks fine".
/// </param>
public sealed record ChainVerificationResult(
    Guid RunId, bool Intact, long? FirstBrokenSeq, string? Reason, IReadOnlyList<ChainEventCheck> Events)
{
    /// <summary>Seqs start at 1, so 0 is free to mean "the chain itself could not be verified".</summary>
    public const long ChainLevelSeq = 0;
}

/// <summary>
/// Recomputes a run's hash chain from the DB rows plus the payload files on the audit volume.
/// Fail-closed: anything that stops us proving an event is untampered — a missing payload file,
/// an unreadable one, a gap in the sequence — is reported as broken, never skipped. The M0
/// implementation treated a missing payload as the empty string, which meant deleting a payload
/// from the volume left the chain reporting intact; that is the hole this class closes.
///
/// Each link is checked against its stored predecessor hash rather than against a running
/// recomputed hash, so one tampered event is reported at its own seq instead of poisoning every
/// event after it. Independent of the API: the CLI uses this class directly.
/// </summary>
public sealed class ChainVerifier(IDbContextFactory<HarnessDbContext> dbFactory, string? payloadRootOverride = null)
{
    public async Task<ChainVerificationResult> VerifyAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        var events = await db.Events.AsNoTracking()
            .Where(e => e.RunId == runId).OrderBy(e => e.Seq).ToListAsync(ct);

        if (run is null && events.Count == 0)
            return new ChainVerificationResult(runId, false, ChainVerificationResult.ChainLevelSeq,
                "no such run: neither a run row nor any events exist, so there is nothing to verify",
                []);

        if (events.Count == 0)
        {
            // A run that got as far as Running must have emitted node_start. Zero events for such a
            // run means the trail was deleted wholesale, which is exactly what an attacker would do.
            var expectedEvents = run is not null && run.Status != RunStatus.Pending;
            return expectedEvents
                ? new ChainVerificationResult(runId, false, ChainVerificationResult.ChainLevelSeq,
                    $"run status is {run!.Status} but the run has no audit events", [])
                : new ChainVerificationResult(runId, true, null, "run has not emitted any events yet", []);
        }

        var checks = new List<ChainEventCheck>(events.Count);
        var expectedSeq = 1L;
        var seen = new HashSet<long>();
        var prevHash = ChainHash.Genesis;

        foreach (var e in events)
        {
            // Sequence first: a gap is the root cause, and reporting it beats reporting the
            // hash mismatch it necessarily produces in the event that follows the hole.
            (ChainStatus Status, string? Detail)? seqIssue = null;
            if (!seen.Add(e.Seq))
                seqIssue = (ChainStatus.DuplicateSeq, $"seq {e.Seq} appears more than once");
            else if (e.Seq != expectedSeq)
                seqIssue = (ChainStatus.SeqGap,
                    $"expected seq {expectedSeq}, found {e.Seq} — event(s) removed or renumbered");

            var (status, detail) = seqIssue
                ?? await CheckEventAsync(e, runId, prevHash, ct);

            checks.Add(new ChainEventCheck(e.Seq, e.Type, e.Node, e.Ts, status, detail));
            expectedSeq = e.Seq + 1;
            prevHash = e.PayloadHash;
        }

        var firstBad = checks.FirstOrDefault(c => !c.Ok);
        return firstBad is null
            ? new ChainVerificationResult(runId, true, null, null, checks)
            : new ChainVerificationResult(runId, false, firstBad.Seq, firstBad.Detail, checks);
    }

    private async Task<(ChainStatus Status, string? Detail)> CheckEventAsync(
        RunEvent e, Guid runId, string prevHash, CancellationToken ct)
    {
        var path = PayloadPath.Resolve(e.PayloadRef, runId, payloadRootOverride);
        if (path is null)
            return (ChainStatus.PayloadRefMissing, "event has no payload reference");

        byte[] payload;
        try
        {
            if (!File.Exists(path)) return (ChainStatus.PayloadMissing, "referenced payload file is missing");
            payload = await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Deliberately does not include the exception text: it can echo a path we would rather
            // not surface, and "unreadable" is all the verdict needs.
            return (ChainStatus.PayloadUnreadable, "referenced payload file could not be read");
        }

        // Recompute over the same bound fields the emitter used: the stored RunId/Seq/Type/Node/Ts
        // are hashed alongside the payload, so a retyped, reattributed, renumbered or backdated
        // row — the metadata-mutation hole STRIDE F3 flagged — now fails here as a hash mismatch.
        // Ts is re-normalized before hashing so a value read back from Postgres (microsecond) and
        // one produced in-memory (100 ns ticks) reduce to the identical millisecond instant.
        var expected = ChainHash.Compute(
            prevHash, e.RunId, e.Seq, e.Type, e.Node, ChainHash.NormalizeTs(e.Ts), payload);
        return expected == e.PayloadHash
            ? (ChainStatus.Ok, null)
            : (ChainStatus.HashMismatch, "recomputed hash does not match the stored hash");
    }
}
