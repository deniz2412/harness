using System.Security.Cryptography;
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
    public async Task EmitAsync(Guid runId, string type, string node, string payload,
        CancellationToken ct, int tokensIn = 0, int tokensOut = 0, decimal costUsd = 0m)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var prev = await db.Events.Where(e => e.RunId == runId)
            .OrderByDescending(e => e.Seq).FirstOrDefaultAsync(ct);
        var seq = (prev?.Seq ?? 0) + 1;
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes((prev?.PayloadHash ?? "genesis") + payload)));

        var dir = Path.Combine(payloadDir, runId.ToString());
        Directory.CreateDirectory(dir);
        var payloadPath = Path.Combine(dir, $"{seq:D6}.json");
        await File.WriteAllTextAsync(payloadPath, payload, ct);

        db.Events.Add(new RunEvent
        {
            RunId = runId, Seq = seq, Type = type, Node = node,
            PayloadHash = hash, PayloadRef = $"file://{payloadPath}",
            TokensIn = tokensIn, TokensOut = tokensOut, CostUsd = costUsd
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Recomputes the chain for a run; returns first broken seq or null if intact.</summary>
    public async Task<long?> VerifyChainAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var events = await db.Events.Where(e => e.RunId == runId).OrderBy(e => e.Seq).ToListAsync(ct);
        var prevHash = "genesis";
        foreach (var e in events)
        {
            var payload = e.PayloadRef is not null && File.Exists(e.PayloadRef.Replace("file://", ""))
                ? await File.ReadAllTextAsync(e.PayloadRef.Replace("file://", ""), ct) : "";
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prevHash + payload)));
            if (expected != e.PayloadHash) return e.Seq;
            prevHash = e.PayloadHash;
        }
        return null;
    }
}
