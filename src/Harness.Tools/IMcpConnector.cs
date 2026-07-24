namespace Harness.Tools;

/// <summary>
/// One connected external MCP toolset, exposed as a single namespace (product-vision §5a). The
/// platform mounts such a toolset with a per-operation allowlist and names each tool
/// <c>&lt;namespace&gt;.&lt;operation&gt;</c>; this seam is how the catalog invokes one advertised
/// operation without knowing how the toolset is reached.
///
/// The interface is deliberately transport-agnostic. For this PoC milestone the only implementation
/// is <see cref="StubMcpConnector"/> — an in-process, no-egress stand-in — but a real MCP client
/// speaking JSON-RPC over stdio/SSE is a drop-in behind exactly this contract, the same
/// subprocess-vs-container deviation used elsewhere in the codebase. Callers depend on the seam, not
/// the transport, so that swap changes no caller.
///
/// Contract for any implementation: <see cref="InvokeAsync"/> is fail-closed — it serves only the
/// operations in <see cref="Operations"/> and throws for anything else (defence in depth, even if
/// something bypassed the config allowlist). The <c>argumentsJson</c> and the returned text are
/// untrusted content (invariant 4): an implementation passes them through opaquely and never
/// interprets them; the caller's <c>AuditedTool</c> is what audits the call and scans the result.
/// </summary>
public interface IMcpConnector
{
    /// <summary>The toolset namespace, e.g. <c>"docs"</c>. Tools are named <c>&lt;namespace&gt;.&lt;operation&gt;</c>.</summary>
    string Namespace { get; }

    /// <summary>The operations this connector serves — what the server advertises.</summary>
    IReadOnlyCollection<string> Operations { get; }

    /// <summary>
    /// Invoke one operation with a JSON argument object; returns a text result the agent sees.
    /// Fail-closed: throws if <paramref name="operation"/> is not one of <see cref="Operations"/>.
    /// </summary>
    Task<string> InvokeAsync(string operation, string argumentsJson, CancellationToken ct);
}
