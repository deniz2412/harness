using Harness.Contracts;

namespace Harness.Api.Ops;

/// <summary>
/// The read model behind the operations console (F1): everything the UI shows is a query over the
/// runs / run_events the platform has recorded since M0. The console is a client of this and of the
/// audit trail — it can render nothing the trail cannot account for.
/// </summary>
public interface IRunQueries
{
    /// <summary>Most-recent runs first, capped at <paramref name="limit"/>, with a per-run event
    /// count and total recorded cost — the run-list view.</summary>
    Task<IReadOnlyList<RunListItem>> ListRunsAsync(int limit, CancellationToken ct = default);

    /// <summary>A run with its ordered events, its gates, and aggregated token/cost totals — the
    /// run-detail view. Null if the run is unknown.</summary>
    Task<RunView?> GetRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>The full payload text of one event (read from the audit volume) for the event/audit
    /// viewer. Null if the run/seq is unknown or the payload file is missing.</summary>
    Task<string?> GetEventPayloadAsync(Guid runId, long seq, CancellationToken ct = default);
}

/// <summary>One row of the run list.</summary>
public sealed record RunListItem(
    Guid Id, string Workflow, string Repo, string Initiator, RunStatus Status,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int EventCount, decimal CostUsd);

/// <summary>A run and everything the detail view renders about it.</summary>
public sealed record RunView(
    Run Run,
    IReadOnlyList<RunEvent> Events,
    IReadOnlyList<GateApproval> Gates,
    int TokensIn, int TokensOut, decimal CostUsd);

/// <summary>
/// The two actions the console performs — the only writes a client makes: start a run and decide a
/// human gate. Both centralise the fail-closed rules (repo allowlist, workflow-sha stability) that
/// the HTTP endpoints and the UI must apply identically, so there is one enforcement path, not two.
/// </summary>
public interface IRunCoordinator
{
    /// <summary>Creates a run and starts it in the background. Fail-closed: the repo must be
    /// allowlisted, the workflow must load. Returns the created run, or a reason it was refused.</summary>
    Task<StartOutcome> StartAsync(
        string workflow, string repo, int? pr, int? issue, string? initiator, CancellationToken ct = default);

    /// <summary>Records a human gate decision and resumes the run. Fail-closed: the run must be
    /// awaiting approval, still target an allowlisted repo, and match the workflow-sha it paused
    /// on.</summary>
    Task<GateOutcome> DecideAsync(
        Guid runId, string node, bool approve, string approver, string? reason, CancellationToken ct = default);
}

/// <summary>Result of <see cref="IRunCoordinator.StartAsync"/>.</summary>
public sealed record StartOutcome(StartStatus Status, Run? Run = null, string? Error = null);

public enum StartStatus { Started, RepoNotAllowlisted, BadWorkflow }

/// <summary>Result of <see cref="IRunCoordinator.DecideAsync"/> — maps to HTTP status and to a UI
/// message. Kept as an enum so both callers translate one closed set of outcomes.</summary>
public enum GateOutcome
{
    Accepted,
    RunNotFound,
    NotAwaitingApproval,
    RepoNoLongerAllowlisted,
    DefinitionChanged,
    GateNotFound,
    AlreadyDecided,
}
