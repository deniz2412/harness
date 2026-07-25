using Harness.Agents;
using Harness.Api.Ops;
using Harness.Audit;
using Harness.Contracts;
using Harness.Engine;
using Harness.Policy;
using Harness.Runner;
using Harness.Tools;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Octokit;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// --- persistence ---
// The password comes from the environment only, so .env stays the single source of truth for
// both this and the postgres container in docker/compose.yaml and the two cannot drift apart.
var connection = new NpgsqlConnectionStringBuilder(cfg.GetConnectionString("Harness"))
{
    Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
        ?? throw new InvalidOperationException(
            "POSTGRES_PASSWORD is not set. Copy .env.example to .env and fill it in.")
}.ConnectionString;
builder.Services.AddDbContextFactory<HarnessDbContext>(o => o.UseNpgsql(connection));

// --- core services ---
var paths = cfg.GetSection("Paths");
// The master key is what stands between any process on this workstation and metered spend at the
// gateway. It was the one credential with a default, so a missing .env silently produced a
// well-known key instead of a startup failure (threat model F5).
var gatewayKey = Environment.GetEnvironmentVariable("GATEWAY_MASTER_KEY");
if (string.IsNullOrWhiteSpace(gatewayKey))
    throw new InvalidOperationException(
        "GATEWAY_MASTER_KEY is not set. Copy .env.example to .env and fill it in.");
builder.Services.AddSingleton(new GatewayOptions
{
    BaseUrl = cfg["Gateway:BaseUrl"]!,
    ApiKey = gatewayKey,
    CheapModel = cfg["Gateway:CheapModel"]!,
    StrongModel = cfg["Gateway:StrongModel"]!
});
// M7c MCP connector layer: the config-declared, allowlisted external toolsets the platform may mount.
// Loaded once, fail-fast on a malformed connectors.yaml (an ABSENT file is fine — it means no
// connectors declared). The registry governs which namespaced operations may be mounted and the
// write-capable boundary; StubMcpConnector stands in for a real MCP client (a documented drop-in
// behind IMcpConnector — no network/subprocess egress in the PoC). Every mounted op is wrapped by
// AuditedTool exactly like a built-in, so it is policed and audited per call.
var connectorRegistry = McpConnectorRegistry.FromFile(paths["Connectors"]!);
var mountedConnectors = connectorRegistry.Connectors.ToDictionary(
    c => c.Namespace,
    c => (IMcpConnector)new StubMcpConnector(c.Namespace, c.Operations),
    // Case-insensitive to match how McpConnectorRegistry/PolicyPipeline compare namespaces, so a name
    // the pre-tool check admits can never then miss the mount and fall through to "unknown tool".
    StringComparer.OrdinalIgnoreCase);
builder.Services.AddSingleton(connectorRegistry);
// Constructing this compiles the embedded secret ruleset and tool catalog: a malformed policy
// layer stops the process here rather than permitting everything at runtime. The connector registry
// makes the pre-tool check govern connector tools too (allowlist + write-capable boundary).
builder.Services.AddSingleton(new PolicyPipeline(SecretScanner.Default, ToolCatalog.Default, connectorRegistry));
builder.Services.AddSingleton(sp => new AuditEmitter(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>(), paths["AuditPayloads"]!));
builder.Services.AddSingleton<IAuditLog>(sp =>
    new AuditEmitterLog(sp.GetRequiredService<AuditEmitter>()));
builder.Services.AddSingleton<IRunStore>(sp => new EfRunStore(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>()));
builder.Services.AddSingleton<IApprovalStore>(sp => new EfApprovalStore(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>(),
    sp.GetRequiredService<AuditEmitter>()));
builder.Services.AddSingleton(sp => new WorkflowLoader(paths["Workflows"]!, paths["Prompts"]!, paths["Agents"]!));

// M7 org policy floor: the ceiling every workflow — org default or team-namespaced — must fit inside
// (allowed tools, a human gate upstream of any gated write, repo allowlist, budget cap). Loaded once,
// fail-fast: a malformed or missing policy.yaml throws here and stops startup, the same posture as the
// secret ruleset and RepoAllowlist. Enforced on the run path (POST /runs and resume) via the validator.
var policyFloor = PolicyFloor.FromFile(paths["Policy"]!);
builder.Services.AddSingleton(policyFloor);
builder.Services.AddSingleton(sp => new PolicyFloorValidator(sp.GetRequiredService<PolicyFloor>()));
// M7 team namespaces: resolves workflows/teams/<team>/<name> → defaults/<name> → flat <name>, so a
// team's same-named file overrides the org default without moving any files (product-vision §4).
builder.Services.AddSingleton(sp => new WorkflowCatalog(paths["Workflows"]!));

