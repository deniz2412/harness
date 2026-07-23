using Harness.Audit;
using Harness.Contracts;
using Harness.Policy;
using Harness.Tools;
using Harness.Tools.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Octokit;
using Xunit;

namespace Harness.Tools.Tests;

/// <summary>
/// Read tools must act on the run's OWN worktree when the run has one (a write workflow), and on the
/// shared root only when it does not (a read-only workflow). Review finding M2-1 was that only the
/// write tool was scoped to the per-run clone, so in a write workflow the agent read the wrong tree
/// (files at <c>{root}/{runid}/src/…</c>, not <c>{root}/src/…</c>) and <c>list</c>/<c>search</c> saw
/// every concurrent run. These pin the fix.
/// </summary>
public sealed class WorktreeScopingTests : IDisposable
{
    private static readonly InMemoryDatabaseRoot Root = new();
    private readonly string _shared;      // the singleton RepoToolset root (shared /data/worktrees)
    private readonly string _perRun;      // one run's isolated clone under it
    private readonly ToolRegistry _tools;

    public WorktreeScopingTests()
    {
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), Root).Options;
        var payloads = NewTempDir();
        var audit = new AuditEmitter(new Factory(options), payloads);

        _shared = NewTempDir();
        _perRun = Path.Combine(_shared, Guid.NewGuid().ToString("N"));  // a per-run clone lives under the root
        Directory.CreateDirectory(_perRun);
        File.WriteAllText(Path.Combine(_shared, "shared-only.txt"), "in the shared root");
        File.WriteAllText(Path.Combine(_perRun, "worktree-only.txt"), "in this run's worktree");

        var github = new GitHubToolset(new GitHubClient(new ProductHeaderValue("scoping-tests")), "o", "r");
        _tools = new ToolRegistry(github, new RepoToolset(_shared), new PolicyPipeline(), audit);
    }

    private AIFunction Resolve(string tool, ToolCallContext ctx) =>
        (AIFunction)_tools.Resolve(ctx).Single(t => ((AIFunction)t).Name.Contains(tool));

    private ToolCallContext WriteCtx(params string[] tools) =>
        new(Guid.NewGuid(), "implement", tools,
            new Dictionary<string, string> { ["repo"] = "write-worktree", ["github"] = "open_pr+issues" },
            FakeRunnerSession.AlwaysOk(_perRun));

    private ToolCallContext ReadOnlyCtx(params string[] tools) =>
        new(Guid.NewGuid(), "gather", tools,
            new Dictionary<string, string> { ["repo"] = "read" });   // no runner

    [Fact]
    public async Task Read_tool_with_a_runner_reads_the_runs_own_worktree()
    {
        var read = Resolve("repo_read_file", WriteCtx("repo.read"));
        var result = await read.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object> { ["path"] = "worktree-only.txt" }));
        Assert.Contains("in this run's worktree", result?.ToString());
    }

    [Fact]
    public async Task Read_tool_with_a_runner_does_not_see_the_shared_root()
    {
        // shared-only.txt is one level up from the per-run worktree — it must be unreachable, both
        // because it isn't this run's file and because the traversal guard bounds reads to the tree.
        var read = Resolve("repo_read_file", WriteCtx("repo.read"));
        await Assert.ThrowsAnyAsync<Exception>(() => read.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object> { ["path"] = "shared-only.txt" })).AsTask());
    }

    [Fact]
    public async Task List_with_a_runner_sees_only_this_run_not_sibling_worktrees()
    {
        // A second run's worktree sits beside this one under the shared root; list must not surface it.
        var sibling = Path.Combine(_shared, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "other-run-secret.txt"), "another run's data");

        var list = Resolve("repo_list_files", WriteCtx("repo.list"));
        var listing = (await list.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object> { ["dir"] = "." })))?.ToString();

        Assert.Contains("worktree-only.txt", listing);
        Assert.DoesNotContain("other-run-secret.txt", listing);
    }

    [Fact]
    public async Task Read_only_workflow_still_uses_the_shared_root()
    {
        // pr-review has no runner: reads must keep resolving against the singleton root (unchanged).
        var read = Resolve("repo_read_file", ReadOnlyCtx("repo.read"));
        var result = await read.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object> { ["path"] = "shared-only.txt" }));
        Assert.Contains("in the shared root", result?.ToString());
    }

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "harness-scoping-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private sealed class Factory(DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_shared, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
