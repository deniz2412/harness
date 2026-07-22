using Harness.Contracts;
using Xunit;

namespace Harness.Audit.Tests;

/// <summary>
/// <c>ReadNodeOutputsAsync</c> is what makes a run resumable and reviewable: the engine writes each
/// node's real output into its <c>node_end</c> payload, and reads it back from here.
/// </summary>
public sealed class NodeOutputTests
{
    private static async Task<Run> SeedRunAsync(AuditTestHarness h)
    {
        var run = new Run
        {
            Workflow = "pr-review", WorkflowSha = "test", Initiator = "tests",
            Repo = "deniz2412/test-repo-harness", Status = RunStatus.Running
        };
        await using var db = await h.CreateDbContextAsync();
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    [Fact]
    public async Task Returns_what_each_node_emitted()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);

        await h.Emitter.EmitAsync(run.Id, "node_start", "gather", "agent", default);
        await h.Emitter.EmitAsync(run.Id, "node_end", "gather", "diff: 4 files", default);
        await h.Emitter.EmitAsync(run.Id, "node_start", "review", "agent", default);
        await h.Emitter.EmitAsync(run.Id, "node_end", "review", """{"findings":[{"severity":"high"}]}""", default);

        var outputs = await h.Emitter.ReadNodeOutputsAsync(run.Id, default);

        Assert.Equal(2, outputs.Count);
        Assert.Equal("diff: 4 files", outputs["gather"]);
        Assert.Equal("""{"findings":[{"severity":"high"}]}""", outputs["review"]);
    }

    [Fact]
    public async Task Latest_node_end_wins()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);

        await h.Emitter.EmitAsync(run.Id, "node_end", "implement", "attempt 1", default);
        await h.Emitter.EmitAsync(run.Id, "node_end", "implement", "attempt 2", default);

        var outputs = await h.Emitter.ReadNodeOutputsAsync(run.Id, default);

        Assert.Equal("attempt 2", Assert.Contains("implement", outputs));
    }

    [Fact]
    public async Task Only_node_end_events_count_and_run_level_events_are_ignored()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);

        await h.Emitter.EmitAsync(run.Id, "model_call", "gather", "prompt", default);
        await h.Emitter.EmitAsync(run.Id, "node_end", AuditEmitter.NoNode, "error: boom", default);

        Assert.Empty(await h.Emitter.ReadNodeOutputsAsync(run.Id, default));
    }

    [Fact]
    public async Task A_node_whose_payload_is_missing_is_omitted_not_reported_empty()
    {
        using var h = new AuditTestHarness();
        var run = await SeedRunAsync(h);

        await h.Emitter.EmitAsync(run.Id, "node_end", "gather", "diff: 4 files", default);
        File.Delete(h.PayloadFile(run.Id, 1));

        var outputs = await h.Emitter.ReadNodeOutputsAsync(run.Id, default);

        // Absent, so a resuming engine re-runs the node; an empty string would be fed onward as
        // if the node had genuinely produced nothing.
        Assert.DoesNotContain("gather", outputs);
    }
}