// Boot-time fail-closed sweep: hold every workflow the platform ships to the floor. If any shipped
// default or team workflow steps outside the ceiling, the process refuses to start — the violation is
// caught here, not at the first run that happens to trigger it. Mirrors the offline eval guarantee.
{
    var bootLoader = new WorkflowLoader(paths["Workflows"]!, paths["Prompts"]!, paths["Agents"]!);
    var bootValidator = new PolicyFloorValidator(policyFloor);
    foreach (var file in Directory.EnumerateFiles(paths["Workflows"]!, "*.yaml", SearchOption.AllDirectories))
    {
        var name = Path.GetRelativePath(paths["Workflows"]!, file)[..^".yaml".Length].Replace('\\', '/');
        bootValidator.EnsureCompliant(bootLoader.Load(name));   // resolves agent_refs too (M7b)
    }
    // M7b: every named agent must also fit the floor's tool ceiling — a registry agent whose tools
    // exceed the org floor is a fail-fast even if no workflow references it yet.
    var bootAgents = new AgentLoader(paths["Agents"]!, paths["Prompts"]!);
    if (Directory.Exists(paths["Agents"]!))
        foreach (var file in Directory.EnumerateFiles(paths["Agents"]!, "*.yaml", SearchOption.AllDirectories))
        {
            var agent = bootAgents.Load(Path.GetRelativePath(paths["Agents"]!, file)[..^".yaml".Length].Replace('\\', '/'));
            foreach (var tool in agent.Tools)
                if (!policyFloor.AllowsTool(tool))
                    throw new InvalidOperationException(
                        $"Agent '{agent.Name}' names tool '{tool}' outside the org policy floor's allowed_tools ceiling.");
        }
}
// M3 un-bound the tools from a single startup repo: which repo a run targets now comes from the
// request (validated against the allowlist below), so GitHub:Owner/Repo config is no longer the
// source of truth. The token is still required.
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(githubToken))
    throw new InvalidOperationException(
        "GITHUB_TOKEN is not set. Copy .env.example to .env and fill it in.");

builder.Services.AddSingleton<IGitHubClient>(_ => new GitHubClient(new ProductHeaderValue("harness"))
    { Credentials = new Credentials(githubToken) });

// M3: GitHub tooling is per-run, not bound to one startup repo. The factory builds a toolset for
// whatever repo a run targets; the repo allowlist (operator config) is the policy control deciding
// which repos that may be. Fail-closed: an empty/absent allowlist denies every run, so an operator
// who forgets to configure it cannot accidentally run against everything.
var allowlistEntries = cfg.GetSection("RepoAllowlist").Get<string[]>() ?? [];
var repoAllowlist = new RepoAllowlist(allowlistEntries);   // throws at startup on a malformed entry
if (repoAllowlist.Entries.Count == 0)
    throw new InvalidOperationException(
        "RepoAllowlist is empty. List the repos (owner/name, or owner/*) any run may target in "
        + "appsettings.json / configuration — a run against a repo not listed here is refused.");
builder.Services.AddSingleton(repoAllowlist);
builder.Services.AddSingleton(sp => new GitHubToolsetFactory(sp.GetRequiredService<IGitHubClient>()));
builder.Services.AddSingleton(sp => new RepoToolset(paths["Worktrees"]!));
builder.Services.AddSingleton(sp => new ToolRegistry(
    sp.GetRequiredService<GitHubToolsetFactory>(),
    sp.GetRequiredService<RepoAllowlist>().Entries,   // search is confined to the allowlist
    sp.GetRequiredService<RepoToolset>(),
    sp.GetRequiredService<PolicyPipeline>(),
    sp.GetRequiredService<AuditEmitter>(),
    mountedConnectors));                              // M7c: declared external toolsets, mounted per namespace

