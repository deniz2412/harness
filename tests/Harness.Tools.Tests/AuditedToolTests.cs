using Harness.Audit;
using Harness.Contracts;
using Harness.Policy;
using Harness.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Octokit;
using Xunit;

namespace Harness.Tools.Tests;

/// <summary>
/// The tool-call seam end to end: a real catalog tool wrapped by <see cref="ToolRegistry"/>, invoked
/// the way the agent's function loop would invoke it. These prove the three guarantees the M0 run
/// lacked — a platform failure fails the run, a tool call is audited before and after, and an
/// ordinary tool error still reaches the model instead of halting the run.
/// </summary>
public sealed class AuditedToolTests : IDisposable
{
    private static readonly InMemoryDatabaseRoot Root = new();
    private readonly DbContextOptions<HarnessDbContext> _options;
    private readonly string _worktree;
    private readonly string _payloadDir;
    private readonly AuditEmitter _audit;
    private readonly ToolRegistry _tools;

    public AuditedToolTests()
    {
        _options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), Root).Options;
        var factory = new Factory(_options);

        _worktree = Path.Combine(Path.GetTempPath(), "harness-tools-tests", Guid.NewGuid().ToString("N"));
        _payloadDir = Path.Combine(_worktree, "_payloads");
        Directory.CreateDirectory(_worktree);
        Directory.CreateDirectory(_payloadDir);

        _audit = new AuditEmitter(factory, _payloadDir);
        // A GitHub client that is never called: this suite only exercises repo.read. The catalog
        // requires the toolset to exist, not to reach the network.
        var githubFactory = new GitHubToolsetFactory(new GitHubClient(new ProductHeaderValue("tools-tests")));
        _tools = new ToolRegistry(githubFactory, ["o/r"], new RepoToolset(_worktree), new PolicyPipeline(), _audit);
    }

    private static ToolCallContext ReviewCtx() =>
        new(Guid.NewGuid(), "review", ["repo.read"],
            new Dictionary<string, string> { ["repo"] = "read" });

    private AIFunction ResolveRepoRead(ToolCallContext ctx) =>
        (AIFunction)_tools.Resolve(ctx).Single();

    private List<RunEvent> Events(Guid runId)
    {
        using var db = new HarnessDbContext(_options);
        return db.Events.Where(e => e.RunId == runId).OrderBy(e => e.Seq).ToList();
    }

    [Fact]
    public async Task Policy_block_during_invoke_latches_and_survives_being_swallowed()
    {
        var ctx = ReviewCtx();
        var tool = ResolveRepoRead(ctx);

        // A secret in a tool argument is caught by the pre-tool scan. The value never reaches the
        // filesystem — the block fires before the tool runs.
        var args = new AIFunctionArguments(new Dictionary<string, object>
        {
            ["path"] = "-----BEGIN RSA PRIVATE KEY-----\nMIIticontent\n-----END RSA PRIVATE KEY-----"
        });

        await Assert.ThrowsAsync<PolicyViolationException>(() => tool.InvokeAsync(args).AsTask());

        // The failure is latched, so even if the agent's loop had swallowed the throw above, the
        // executor's ThrowIfLatched still fails the node.
        Assert.IsType<PolicyViolationException>(ctx.Latched);
        Assert.Throws<PolicyViolationException>(() => ctx.ThrowIfLatched());

        // Fail-closed also means the blocked write left no tool_result claiming it happened.
        Assert.DoesNotContain(Events(ctx.RunId), e => e.Type == "tool_result");
    }

    [Fact]
    public async Task Clean_call_audits_before_and_after_and_does_not_latch()
    {
        await File.WriteAllTextAsync(Path.Combine(_worktree, "note.txt"), "ordinary file contents");

        var ctx = ReviewCtx();
        var tool = ResolveRepoRead(ctx);
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object> { ["path"] = "note.txt" }));

        // The function loop serialises results, so the returned value may be a JsonElement rather
        // than a raw string; assert on its content, not its CLR type.
        Assert.Contains("ordinary file contents", result?.ToString());
        Assert.Null(ctx.Latched);

        // tool_call before, tool_result after — the invariant-5 record the M0 run never produced.
        var events = Events(ctx.RunId);
        Assert.Equal(["tool_call", "tool_result"], events.Select(e => e.Type));
    }

    [Fact]
    public async Task Ordinary_tool_error_propagates_to_the_model_and_does_not_latch()
    {
        var ctx = ReviewCtx();
        var tool = ResolveRepoRead(ctx);

        // A missing file is the world being messy, not a broken guarantee: it must surface to the
        // agent (which may try another path), not latch and kill the run.
        await Assert.ThrowsAnyAsync<Exception>(() => tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object> { ["path"] = "does-not-exist.cs" })).AsTask());

        Assert.Null(ctx.Latched);
        // The attempt is still on the record (tool_call emitted before the call), but there is no
        // tool_result, because the call did not produce one.
        Assert.Equal(["tool_call"], Events(ctx.RunId).Select(e => e.Type));
    }

    private sealed class Factory(DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
