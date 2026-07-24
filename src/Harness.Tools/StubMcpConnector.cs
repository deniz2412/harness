namespace Harness.Tools;

/// <summary>
/// An in-process <see cref="IMcpConnector"/>: deterministic, no egress, safe for tests and the PoC
/// container. It stands in for a real MCP server so the governance layer — the config allowlist, the
/// namespaced tool names, the per-call audit/policy wrapping in <c>AuditedTool</c> — can be built and
/// exercised now, while the real JSON-RPC-over-stdio/SSE transport stays a deferred drop-in behind
/// <see cref="IMcpConnector"/>.
///
/// It adds no capability of its own (invariant 1): it only runs a supplied responder, defaulting to a
/// deterministic echo. It never interprets <c>argumentsJson</c> or the result (invariant 4) — both are
/// untrusted and passed through opaquely; auditing and scanning happen one layer up.
/// </summary>
public sealed class StubMcpConnector : IMcpConnector
{
    private readonly HashSet<string> _operations;
    private readonly Func<string, string, string> _responder;

    /// <param name="ns">The toolset namespace (non-blank).</param>
    /// <param name="operations">The advertised operations. Each must be non-blank and unique.</param>
    /// <param name="responder">
    /// <c>responder(operation, argumentsJson) =&gt; resultText</c>. When null, a deterministic echo
    /// <c>"[stub:{ns}.{operation}] {argumentsJson}"</c> is used.
    /// </param>
    public StubMcpConnector(string ns, IEnumerable<string> operations, Func<string, string, string>? responder = null)
    {
        if (string.IsNullOrWhiteSpace(ns))
            throw new ArgumentException("An MCP connector namespace must be non-blank.", nameof(ns));
        ArgumentNullException.ThrowIfNull(operations);

        Namespace = ns;

        // Validate up front: a blank op could never be advertised as a tool name, and a duplicate
        // would make the served set ambiguous. Fail at construction, not at first invoke.
        _operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in operations)
        {
            if (string.IsNullOrWhiteSpace(op))
                throw new ArgumentException("An MCP operation name must be non-blank.", nameof(operations));
            if (!_operations.Add(op))
                throw new ArgumentException($"Duplicate MCP operation '{op}'.", nameof(operations));
        }

        _responder = responder ?? ((operation, argumentsJson) => $"[stub:{ns}.{operation}] {argumentsJson}");
    }

    /// <inheritdoc/>
    public string Namespace { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Operations => _operations;

    /// <inheritdoc/>
    public Task<string> InvokeAsync(string operation, string argumentsJson, CancellationToken ct)
    {
        // Fail-closed (invariant 2): serve only advertised operations, whatever the caller passed —
        // defence in depth even if the config allowlist was somehow bypassed upstream.
        if (!_operations.Contains(operation))
            throw new InvalidOperationException(
                $"Operation '{operation}' is not served by MCP connector '{Namespace}'.");

        // argumentsJson is opaque and untrusted: any string is tolerated for a valid op and passed
        // straight to the responder. Pure and synchronous under the hood — no timers, no IO.
        return Task.FromResult(_responder(operation, argumentsJson));
    }
}
