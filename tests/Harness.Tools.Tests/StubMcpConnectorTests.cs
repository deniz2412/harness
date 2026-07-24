using Xunit;

namespace Harness.Tools.Tests;

/// <summary>
/// The M7c connector transport seam, offline. It proves the in-process stub is a faithful,
/// deterministic <see cref="IMcpConnector"/>: it advertises exactly the namespace and operations it
/// was given, echoes deterministically by default, honours a custom responder, tolerates any
/// argument string for a served op, and — the governance point — fails closed on an operation it
/// does not advertise even though nothing about the call is malformed.
/// </summary>
public sealed class StubMcpConnectorTests
{
    [Fact]
    public void Namespace_and_operations_round_trip_from_the_ctor()
    {
        var connector = new StubMcpConnector("docs", new[] { "search", "fetch" });

        Assert.Equal("docs", connector.Namespace);
        Assert.Equal(new[] { "fetch", "search" }, connector.Operations.OrderBy(o => o));
        Assert.Equal(2, connector.Operations.Count);
    }

    [Fact]
    public async Task Default_responder_echoes_deterministically_and_is_stable_across_calls()
    {
        var connector = new StubMcpConnector("docs", new[] { "search" });

        var first = await connector.InvokeAsync("search", "{\"q\":\"x\"}", CancellationToken.None);
        var second = await connector.InvokeAsync("search", "{\"q\":\"x\"}", CancellationToken.None);

        Assert.Equal("[stub:docs.search] {\"q\":\"x\"}", first);
        Assert.Equal(first, second); // deterministic: same input, same output, no hidden state
    }

    [Fact]
    public async Task Custom_responder_is_used_and_receives_the_operation_and_argumentsJson()
    {
        string? seenOp = null;
        string? seenArgs = null;
        var connector = new StubMcpConnector("docs", new[] { "search" },
            responder: (op, args) =>
            {
                seenOp = op;
                seenArgs = args;
                return $"handled {op}";
            });

        var result = await connector.InvokeAsync("search", "{\"q\":\"y\"}", CancellationToken.None);

        Assert.Equal("search", seenOp);
        Assert.Equal("{\"q\":\"y\"}", seenArgs);
        Assert.Equal("handled search", result);
    }

    [Fact]
    public async Task InvokeAsync_fails_closed_on_an_operation_not_advertised()
    {
        var connector = new StubMcpConnector("docs", new[] { "search" });

        // Defence in depth: even a well-formed call to an unadvertised op is refused, not echoed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connector.InvokeAsync("delete", "{}", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"deeply\":{\"nested\":[1,2,3]}}")]
    public async Task InvokeAsync_tolerates_arbitrary_argumentsJson_for_a_valid_op(string args)
    {
        var connector = new StubMcpConnector("docs", new[] { "search" });

        // argumentsJson is opaque: the stub never parses it, so any string is fine and echoes back.
        var result = await connector.InvokeAsync("search", args, CancellationToken.None);

        Assert.Equal($"[stub:docs.search] {args}", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_rejects_a_blank_namespace(string blank)
    {
        Assert.Throws<ArgumentException>(() => new StubMcpConnector(blank, new[] { "search" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_rejects_a_blank_operation(string blank)
    {
        Assert.Throws<ArgumentException>(() => new StubMcpConnector("docs", new[] { "search", blank }));
    }

    [Fact]
    public void Ctor_rejects_a_duplicate_operation()
    {
        Assert.Throws<ArgumentException>(() => new StubMcpConnector("docs", new[] { "search", "search" }));
    }
}
