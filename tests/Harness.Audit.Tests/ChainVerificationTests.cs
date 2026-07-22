using Harness.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harness.Audit.Tests;

public sealed class ChainVerificationTests
{
    private static Run NewRun() => new()
    {
        Workflow = "pr-review", WorkflowSha = "test", Initiator = "tests",
        Repo = "deniz2412/test-repo-harness", PullRequest = 1, Status = RunStatus.Running
    };

    private static async Task<Run> SeedRunAsync(AuditTestHarness h)
    {
        var run = NewRun();
        await using var db = await h.CreateDbContextAsync();
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private static async Task EmitThreeAsync(AuditTestHarness h, Guid runId)
    {
        await h.Emitter.EmitAsync(runId, "node_start", "gather", "agent", default);
        await h.Emitter.EmitAsync(runId, "model_call", "gather", """{"model":"cheap"}""", default);
        await h.Emitter.EmitAsync(runId, "node_end", "gather", "findings: 3", default);
    }

    [Fact]
    public async Task Clean_chain_verifies()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.True(result.Intact);
        Assert.Null(result.FirstBrokenSeq);
        Assert.Equal(3, result.Events.Count);
        Assert.All(result.Events, e => Assert.Equal(ChainStatus.Ok, e.Status));
        Assert.Null(await h.Emitter.VerifyChainAsync(run.Id, default));
    }

