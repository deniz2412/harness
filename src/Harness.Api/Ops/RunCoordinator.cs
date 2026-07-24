using Harness.Audit;
using Harness.Contracts;
using Harness.Engine;
using Harness.Policy;
using Microsoft.Extensions.Logging;

namespace Harness.Api.Ops;

/// <summary>
/// Runs a loaded workflow to completion (or a pause) in the background. The seam that lets the
/// coordinator's fail-closed logic be tested without a live gateway: <see cref="DagExecutor"/> is
/// concrete and reaches a model, so the coordinator depends on this narrow interface instead, and
/// <see cref="DagWorkflowRunner"/> is the production adapter over the real executor.
/// </summary>
public interface IWorkflowRunner
{
    Task ExecuteAsync(Run run, WorkflowDefinition wf, CancellationToken ct);
}

/// <summary>Production <see cref="IWorkflowRunner"/> — forwards straight to the DAG executor.</summary>
public sealed class DagWorkflowRunner(DagExecutor dag) : IWorkflowRunner
{
    public Task ExecuteAsync(Run run, WorkflowDefinition wf, CancellationToken ct) =>
        dag.ExecuteAsync(run, wf, ct);
}

/// <summary>
/// The two write actions the operations console performs — start a run and decide a human gate —
/// with every fail-closed guard the HTTP endpoints applied, in the same order, so the endpoints and
/// the UI share one enforcement path. This class holds the only copy of the run-execution error
/// handling (<see cref="StartExecution"/>); both actions kick execution off through it.
/// </summary>
public sealed class RunCoordinator(
    WorkflowLoader loader,
    WorkflowCatalog catalog,
    PolicyFloorValidator floor,
    IWorkflowRunner runner,
    IRunStore runs,
    AuditEmitter audit,
    PolicyPipeline policy,
    RepoAllowlist allowlist,
    IApprovalStore approvals,
    ILoggerFactory loggerFactory) : IRunCoordinator
{
    private readonly ILogger _log = loggerFactory.CreateLogger("Harness.Run");

    public async Task<StartOutcome> StartAsync(
        string workflow, string repo, int? pr, int? issue, string? initiator, string? team = null,
        CancellationToken ct = default)
    {
        // Fail-closed, before anything is created or any tool runs: a run may only target a repo the
        // operator has allowlisted (M3's policy control). A non-allowlisted or malformed repo is
        // refused and never reaches the tools.
        if (!allowlist.IsAllowed(repo))
            return new StartOutcome(StartStatus.RepoNotAllowlisted,
                Error: $"Repository '{repo}' is not allowlisted. Add it to RepoAllowlist to run against it.");

        // M7 team namespaces: resolve (workflow, team) to the concrete definition — a same-named team
        // file under workflows/teams/<team>/ overrides the org default. The RESOLVED name is what the
        // run stores and pins its sha to, so a resume re-loads the identical file (the override picked
        // at start is not re-decided). `team` is a caller claim until there is auth (threat model F1),
        // exactly like `initiator`.
        string resolved;
        WorkflowDefinition wf;
        try
        {
            resolved = catalog.ResolveName(workflow, team);
            wf = loader.Load(resolved);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or ArgumentException)
        {
            // A workflow that will not resolve or fails validation is a bad request, not a server fault.
            return new StartOutcome(StartStatus.BadWorkflow, Error: ex.Message);
        }

        // M7 org policy floor: the resolved workflow must fit inside the org ceiling (allowed tools,
        // a human gate upstream of any gated write). Fail-closed BEFORE the run is created — a
        // workflow that steps outside the floor never starts.
        try
        {
            floor.EnsureCompliant(wf);
        }
        catch (PolicyFloorException pfx)
        {
            return new StartOutcome(StartStatus.PolicyFloorBlocked, Error: pfx.Message);
        }

        var run = new Run
        {
            Workflow = resolved,          // the resolved (possibly team-namespaced) definition name
            WorkflowSha = wf.Sha,          // the definition + prompts this run actually executed
            Initiator = initiator ?? "local",
            Repo = repo, PullRequest = pr, Issue = issue
        };
        await runs.SaveAsync(run, ct);

        StartExecution(run, wf);
        return new StartOutcome(StartStatus.Started, run);
    }

    public async Task<GateOutcome> DecideAsync(
        Guid runId, string node, bool approve, string approver, string? reason,
        CancellationToken ct = default)
    {
        var run = await runs.GetAsync(runId, ct);
        if (run is null) return GateOutcome.RunNotFound;
        if (run.Status != RunStatus.AwaitingApproval) return GateOutcome.NotAwaitingApproval;

        // Resuming re-runs the DAG (an approval reaches the write tools), so re-check the allowlist:
        // if the repo was removed from config while this run was paused, it must not resume against a
        // now-forbidden repo. Fail-closed on the current policy, not the old one.
        if (!allowlist.IsAllowed(run.Repo)) return GateOutcome.RepoNoLongerAllowlisted;

        var decision = approve ? GateDecision.Approved : GateDecision.Rejected;
        // The approver is whatever the caller claims until there is authentication — recorded as a
        // claim, not an identity (threat model F1). Blank defaults to the run's initiator.
        var effectiveApprover = string.IsNullOrWhiteSpace(approver) ? run.Initiator : approver;

        // Resuming re-executes the DAG against a freshly loaded definition, and it must be the
        // definition the human acted on. If the workflow or a prompt changed while the run was
        // paused, the decision was made about something else — refuse before recording it, so a
        // stale run is refused, not half-run.
        WorkflowDefinition wf;
        try
        {
            wf = loader.Load(run.Workflow);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            // The definition this run paused on is gone or no longer valid — e.g. a team removed its
            // namespaced override file while the run was paused. Treat it as a definition change: the
            // decision does not carry over. Fail-closed — no resume, no write, nothing recorded.
            return GateOutcome.DefinitionChanged;
        }
        if (!string.Equals(wf.Sha, run.WorkflowSha, StringComparison.Ordinal))
            return GateOutcome.DefinitionChanged;

        // Resuming re-executes the DAG, so re-check the org policy floor against the CURRENT policy —
        // if the floor was tightened while the run was paused (the workflow-sha is unchanged, so the
        // sha guard above does not catch it), a now-non-compliant workflow must not resume.
        try
        {
            floor.EnsureCompliant(wf);
        }
        catch (PolicyFloorException)
        {
            return GateOutcome.PolicyFloorViolation;
        }

        try
        {
            await approvals.DecideAsync(runId, node, decision, effectiveApprover, reason, ct);
        }
        catch (GateNotFoundException)
        {
            return GateOutcome.GateNotFound;
        }
        catch (GateAlreadyDecidedException)
        {
            return GateOutcome.AlreadyDecided;
        }

        // Either way the executor resumes: an approval proceeds past the gate, a rejection is
        // recorded and the run closed out — both through the executor's own audited path.
        StartExecution(run, wf);
        return GateOutcome.Accepted;
    }

    /// <summary>
    /// The single copy of the run-execution body (was Program.cs's <c>StartAsync</c> local). Still
    /// fire-and-forget — a real background queue is M2 work — so anything not logged inside the task
    /// is lost, and a failing audit emit must not mask the error that triggered it. The success and
    /// pause paths persist their own status inside the executor, so there is deliberately no finally
    /// block: it would race the executor and overwrite AwaitingApproval with a stale value.
    /// </summary>
    private void StartExecution(Run run, WorkflowDefinition wf)
    {
        _ = Task.Run(async () =>
        {
            async Task TryEmit(string type, string message)
            {
                try { await audit.EmitAsync(run.Id, type, "-", message, CancellationToken.None); }
                catch (Exception ae) { _log.LogCritical(ae, "Audit emit failed for run {RunId}", run.Id); }
            }

            async Task FinishAsync(RunStatus status)
            {
                run.Status = status;
                run.FinishedAt = DateTimeOffset.UtcNow;
                try { await runs.SaveAsync(run, CancellationToken.None); }
                catch (Exception se) { _log.LogCritical(se, "Could not persist final status for run {RunId}", run.Id); }
            }

            try
            {
                await runner.ExecuteAsync(run, wf, CancellationToken.None);
            }
            catch (PolicyViolationException pex)
            {
                _log.LogWarning(pex, "Run {RunId} policy-blocked", run.Id);
                // pex.Message carries only a redaction token, never matched secret material.
                await TryEmit("policy_block", pex.Message);
                await FinishAsync(RunStatus.PolicyBlocked);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Run {RunId} failed", run.Id);
                // An exception message can quote untrusted content it read, so it is scanned like any
                // other payload before being written to the trail.
                await TryEmit("node_end", $"error: {policy.Redact(ex.Message)}");
                await FinishAsync(RunStatus.Failed);
            }
        });
    }
}
