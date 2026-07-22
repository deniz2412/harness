using Harness.Policy;
using Harness.Tools;
using Xunit;

namespace Harness.Tools.Tests;

/// <summary>
/// The latch is the fail-closed guarantee for tool calls: the function-invoking loop underneath MAF
/// treats a tool exception as recoverable and retries, so a policy block has to survive being
/// swallowed there and re-surface to fail the node. These test that mechanism in isolation;
/// <see cref="AuditedToolTests"/> tests it end to end through a real wrapped tool.
/// </summary>
public class ToolCallContextTests
{
    private static ToolCallContext Ctx() =>
        new(Guid.NewGuid(), "review", ["repo.read"],
            new Dictionary<string, string> { ["repo"] = "read" });

    [Fact]
    public void Clean_context_does_not_throw()
    {
        var ctx = Ctx();
        Assert.Null(ctx.Latched);
        ctx.ThrowIfLatched();   // nothing latched ⇒ no-op
    }

    [Fact]
    public void ThrowIfLatched_rethrows_the_original_type()
    {
        var ctx = Ctx();
        ctx.Latch(new PolicyViolationException("pre-tool", "secret detected"));

        // A swallowed policy block must land on the run as a policy block, not a generic failure.
        var ex = Assert.Throws<PolicyViolationException>(() => ctx.ThrowIfLatched());
        Assert.Equal("pre-tool", ex.Stage);
    }

    [Fact]
    public void Latch_keeps_the_first_failure()
    {
        var ctx = Ctx();
        ctx.Latch(new PolicyViolationException("pre-tool", "first"));
        ctx.Latch(new InvalidOperationException("second"));

        // The first failure is the one that explains the run; later noise does not displace it.
        var ex = Assert.Throws<PolicyViolationException>(() => ctx.ThrowIfLatched());
        Assert.Equal("first", ex.Reason);
    }
}
