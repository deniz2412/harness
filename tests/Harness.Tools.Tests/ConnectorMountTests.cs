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
/// M7c — a declared MCP connector operation mounts through the SAME governed seam as a built-in tool:
/// it is resolved into an <see cref="AuditedTool"/> (so every call is policy-scanned and audited), the
/// connector allowlist and the write-capable boundary gate it fail-closed, and the in-process stub
/// transport returns its (untrusted) text like a real server would. No gateway/model involved.
/// </summary>
public sealed class ConnectorMountTests : IDisposable
{
    private static readonly InMemoryDatabaseRoot Root = new();
    private readonly DbContextOptions<HarnessDbContext> _options;
    private readonly string _worktree;
    private readonly ToolRegistry _tools;

    public ConnectorMountTests()
    {
        _options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), Root).Options;
        var audit = new AuditEmitter(new Factory(_options), NewTempDir());
        _worktree = NewTempDir();

        // A declared, read-only 'docs' connector serving only 'search'.
        var registry = McpConnectorRegistry.FromYaml(
            "connectors:\n  - namespace: docs\n    write_capable: false\n    operations: [search]\n");
        var policy = new PolicyPipeline(SecretScanner.Default, ToolCatalog.Default, registry);
        var connectors = new Dictionary<string, IMcpConnector>(StringComparer.Ordinal)
        {
            ["docs"] = new StubMcpConnector("docs", new[] { "search" }),
        };

        var githubFactory = new GitHubToolsetFactory(new GitHubClient(new ProductHeaderValue("connector-tests")));
        _tools = new ToolRegistry(githubFactory, ["o/r"], new RepoToolset(_worktree), policy, audit, connectors);
    }

    private ToolCallContext ReadCtx(params string[] tools) =>
        new(Guid.NewGuid(), "gather", tools, new Dictionary<string, string> { ["repo"] = "read" });

    private ToolCallContext WriteCtx(params string[] tools) =>
        new(Guid.NewGuid(), "implement", tools,
            new Dictionary<string, string> { ["repo"] = "write-worktree", ["github"] = "open_pr+issues" });

    private List<RunEvent> Events(Guid runId)
    {
        using var db = new HarnessDbContext(_options);
        return db.Events.Where(e => e.RunId == runId).OrderBy(e => e.Seq).ToList();
    }

    [Fact]
    public async Task A_declared_connector_op_mounts_as_an_audited_tool_and_invokes_the_transport()
    {
        var ctx = ReadCtx("repo.read", "docs.search");
        var tool = (AIFunction)_tools.Resolve(ctx).Single(t => ((AIFunction)t).Name == "docs_search");

        var result = await tool.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object> { ["argumentsJson"] = "{\"q\":\"bulk discount\"}" }));

        // The stub transport answered, and it came back through the audited seam.
        Assert.Contains("[stub:docs.search]", result?.ToString());
        Assert.Null(ctx.Latched);
        // Invariant 5 / §5a: a mounted op is logged per call exactly like a built-in.
        Assert.Equal(["tool_call", "tool_result"], Events(ctx.RunId).Select(e => e.Type));
    }

    [Fact]
    public void A_read_only_connector_is_refused_on_a_write_capable_node()
    {
        // The write-capable boundary: docs (write_capable: false) must not attach to a write ceiling.
        Assert.Throws<PolicyViolationException>(() => _tools.Resolve(WriteCtx("docs.search")));
    }

    [Fact]
    public void An_un_allowlisted_connector_operation_is_refused()
    {
        // 'delete' is not in the docs connector's operation allowlist.
        Assert.Throws<PolicyViolationException>(() => _tools.Resolve(ReadCtx("docs.delete")));
    }

    [Fact]
    public void An_undeclared_connector_namespace_is_refused()
    {
        // 'jira' is not a declared connector → falls through to the curated catalog → not a built-in.
        Assert.Throws<PolicyViolationException>(() => _tools.Resolve(ReadCtx("jira.create")));
    }

    private sealed class Factory(DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harness-connector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
    }
}
