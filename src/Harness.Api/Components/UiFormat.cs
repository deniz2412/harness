using System.Globalization;
using Harness.Contracts;

namespace Harness.Api.Components;

/// <summary>
/// Pure presentation helpers for the operations console. Kept out of the .razor files so the
/// non-trivial formatting (durations, status → css class / label) is unit-testable without a
/// renderer. Nothing here reaches a service; it only shapes values the seam already returned.
/// </summary>
public static class UiFormat
{
    /// <summary>A stable css-class suffix per status, used as <c>badge badge-{class}</c>. Runs that
    /// need a human (<see cref="RunStatus.AwaitingApproval"/>) and terminal-bad states get their own
    /// classes so an operator can scan the list by colour.</summary>
    public static string StatusClass(RunStatus status) => status switch
    {
        RunStatus.Completed => "ok",
        RunStatus.Running => "running",
        RunStatus.Pending => "pending",
        RunStatus.AwaitingApproval => "await",
        RunStatus.PolicyBlocked => "blocked",
        RunStatus.Rejected => "rejected",
        RunStatus.Failed => "failed",
        _ => "pending"
    };

    /// <summary>Human label for a status (spaces the enum's PascalCase words).</summary>
    public static string StatusLabel(RunStatus status) => status switch
    {
        RunStatus.AwaitingApproval => "Awaiting approval",
        RunStatus.PolicyBlocked => "Policy blocked",
        _ => status.ToString()
    };

    /// <summary>True when a run is still moving (or waiting on a human) — the pages that show such a
    /// run keep a refresh timer running; a terminal run does not need one.</summary>
    public static bool IsLive(RunStatus status) =>
        status is RunStatus.Pending or RunStatus.Running or RunStatus.AwaitingApproval;

    /// <summary>
    /// Elapsed time between start and finish, or start and <paramref name="now"/> when the run has
    /// not finished. Rendered compactly (e.g. <c>1h 04m</c>, <c>2m 09s</c>, <c>7.3s</c>). A negative
    /// span (clock skew) is clamped to zero rather than shown as garbage.
    /// </summary>
    public static string Duration(DateTimeOffset startedAt, DateTimeOffset? finishedAt, DateTimeOffset now)
    {
        var end = finishedAt ?? now;
        var span = end - startedAt;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes:D2}m";
        if (span.TotalMinutes >= 1)
            return $"{span.Minutes}m {span.Seconds:D2}s";
        return $"{span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
    }

    /// <summary>USD with cents-and-below precision — costs are often fractions of a cent.</summary>
    public static string Cost(decimal usd) =>
        "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>Absolute UTC timestamp, seconds precision, unambiguous for an audit context.</summary>
    public static string Timestamp(DateTimeOffset ts) =>
        ts.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>A css-class suffix per gate decision for the gate badge.</summary>
    public static string GateClass(GateDecision decision) => decision switch
    {
        GateDecision.Approved => "ok",
        GateDecision.Rejected => "rejected",
        _ => "await"
    };
}