// The write-path sandbox (M2): a per-run worktree the write nodes act in. Subprocess-backed for
// the local PoC — a container drop-in replaces it behind IRunnerFactory at graduation. The token
// accessor is the same GITHUB_TOKEN the rest of the platform uses; the factory clones into the
// mounted worktrees volume and never logs the token.
builder.Services.AddSingleton<IRunnerFactory>(_ => new SubprocessRunnerFactory(
    tokenAccessor: () => githubToken,
    options: new RunnerOptions { WorktreeRoot = paths["Worktrees"]! }));

// Node-kind executors. `agent` and `agent-loop` reach the model (gateway only); `bash` and `gate`
// do not. All four are registered as INodeExecutor and dispatched by the DAG on node.Kind.
builder.Services.AddSingleton<INodeExecutor>(sp => new AgentNodeExecutor(
    sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<ToolRegistry>(),
    sp.GetRequiredService<PolicyPipeline>(), sp.GetRequiredService<AuditEmitter>(),
    paths["Prompts"]!));
builder.Services.AddSingleton<INodeExecutor>(sp => new AgentLoopNodeExecutor(
    sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<ToolRegistry>(),
    sp.GetRequiredService<PolicyPipeline>(), sp.GetRequiredService<AuditEmitter>(),
    sp.GetRequiredService<IAuditLog>(), paths["Prompts"]!));
