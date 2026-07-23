using Harness.Agents;
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
// Constructing this compiles the embedded secret ruleset and tool catalog: a malformed policy
// layer stops the process here rather than permitting everything at runtime.
builder.Services.AddSingleton<PolicyPipeline>();
builder.Services.AddSingleton(sp => new AuditEmitter(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>(), paths["AuditPayloads"]!));
builder.Services.AddSingleton<IAuditLog>(sp =>
    new AuditEmitterLog(sp.GetRequiredService<AuditEmitter>()));
builder.Services.AddSingleton<IRunStore>(sp => new EfRunStore(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>()));
builder.Services.AddSingleton<IApprovalStore>(sp => new EfApprovalStore(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>(),
    sp.GetRequiredService<AuditEmitter>()));
builder.Services.AddSingleton(sp => new WorkflowLoader(paths["Workflows"]!, paths["Prompts"]!));
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
    sp.GetRequiredService<AuditEmitter>()));

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

var app = builder.Build();

// Schema now comes from EF migrations rather than EnsureCreated, so the audit store has a real
// upgrade path instead of a create-once-and-hope one.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HarnessDbContext>>()
        .CreateDbContext();
    db.Database.Migrate();
}

// --- run execution ---
// Still fire-and-forget (a real background queue is M2 work), so anything not logged inside the
// task is lost. A failing audit emit must not mask the error that triggered it — that combination
// once produced a run with no events, no logs and no explanation.
static Task StartAsync(Run run, WorkflowDefinition wf, DagExecutor dag, IRunStore runs,
    AuditEmitter audit, PolicyPipeline policy, ILogger log)
{
    _ = Task.Run(async () =>
    {
        async Task TryEmit(string type, string message)
        {
            try { await audit.EmitAsync(run.Id, type, "-", message, CancellationToken.None); }
            catch (Exception ae) { log.LogCritical(ae, "Audit emit failed for run {RunId}", run.Id); }
        }

        async Task FinishAsync(RunStatus status)
        {
            run.Status = status;
            run.FinishedAt = DateTimeOffset.UtcNow;
            try { await runs.SaveAsync(run, CancellationToken.None); }
            catch (Exception se) { log.LogCritical(se, "Could not persist final status for run {RunId}", run.Id); }
        }

        try { await dag.ExecuteAsync(run, wf, CancellationToken.None); }
        catch (PolicyViolationException pex)
        {
            log.LogWarning(pex, "Run {RunId} policy-blocked", run.Id);
            // pex.Message carries only a redaction token, never matched secret material.
            await TryEmit("policy_block", pex.Message);
            await FinishAsync(RunStatus.PolicyBlocked);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Run {RunId} failed", run.Id);
            // An exception message can quote untrusted content it read, so it is scanned like any
            // other payload before being written to the trail.
            await TryEmit("node_end", $"error: {policy.Redact(ex.Message)}");
            await FinishAsync(RunStatus.Failed);
        }
        // The success and pause paths persist their own status inside the executor, so there is no
        // finally block here: it would race the executor and overwrite AwaitingApproval with a
        // stale value.
    });
    return Task.CompletedTask;
}

// --- endpoints ---
app.MapPost("/runs", async (RunRequest req, WorkflowLoader loader, DagExecutor dag,
    IRunStore runs, AuditEmitter audit, PolicyPipeline policy, RepoAllowlist allowlist,
    ILoggerFactory logs, CancellationToken ct) =>
{
    var log = logs.CreateLogger("Harness.Run");

    // Fail-closed, before anything is created or any tool runs: a run may only target a repo the
    // operator has allowlisted (M3's policy control). A non-allowlisted or malformed repo is a bad
    // request, not a server fault, and never reaches the tools.
    if (!allowlist.IsAllowed(req.Repo))
        return Results.BadRequest(new
        {
            error = $"Repository '{req.Repo}' is not allowlisted. Add it to RepoAllowlist to run against it."
        });

    WorkflowDefinition wf;
    try { wf = loader.Load(req.Workflow); }
    catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
    {
        // A workflow that fails validation is a bad request, not a server fault.
        return Results.BadRequest(new { error = ex.Message });
    }

    var run = new Run
    {
        Workflow = req.Workflow,
        WorkflowSha = wf.Sha,          // the definition + prompts this run actually executed
        Initiator = req.Initiator ?? "local",
        Repo = req.Repo, PullRequest = req.Pr, Issue = req.Issue
    };
    await runs.SaveAsync(run, ct);

    await StartAsync(run, wf, dag, runs, audit, policy, log);
    return Results.Accepted($"/runs/{run.Id}", new { run.Id, run.Status });
});

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
    Guid id, string node, GateDecisionRequest body, IApprovalStore approvals, IRunStore runs,
    WorkflowLoader loader, DagExecutor dag, AuditEmitter audit, PolicyPipeline policy,
    RepoAllowlist allowlist, ILoggerFactory logs, CancellationToken ct) =>
{
    var log = logs.CreateLogger("Harness.Run");
    var run = await runs.GetAsync(id, ct);
    if (run is null) return Results.NotFound();
    if (run.Status != RunStatus.AwaitingApproval)
        return Results.Conflict(new { error = $"Run is {run.Status}, not awaiting approval." });

    // Resuming re-runs the DAG (an approval reaches the write tools), so re-check the allowlist: if
    // the repo was removed from config and the service restarted while this run was paused, it must
    // not resume against a now-forbidden repo. Fail-closed on the current policy, not the old one.
    if (!allowlist.IsAllowed(run.Repo))
        return Results.Conflict(new
        {
            error = $"Repository '{run.Repo}' is no longer allowlisted; this run cannot resume."
        });

    var decision = body.Approve ? GateDecision.Approved : GateDecision.Rejected;
    // The approver is whatever the caller claims until there is authentication — recorded as a
    // claim, not as an identity (threat model F1).
    var approver = string.IsNullOrWhiteSpace(body.Approver) ? run.Initiator : body.Approver;

    // Resuming re-executes the DAG, so both decisions run against a freshly loaded definition — and
    // both must be the definition the human acted on. If the workflow or a prompt changed while the
    // run was paused, the decision was made about something else: an approval could execute inserted
    // pre-gate nodes, and a rejection could run a DAG whose gate has moved or gone. Check before
    // recording the decision, so a stale run is refused, not half-run.
    var wf = loader.Load(run.Workflow);
    if (!string.Equals(wf.Sha, run.WorkflowSha, StringComparison.Ordinal))
        return Results.Conflict(new
        {
            error = "The workflow definition changed while this run was paused; "
                  + "the decision does not carry over. Start a new run.",
            decidedSha = run.WorkflowSha, currentSha = wf.Sha
        });

    try { await approvals.DecideAsync(id, node, decision, approver, body.Reason, ct); }
    catch (GateNotFoundException) { return Results.NotFound(new { error = $"No gate requested for node '{node}'." }); }
    catch (GateAlreadyDecidedException e) { return Results.Conflict(new { error = e.Message }); }

    // Either way the executor resumes: an approval proceeds past the gate, a rejection is recorded
    // and the run closed out — both through the executor's own audited path.
    await StartAsync(run, wf, dag, runs, audit, policy, log);
    return Results.Accepted($"/runs/{run.Id}", new { run.Id, decision = decision.ToString() });
});

app.Run();

public sealed record RunRequest(string Workflow, string Repo, int? Pr, int? Issue, string? Initiator);

public sealed record GateDecisionRequest(bool Approve, string? Approver, string? Reason);
