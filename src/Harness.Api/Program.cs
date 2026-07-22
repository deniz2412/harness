using Harness.Agents;
using Harness.Audit;
using Harness.Contracts;
using Harness.Engine;
using Harness.Policy;
using Harness.Tools;
using Microsoft.EntityFrameworkCore;
using Octokit;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// --- persistence ---
builder.Services.AddDbContextFactory<HarnessDbContext>(o =>
    o.UseNpgsql(cfg.GetConnectionString("Harness")));

// --- core services ---
var paths = cfg.GetSection("Paths");
builder.Services.AddSingleton(new GatewayOptions
{
    BaseUrl = cfg["Gateway:BaseUrl"]!,
    ApiKey = Environment.GetEnvironmentVariable("GATEWAY_MASTER_KEY") ?? "local-dev-master",
    CheapModel = cfg["Gateway:CheapModel"]!,
    StrongModel = cfg["Gateway:StrongModel"]!
});
builder.Services.AddSingleton<PolicyPipeline>();
builder.Services.AddSingleton(sp => new AuditEmitter(
    sp.GetRequiredService<IDbContextFactory<HarnessDbContext>>(), paths["AuditPayloads"]!));
builder.Services.AddSingleton(sp => new WorkflowLoader(paths["Workflows"]!));
builder.Services.AddSingleton<IGitHubClient>(_ => new GitHubClient(new ProductHeaderValue("harness"))
    { Credentials = new Credentials(Environment.GetEnvironmentVariable("GITHUB_TOKEN")) });
builder.Services.AddSingleton(sp => new GitHubToolset(
    sp.GetRequiredService<IGitHubClient>(), cfg["GitHub:Owner"]!, cfg["GitHub:Repo"]!));
builder.Services.AddSingleton(sp => new RepoToolset(paths["Worktrees"]!));
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<INodeExecutor>(sp => new AgentNodeExecutor(
    sp.GetRequiredService<GatewayOptions>(), sp.GetRequiredService<ToolRegistry>(),
    sp.GetRequiredService<PolicyPipeline>(), sp.GetRequiredService<AuditEmitter>(),
    paths["Prompts"]!));
builder.Services.AddSingleton<DagExecutor>();

var app = builder.Build();

// dev-only: create schema on boot (real migrations at M1)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HarnessDbContext>>()
        .CreateDbContext();
    db.Database.EnsureCreated();
}

// --- endpoints ---
app.MapPost("/runs", async (RunRequest req, WorkflowLoader loader, DagExecutor dag,
    IDbContextFactory<HarnessDbContext> dbf, AuditEmitter audit, CancellationToken ct) =>
{
    var wf = loader.Load(req.Workflow);
    var run = new Run
    {
        Workflow = req.Workflow, WorkflowSha = "dev",   // M1: read from git
        Initiator = req.Initiator ?? "local",
        Repo = req.Repo, PullRequest = req.Pr, Issue = req.Issue
    };
    await using (var db = await dbf.CreateDbContextAsync(ct)) { db.Runs.Add(run); await db.SaveChangesAsync(ct); }

    // M0: fire-and-forget; M1 moves this to a proper background queue
    _ = Task.Run(async () =>
    {
        try { await dag.ExecuteAsync(run, wf, CancellationToken.None); }
        catch (PolicyViolationException pex)
        {
            run.Status = RunStatus.PolicyBlocked;
            await audit.EmitAsync(run.Id, "policy_block", "-", pex.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            await audit.EmitAsync(run.Id, "node_end", "-", $"error: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            await using var db = await dbf.CreateDbContextAsync();
            db.Runs.Update(run); await db.SaveChangesAsync();
        }
    });

    return Results.Accepted($"/runs/{run.Id}", new { run.Id, run.Status });
});

app.MapGet("/runs/{id:guid}", async (Guid id, IDbContextFactory<HarnessDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    var run = await db.Runs.FindAsync(id);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapGet("/runs/{id:guid}/events", async (Guid id, IDbContextFactory<HarnessDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    return Results.Ok(await db.Events.Where(e => e.RunId == id).OrderBy(e => e.Seq).ToListAsync());
});

app.MapGet("/runs/{id:guid}/verify", async (Guid id, AuditEmitter audit, CancellationToken ct) =>
{
    var broken = await audit.VerifyChainAsync(id, ct);
    return Results.Ok(new { intact = broken is null, firstBrokenSeq = broken });
});

app.Run();

public sealed record RunRequest(string Workflow, string Repo, int? Pr, int? Issue, string? Initiator);