builder.Services.AddSingleton<INodeExecutor>(sp => new BashNodeExecutor(
    sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<INodeExecutor>(_ => new GateNodeExecutor());
builder.Services.AddSingleton<DagExecutor>();

// --- operations console (F1) ---
// The read model + the two write actions the console and the HTTP endpoints share. The coordinator
// holds the only copy of the fail-closed run logic (allowlist, workflow-sha), so the UI cannot take
// a shortcut the API doesn't. IWorkflowRunner is the seam that keeps that logic testable off a live
// gateway; in production it forwards to the real DAG executor.
builder.Services.AddSingleton<IRunQueries>(sp => new RunQueries(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>(),
    paths["AuditPayloads"]!, sp.GetRequiredService<AuditEmitter>()));
// F2 workflow catalog read model: browse workflows (parsed from YAML via the production loader, so
// agent_refs are merged and shas stamped as a run would see), render one's DAG, and per-workflow run
// stats. A read-only client seam like IRunQueries.
builder.Services.AddSingleton<IWorkflowCatalogQueries>(sp => new WorkflowCatalogQueries(
    sp.GetRequiredService<WorkflowCatalog>(),
    sp.GetRequiredService<WorkflowLoader>(),
    paths["Workflows"]!,
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>()));
// F3 authoring workbench: validate editor YAML (structure + prompts/agents resolve + curated catalog +
// org policy floor) and dry-run its DAG, without executing or writing anything. Pure/read-only.
builder.Services.AddSingleton<IWorkbenchService>(sp => new WorkbenchService(
    sp.GetRequiredService<WorkflowLoader>(),
    sp.GetRequiredService<PolicyFloorValidator>(),
    sp.GetRequiredService<McpConnectorRegistry>()));
builder.Services.AddSingleton<IWorkflowRunner>(sp => new DagWorkflowRunner(
    sp.GetRequiredService<DagExecutor>()));
builder.Services.AddSingleton<IRunCoordinator>(sp => new RunCoordinator(
    sp.GetRequiredService<WorkflowLoader>(), sp.GetRequiredService<WorkflowCatalog>(),
    sp.GetRequiredService<PolicyFloorValidator>(), sp.GetRequiredService<IWorkflowRunner>(),
    sp.GetRequiredService<IRunStore>(), sp.GetRequiredService<AuditEmitter>(),
    sp.GetRequiredService<PolicyPipeline>(), sp.GetRequiredService<RepoAllowlist>(),
    sp.GetRequiredService<IApprovalStore>(), sp.GetRequiredService<ILoggerFactory>()));

// Blazor Server: the console is served from this same process (one container, loopback-only), a
// client of the services above — it renders the audit trail and performs only the gate decision.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// Schema now comes from EF migrations rather than EnsureCreated, so the audit store has a real
// upgrade path instead of a create-once-and-hope one.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HarnessDbContext>>()
        .CreateDbContext();
    db.Database.Migrate();
}

// --- endpoints ---
// The HTTP API and the ops console both go through IRunCoordinator, so the fail-closed rules
// (repo allowlist, workflow-sha) are enforced on one path. The endpoint just translates the
// coordinator's outcome to an HTTP result.
app.MapPost("/runs", async (RunRequest req, IRunCoordinator coordinator, CancellationToken ct) =>
{
    var outcome = await coordinator.StartAsync(
        req.Workflow, req.Repo, req.Pr, req.Issue, req.Initiator, req.Team, ct);
    return outcome.Status switch
    {
        StartStatus.Started => Results.Accepted(
            $"/runs/{outcome.Run!.Id}", new { outcome.Run.Id, outcome.Run.Status }),
        // RepoNotAllowlisted | BadWorkflow | PolicyFloorBlocked — all caller-fixable bad requests.
        _ => Results.BadRequest(new { error = outcome.Error }),
    };
});

app.MapGet("/runs", async (IRunQueries queries, int? limit, CancellationToken ct) =>
    Results.Ok(await queries.ListRunsAsync(Math.Clamp(limit ?? 50, 1, 500), ct)));

app.MapGet("/runs/{id:guid}", async (Guid id, IRunStore runs, CancellationToken ct) =>
{
    var run = await runs.GetAsync(id, ct);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapGet("/runs/{id:guid}/events", async (Guid id, IDbContextFactory<HarnessDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    return Results.Ok(await db.Events.Where(e => e.RunId == id).OrderBy(e => e.Seq).ToListAsync());
});

app.MapGet("/runs/{id:guid}/verify", async (Guid id, AuditEmitter audit, CancellationToken ct) =>
{
    var result = await audit.VerifyAsync(id, ct);
    return Results.Ok(new
    {
        intact = result.Intact,
        firstBrokenSeq = result.FirstBrokenSeq,
        reason = result.Reason,
        events = result.Events
    });
});

// --- human gates ---
app.MapGet("/runs/{id:guid}/gates", async (Guid id, IDbContextFactory<HarnessDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    return Results.Ok(await db.Approvals.Where(a => a.RunId == id)
        .OrderBy(a => a.RequestedAt).ToListAsync());
});

app.MapPost("/runs/{id:guid}/gates/{node}/decide", async (
    Guid id, string node, GateDecisionRequest body, IRunCoordinator coordinator, CancellationToken ct) =>
{
    var outcome = await coordinator.DecideAsync(
        id, node, body.Approve, body.Approver ?? "", body.Reason, ct);
    return outcome switch
    {
        GateOutcome.Accepted => Results.Accepted($"/runs/{id}",
            new { id, decision = body.Approve ? "Approved" : "Rejected" }),
        GateOutcome.RunNotFound => Results.NotFound(),
        GateOutcome.GateNotFound => Results.NotFound(new { error = $"No gate requested for node '{node}'." }),
        GateOutcome.NotAwaitingApproval => Results.Conflict(new { error = "Run is not awaiting approval." }),
        GateOutcome.AlreadyDecided => Results.Conflict(new { error = "This gate has already been decided." }),
        GateOutcome.RepoNoLongerAllowlisted => Results.Conflict(new
        {
            error = "The run's repository is no longer allowlisted; this run cannot resume."
        }),
        GateOutcome.DefinitionChanged => Results.Conflict(new
        {
            error = "The workflow definition changed while this run was paused; "
                  + "the decision does not carry over. Start a new run.",
        }),
        GateOutcome.PolicyFloorViolation => Results.Conflict(new
        {
            error = "The org policy floor was tightened while this run was paused and the workflow "
                  + "no longer satisfies it; the run cannot resume. Start a new run.",
        }),
        _ => Results.Problem("Unhandled gate outcome."),
    };
});

// The operations console (Blazor Server) is served from the root of this same process — a client of
// the services above, loopback-only like the rest of the API. UseStaticFiles serves the console's
// stylesheet from wwwroot; UseAntiforgery guards its interactive form posts (gate decisions,
// launch); the JSON API endpoints above take no form input.
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<Harness.Api.Components.App>().AddInteractiveServerRenderMode();

app.Run();

public sealed record RunRequest(
    string Workflow, string Repo, int? Pr, int? Issue, string? Initiator, string? Team = null);

public sealed record GateDecisionRequest(bool Approve, string? Approver, string? Reason);
