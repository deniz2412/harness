namespace Harness.Audit;

/// <summary>
/// Turns a stored <c>payload_ref</c> into a path on this machine. The emitter writes the reference
/// as <c>file://{absolute path}</c>, which is the path inside the harness container. A verifier
/// running elsewhere (the CLI against a mounted copy of the audit volume) passes a root override;
/// resolution is then deterministic — <c>{root}/{runId}/{filename}</c> — and never a "if the first
/// path is missing, try the other one" fallback, because that would let a deleted payload be
/// silently satisfied by a different file.
/// </summary>
public static class PayloadPath
{
    private const string Scheme = "file://";

    public static string? Resolve(string? payloadRef, Guid runId, string? rootOverride)
    {
        if (string.IsNullOrWhiteSpace(payloadRef)) return null;

        var raw = payloadRef.StartsWith(Scheme, StringComparison.Ordinal)
            ? payloadRef[Scheme.Length..]
            : payloadRef;
        if (raw.Length == 0) return null;

        if (string.IsNullOrWhiteSpace(rootOverride)) return raw;

        // Only the file name is taken from the reference, so a doctored ref cannot walk out of
        // the override root.
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        return string.IsNullOrEmpty(name) ? null : Path.Combine(rootOverride, runId.ToString(), name);
    }
}
