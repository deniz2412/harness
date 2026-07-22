using Xunit;

namespace Harness.Engine.Tests;

/// <summary>
/// The workflows and prompts that actually ship must satisfy the loader's fail-closed validation —
/// a broken prompt_ref should be caught here, not on a run.
/// </summary>
public class ShippedWorkflowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    [Fact]
    public void Pr_review_loads_and_is_pinned()
    {
        var root = RepoRoot();
        var loader = new WorkflowLoader(Path.Combine(root, "workflows"), Path.Combine(root, "prompts"));

        var wf = loader.Load("pr-review");

        Assert.Equal(3, wf.Nodes.Count);
        Assert.Equal(64, wf.Sha.Length);                       // sha256, lowercase hex
        Assert.Equal(wf.Sha, loader.Load("pr-review").Sha);
    }
}