    [Fact]
    public async Task Mutated_payload_is_caught_at_that_seq()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        await File.WriteAllTextAsync(h.PayloadFile(run.Id, 2), """{"model":"strong"}""");

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(2, result.FirstBrokenSeq);
        Assert.Equal(ChainStatus.HashMismatch, result.Events.Single(e => e.Seq == 2).Status);
        // Localised: tampering with one payload does not smear across every later event.
        Assert.Equal(ChainStatus.Ok, result.Events.Single(e => e.Seq == 3).Status);
        Assert.Equal(2, await h.Emitter.VerifyChainAsync(run.Id, default));
    }

    /// <summary>
    /// The M0 regression: a missing payload file was read as the empty string, so deleting a
    /// payload from the audit volume left the chain reporting intact.
    /// </summary>
    [Fact]
    public async Task Deleted_payload_file_is_caught()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        File.Delete(h.PayloadFile(run.Id, 2));

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(2, result.FirstBrokenSeq);
        Assert.Equal(ChainStatus.PayloadMissing, result.Events.Single(e => e.Seq == 2).Status);
        Assert.Equal(2, await h.Emitter.VerifyChainAsync(run.Id, default));
    }

    [Fact]
    public async Task Deleted_middle_event_is_caught()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        await using (var db = await h.CreateDbContextAsync())
        {
            var victim = await db.Events.SingleAsync(e => e.RunId == run.Id && e.Seq == 2);
            db.Events.Remove(victim);
            await db.SaveChangesAsync();
        }

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(3, result.FirstBrokenSeq);      // first event after the hole
        Assert.Equal(ChainStatus.SeqGap, result.Events.Single(e => e.Seq == 3).Status);
    }

    [Fact]
    public async Task Renumbered_event_is_caught()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        // Reordering events by rewriting a seq: the row moves, the hash no longer chains.
        await using (var db = await h.CreateDbContextAsync())
        {
            var third = await db.Events.SingleAsync(e => e.RunId == run.Id && e.Seq == 3);
            db.Events.Remove(third);
            await db.SaveChangesAsync();
            db.Events.Add(new RunEvent
            {
                RunId = third.RunId, Seq = 9, Type = third.Type, Node = third.Node,
                PayloadHash = third.PayloadHash, PayloadRef = third.PayloadRef
            });
            await db.SaveChangesAsync();
        }

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(9, result.FirstBrokenSeq);
        Assert.Equal(ChainStatus.SeqGap, result.Events.Single(e => e.Seq == 9).Status);
    }

    /// <summary>
    /// Replaces the seq-N event of a run with a copy carrying the same PayloadHash, PayloadRef and
    /// untouched payload file, but one edited metadata field — exactly what an attacker with DB
    /// write access does to retype, reattribute or backdate an event while leaving the hash column
    /// and payload alone. The M0 hash (payload-only) would still verify such a row.
    /// </summary>
    private static async Task TamperMetadataAsync(
        AuditTestHarness h, Guid runId, long seq, Action<RunEventDraft> edit)
    {
        await using var db = await h.CreateDbContextAsync();
        var original = await db.Events.SingleAsync(e => e.RunId == runId && e.Seq == seq);
        var draft = new RunEventDraft
        {
            RunId = original.RunId, Seq = original.Seq, Type = original.Type,
            Node = original.Node, Ts = original.Ts, PayloadHash = original.PayloadHash,
            PayloadRef = original.PayloadRef
        };
        edit(draft);

        db.Events.Remove(original);
        await db.SaveChangesAsync();
        db.Events.Add(new RunEvent
        {
            RunId = draft.RunId, Seq = draft.Seq, Type = draft.Type, Node = draft.Node,
            Ts = draft.Ts, PayloadHash = draft.PayloadHash, PayloadRef = draft.PayloadRef
        });
        await db.SaveChangesAsync();
    }

    private sealed class RunEventDraft
    {
        public Guid RunId; public long Seq = 0; public string Type = ""; public string Node = "";
        public DateTimeOffset Ts; public string PayloadHash = ""; public string? PayloadRef;
    }

    [Fact]
    public async Task Retyped_event_is_caught()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        // seq 2 was a model_call; relabel it a node_end. Payload and hash column are untouched.
        await TamperMetadataAsync(h, run.Id, 2, d => d.Type = "node_end");

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(2, result.FirstBrokenSeq);
        Assert.Equal(ChainStatus.HashMismatch, result.Events.Single(e => e.Seq == 2).Status);
        // Localised: only the tampered event breaks; the widened hash still chains the rest.
        Assert.Equal(ChainStatus.Ok, result.Events.Single(e => e.Seq == 3).Status);
        Assert.Equal(2, await h.Emitter.VerifyChainAsync(run.Id, default));
    }

    [Fact]
    public async Task Reattributed_node_is_caught()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        // Blame a different node for the model call.
        await TamperMetadataAsync(h, run.Id, 2, d => d.Node = "post");

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(2, result.FirstBrokenSeq);
        Assert.Equal(ChainStatus.HashMismatch, result.Events.Single(e => e.Seq == 2).Status);
    }

    [Fact]
    public async Task Backdated_event_is_caught()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        // Rewind the clock a day (well beyond the millisecond binding resolution).
        await TamperMetadataAsync(h, run.Id, 2, d => d.Ts = d.Ts.AddDays(-1));

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(2, result.FirstBrokenSeq);
        Assert.Equal(ChainStatus.HashMismatch, result.Events.Single(e => e.Seq == 2).Status);
    }

    /// <summary>
    /// A metadata field cannot be swapped across a field boundary without breaking the chain:
    /// the length prefix on each field makes ("a","bc") and ("ab","c") hash differently, so
    /// shifting a character from Type into Node (leaving the concatenation identical) is caught.
    /// </summary>
    [Fact]
    public async Task Field_boundary_shift_is_caught()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);

        // Emit with Type/Node whose concatenation is stable under the shift below.
        await h.Emitter.EmitAsync(run.Id, "ab", "c", "payload", default);

        await TamperMetadataAsync(h, run.Id, 1, d => { d.Type = "a"; d.Node = "bc"; });

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(1, result.FirstBrokenSeq);
        Assert.Equal(ChainStatus.HashMismatch, result.Events.Single().Status);
    }

    [Fact]
    public async Task Event_without_a_payload_reference_cannot_be_verified()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);

        await using (var db = await h.CreateDbContextAsync())
        {
            db.Events.Add(new RunEvent
            {
                RunId = run.Id, Seq = 1, Type = "node_start", Node = "gather",
                PayloadHash = "DEADBEEF", PayloadRef = null
            });
            await db.SaveChangesAsync();
        }

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(ChainStatus.PayloadRefMissing, result.Events.Single().Status);
    }

    [Fact]
    public async Task Unknown_run_is_not_reported_intact()
    {
        using var h = new AuditTestHarness();

        var result = await h.Verifier.VerifyAsync(Guid.NewGuid());

        Assert.False(result.Intact);
        Assert.Equal(ChainVerificationResult.ChainLevelSeq, result.FirstBrokenSeq);
        Assert.Empty(result.Events);
        Assert.Equal(0, await h.Emitter.VerifyChainAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Wholesale_deletion_is_caught_by_the_anchor()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);   // advances the run's head anchor to seq 3

        // Simulate an attacker with owner privileges (who can bypass the append-only trigger)
        // deleting every event but leaving the anchor untouched. InMemory does not enforce the
        // trigger, which is exactly the scenario we need to reproduce here.
        await using (var db = await h.CreateDbContextAsync())
        {
            db.Events.RemoveRange(await db.Events.Where(e => e.RunId == run.Id).ToListAsync());
            await db.SaveChangesAsync();
        }

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        // Chain ends at seq 0, anchor is 3 → the gap opens at the first missing seq, 1.
        Assert.Equal(1, result.FirstBrokenSeq);
        Assert.Empty(result.Events);
        Assert.Equal(1, await h.Emitter.VerifyChainAsync(run.Id, default));
    }

    [Fact]
    public async Task Tail_truncation_is_caught_by_the_anchor()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);   // anchor at seq 3

        // Delete only the last event. Seqs 1..2 remain a perfectly consistent chain — the loop
        // over surviving events cannot see the gap; only the anchor (still seq 3) reveals it.
        await using (var db = await h.CreateDbContextAsync())
        {
            var last = await db.Events.SingleAsync(e => e.RunId == run.Id && e.Seq == 3);
            db.Events.Remove(last);
            await db.SaveChangesAsync();
        }

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.False(result.Intact);
        Assert.Equal(3, result.FirstBrokenSeq);              // first missing seq
        Assert.All(result.Events, e => Assert.Equal(ChainStatus.Ok, e.Status)); // survivors verify
    }

    [Fact]
    public async Task Clean_run_verifies_with_the_anchor_matching()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        var result = await h.Verifier.VerifyAsync(run.Id);
        Assert.True(result.Intact);

        // The anchor itself must point at the real terminal event.
        await using var db = await h.CreateDbContextAsync();
        var stored = await db.Runs.SingleAsync(r => r.Id == run.Id);
        var lastEvent = await db.Events.SingleAsync(e => e.RunId == run.Id && e.Seq == 3);
        Assert.Equal(3, stored.HeadSeq);
        Assert.Equal(lastEvent.PayloadHash, stored.HeadHash);
    }

    [Fact]
    public async Task Anchor_advances_on_each_emit()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);

        await h.Emitter.EmitAsync(run.Id, "node_start", "gather", "agent", default);
        await AssertAnchorAsync(h, run.Id, 1);
        await h.Emitter.EmitAsync(run.Id, "model_call", "gather", "call", default);
        await AssertAnchorAsync(h, run.Id, 2);
        await h.Emitter.EmitAsync(run.Id, "node_end", "gather", "done", default);
        await AssertAnchorAsync(h, run.Id, 3);
    }

    private static async Task AssertAnchorAsync(AuditTestHarness h, Guid runId, long expectedSeq)
    {
        await using var db = await h.CreateDbContextAsync();
        var run = await db.Runs.SingleAsync(r => r.Id == runId);
        var head = await db.Events.SingleAsync(e => e.RunId == runId && e.Seq == expectedSeq);
        Assert.Equal(expectedSeq, run.HeadSeq);
        Assert.Equal(head.PayloadHash, run.HeadHash);
    }

    /// <summary>
    /// Executable documentation of the STRIDE F4 boundary: the anchor is a mutable in-band column,
    /// so an attacker who truncates the chain AND rewrites the anchor to match the shorter chain
    /// defeats detection. This is tamper-<em>evidence</em>, not tamper-<em>resistance</em>; closing
    /// it needs the least-privilege append-only runtime role (graduation / M4). If this test ever
    /// starts failing, the anchor gained out-of-band protection — update the threat model.
    /// </summary>
    [Fact]
    public async Task Truncation_with_a_matching_rewritten_anchor_is_the_documented_F4_limit()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await EmitThreeAsync(h, run.Id);

        await using (var db = await h.CreateDbContextAsync())
        {
            var last = await db.Events.SingleAsync(e => e.RunId == run.Id && e.Seq == 3);
            var survivor2 = await db.Events.SingleAsync(e => e.RunId == run.Id && e.Seq == 2);
            db.Events.Remove(last);
            var runRow = await db.Runs.SingleAsync(r => r.Id == run.Id);
            runRow.HeadSeq = 2;                       // rewind the anchor to the truncated chain
            runRow.HeadHash = survivor2.PayloadHash;
            await db.SaveChangesAsync();
        }

        // Verifies intact — the anchor and chain agree. The limitation is real and acknowledged.
        Assert.True((await h.Verifier.VerifyAsync(run.Id)).Intact);
    }

    [Fact]
    public async Task Pending_run_with_no_events_is_intact()
    {
        using var h = new AuditTestHarness();
        var run = new Run
        {
            Workflow = "pr-review", WorkflowSha = "test", Initiator = "tests",
            Repo = "deniz2412/test-repo-harness", Status = RunStatus.Pending
        };
        await using (var db = await h.CreateDbContextAsync())
        {
            db.Runs.Add(run);
            await db.SaveChangesAsync();
        }

        var result = await h.Verifier.VerifyAsync(run.Id);

        Assert.True(result.Intact);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Payloads_are_never_overwritten_by_a_later_emit()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);
        await h.Emitter.EmitAsync(run.Id, "node_start", "gather", "agent", default);

        var first = await File.ReadAllTextAsync(h.PayloadFile(run.Id, 1));
        await h.Emitter.EmitAsync(run.Id, "node_end", "gather", "done", default);

        Assert.Equal("agent", first);
        Assert.Equal("agent", await File.ReadAllTextAsync(h.PayloadFile(run.Id, 1)));
        Assert.True(await h.Verifier.VerifyAsync(run.Id) is { Intact: true });
    }
}
