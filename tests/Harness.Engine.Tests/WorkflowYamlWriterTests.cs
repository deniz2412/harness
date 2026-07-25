using Harness.Contracts;
using Harness.Engine;
using Xunit;

namespace Harness.Engine.Tests;

/// <summary>
/// The YAML emitter must be a faithful inverse of the loader: a shipped workflow, emitted and reloaded,
/// yields an equivalent definition — so the F4 builder produces the SAME artifact the platform runs.
/// </summary>
public class WorkflowYamlWriterTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Harness.sln not found above the test binary.");
    }

    private static WorkflowLoader Loader()
    {
        var root = RepoRoot();
        return new WorkflowLoader(
            Path.Combine(root, "workflows"), Path.Combine(root, "prompts"), Path.Combine(root, "agents"));
    }

    [Theory]
    [InlineData("pr-review")]                 // simple read-only, output_schema on a node
    [InlineData("test-generation")]           // agent-loop (until/max_iterations/fresh_context) + human gate
    [InlineData("threat-model-draft")]        // gated write (push_branch/open_pr)
    [InlineData("pr-security-review")]         // agent_ref node (loader-merged)
    public void Emitted_yaml_round_trips_through_the_loader(string name)
    {
        var loader = Loader();
        var wf = loader.Load(name);

        var yaml = WorkflowYamlWriter.ToYaml(wf);
        var wf2 = loader.LoadFromText(yaml, name, name.StartsWith("teams/") ? name.Split('/')[1] : null);

        Assert.Equal(wf.Name, wf2.Name);
        Assert.Equal(wf.Nodes.Count, wf2.Nodes.Count);
        for (var i = 0; i < wf.Nodes.Count; i++)
        {
            var a = wf.Nodes[i];
            var b = wf2.Nodes[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.DependsOn, b.DependsOn);
            Assert.Equal(a.Gate, b.Gate);
            Assert.Equal(a.PromptRef, b.PromptRef);          // for agent_ref nodes, the re-merged prompt
            Assert.Equal(a.Tools, b.Tools);                  // for agent_ref nodes, the re-merged tools
            Assert.Equal(a.ModelTier, b.ModelTier);
            Assert.Equal(a.OutputSchema, b.OutputSchema);
            Assert.Equal(a.Run, b.Run);                      // bash node command
            Assert.Equal(a.Until, b.Until);                  // agent-loop bound
            Assert.Equal(a.MaxIterations, b.MaxIterations);  // agent-loop bound (test-generation)
            Assert.Equal(a.FreshContext, b.FreshContext);    // agent-loop
            Assert.Equal(a.Approvers, b.Approvers);          // human-gate approvers
        }
        // NOTE: the sha is over exact file bytes, and the emitted YAML is not byte-identical to the
        // original file (comments stripped, canonical formatting) — so the round-trip preserves the
        // workflow's SEMANTICS (asserted above), not its byte-for-byte sha. Both shas are valid 64-hex.
        Assert.Equal(64, wf2.Sha.Length);
    }

    [Fact]
    public void Emitted_yaml_omits_nulls_defaults_and_the_sha()
    {
        var wf = Loader().Load("pr-review");
        var yaml = WorkflowYamlWriter.ToYaml(wf);

        Assert.DoesNotContain("sha:", yaml);
        Assert.DoesNotContain("null", yaml);
        Assert.DoesNotContain("agent_ref:", yaml);          // pr-review has no agent refs
        Assert.DoesNotContain("max_iterations:", yaml);     // no agent-loop node in pr-review
        Assert.DoesNotContain("run:", yaml);                // no bash node in pr-review
    }

    [Fact]
    public void A_hand_built_definition_emits_yaml_the_loader_accepts()
    {
        var wf = new WorkflowDefinition
        {
            Name = "hand-built",
            Description = "A minimal read-only review.",
            Permissions = new Dictionary<string, string> { ["repo"] = "read", ["github"] = "comment" },
            Nodes =
            [
                new NodeDefinition { Id = "gather", Kind = "agent", PromptRef = "pr-review/gather.md",
                    Tools = ["repo.read", "github.pr_diff"] },
                new NodeDefinition { Id = "post", Kind = "agent", DependsOn = ["gather"], Gate = "auto",
                    PromptRef = "pr-review/post.md", Tools = ["github.pr_comment"] },
            ],
        };

        var yaml = WorkflowYamlWriter.ToYaml(wf);
        var reloaded = Loader().LoadFromText(yaml, "hand-built", null);   // must not throw

        Assert.Equal("hand-built", reloaded.Name);
        Assert.Equal(2, reloaded.Nodes.Count);
        Assert.Equal("auto", reloaded.Nodes[1].Gate);
    }
}
