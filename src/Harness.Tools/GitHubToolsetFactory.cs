using Octokit;

namespace Harness.Tools;

/// <summary>
/// Builds a <see cref="GitHubToolset"/> bound to a specific <c>owner/name</c> per run (M3). Before M3
/// the toolset was a single startup-configured singleton; a run now acts on its own <c>run.Repo</c>,
/// so the repo-bound tools (<c>pr_diff</c>, <c>pr_comment</c>, <c>get_issue</c>, <c>push_branch</c>,
/// <c>open_pr</c>, <c>issue_comment</c>) must be reachable through a per-run instance the orchestrator
/// builds from <c>ctx.Repo</c>.
///
/// The factory ONLY builds — it does not consult the repo allowlist. The allowlist is a policy control
/// enforced before a run reaches any tool (workstream 1); duplicating the check here would split one
/// boundary across two layers. It reuses the existing <see cref="GitHubToolset"/> type rather than
/// duplicating any method, and never touches the token (the injected client already carries it).
/// </summary>
public sealed class GitHubToolsetFactory(IGitHubClient client)
{
    /// <summary>
    /// Parse <paramref name="repoFullName"/> as <c>owner/name</c> and bind a toolset to it. Fails fast
    /// with a clear exception on a blank or malformed value: a toolset built over a blank repo would
    /// not fail here but 404 silently mid-run — the exact class of bug M0 fixed by validating owner/repo
    /// at startup. Better to refuse the run before it reaches the model than to spin on a phantom repo.
    /// </summary>
    public GitHubToolset ForRepo(string repoFullName)
    {
        if (string.IsNullOrWhiteSpace(repoFullName))
            throw new ArgumentException(
                "A target repository is required as \"owner/name\"; got a blank value.", nameof(repoFullName));

        var parts = repoFullName.Trim().Split('/');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException(
                $"Repository must be \"owner/name\"; got \"{repoFullName}\".", nameof(repoFullName));

        return new GitHubToolset(client, parts[0].Trim(), parts[1].Trim());
    }
}
