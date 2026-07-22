using Xunit;

namespace Harness.Engine.Tests;

/// <summary>
/// The sha is the audit's answer to "what definition ran?", so it must be stable for identical
/// content and must move for any change to the workflow or to a prompt it references.
/// </summary>
public sealed class WorkflowLoaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "harness-loader-tests", Guid.NewGuid().ToString("N"));

    private string WorkflowsDir => Path.Combine(_root, "workflows");
    private string PromptsDir => Path.Combine(_root, "prompts");

    private const string Yaml = """
        name: t
        nodes:
          - id: gather
            kind: agent
            prompt_ref: t/gather.md
          - id: post
            kind: agent
            depends_on: [gather]
            gate: human
            prompt_ref: t/post.md
        """;

    public WorkflowLoaderTests()
    {
        Directory.CreateDirectory(WorkflowsDir);
        Directory.CreateDirectory(Path.Combine(PromptsDir, "t"));
        Write("workflows/t.yaml", Yaml);
        Write("prompts/t/gather.md", "gather the diff");
        Write("prompts/t/post.md", "post the review");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content) =>
        File.WriteAllText(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    private WorkflowLoader Loader() => new(WorkflowsDir, PromptsDir);

    [Fact]
    public void Sha_is_stable_across_reloads()
    {
        Assert.Equal(Loader().Load("t").Sha, Loader().Load("t").Sha);
        Assert.NotEmpty(Loader().Load("t").Sha);
    }

    [Fact]
    public void Sha_changes_when_the_workflow_changes()
    {
        var before = Loader().Load("t").Sha;
        Write("workflows/t.yaml", Yaml.Replace("gate: human", "gate: auto"));
        Assert.NotEqual(before, Loader().Load("t").Sha);
    }

    [Fact]
    public void Sha_changes_when_a_referenced_prompt_changes()
    {
        var before = Loader().Load("t").Sha;
        Write("prompts/t/review.md", "unreferenced prompts do not count");
        Assert.Equal(before, Loader().Load("t").Sha);

        Write("prompts/t/post.md", "post the review, but differently");
        Assert.NotEqual(before, Loader().Load("t").Sha);
    }

    [Fact]
    public void Sha_is_path_tagged_so_moving_content_between_prompts_is_visible()
    {
        var before = Loader().Load("t").Sha;
        // Swap the two prompt bodies: same bytes overall, different files.
        Write("prompts/t/gather.md", "post the review");
        Write("prompts/t/post.md", "gather the diff");
        Assert.NotEqual(before, Loader().Load("t").Sha);
    }

    [Fact]
    public void Missing_prompt_file_fails_the_load()
    {
        File.Delete(Path.Combine(PromptsDir, "t", "post.md"));
        var ex = Assert.Throws<FileNotFoundException>(() => Loader().Load("t"));
        Assert.Contains("post", ex.Message);
    }

    [Fact]
    public void Prompt_ref_escaping_the_prompts_directory_fails_the_load()
    {
        Write("workflows/t.yaml", Yaml.Replace("t/post.md", "../workflows/t.yaml"));
        Assert.Throws<InvalidOperationException>(() => Loader().Load("t"));
    }

    [Fact]
    public void Unknown_gate_value_fails_the_load()
    {
        Write("workflows/t.yaml", Yaml.Replace("gate: human", "gate: humna"));
        Assert.Throws<InvalidOperationException>(() => Loader().Load("t"));
    }

    [Fact]
    public void Unknown_dependency_fails_the_load()
    {
        Write("workflows/t.yaml", Yaml.Replace("depends_on: [gather]", "depends_on: [nope]"));
        Assert.Throws<InvalidOperationException>(() => Loader().Load("t"));
    }

    [Fact]
    public void Unknown_workflow_fails_the_load()
    {
        Assert.Throws<FileNotFoundException>(() => Loader().Load("does-not-exist"));
    }
}
