using Harness.Contracts;
using Harness.Engine;
using Xunit;

namespace Harness.Engine.Tests;

public class DagExecutorTests
{
    private static WorkflowDefinition Wf(params NodeDefinition[] nodes) =>
        new() { Name = "t", Nodes = [.. nodes] };

    private static NodeDefinition N(string id, params string[] deps) =>
        new() { Id = id, Kind = "agent", DependsOn = [.. deps] };

    [Fact]
    public void TopologicalOrder_respects_dependencies()
    {
        var order = DagExecutor.TopologicalOrder(Wf(N("post", "review"), N("gather"), N("review", "gather")))
            .Select(n => n.Id).ToList();
        Assert.True(order.IndexOf("gather") < order.IndexOf("review"));
        Assert.True(order.IndexOf("review") < order.IndexOf("post"));
    }

    [Fact]
    public void TopologicalOrder_detects_cycles()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DagExecutor.TopologicalOrder(Wf(N("a", "b"), N("b", "a"))));
    }
}
