using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Harness.Audit;

/// <summary>
/// The one definition of the chain hash. Emitter and verifier must never drift apart, so both go
/// through here.
///
/// <para>
/// <b>Definition.</b> <c>hash(n) = sha256( field(utf8(hash(n-1))) ‖ field(runId) ‖ field(seq) ‖
/// field(type) ‖ field(node) ‖ field(ts) ‖ field(payload_bytes) )</c>, upper-case hex, where each
/// <c>field(x)</c> is the 8-byte big-endian length of <c>x</c>'s bytes followed by those bytes.
/// </para>
///
/// <para>
/// <b>Why length-prefix every field.</b> Plain concatenation is ambiguous — <c>Node="a",
/// Type="bc"</c> and <c>Node="ab", Type="c"</c> would hash identically, letting an attacker
/// reattribute an event across a field boundary without breaking the chain. Prefixing each field
/// with its exact byte length makes the encoding injective: one, and only one, tuple of fields
/// yields a given byte stream, so a change to any bound field necessarily changes the hash.
/// </para>
///
/// <para>
/// <b>What is bound.</b> <c>RunId</c>, <c>Seq</c>, <c>Type</c>, <c>Node</c>, <c>Ts</c> and the
/// payload — the whole tamper/reattribution/renumber/backdate surface STRIDE F3 named. Binding
/// them means a row whose metadata is edited in the database no longer verifies, where the
/// original M0 hash (payload only) reported it intact.
/// </para>
///
/// <para>
/// <b>Ts precision.</b> Bound at <em>millisecond</em> resolution (Unix ms). This is not cosmetic:
/// Postgres <c>timestamptz</c> keeps microseconds while .NET keeps 100 ns ticks, so hashing a
/// full-precision timestamp at emit would fail to verify after the value round-trips through the
/// database. The emitter truncates <c>Ts</c> to the same millisecond value it hashes, so store and
/// recompute agree exactly. Ts is an immutable, server-set field on an append-only event — there
/// is no legitimate post-hoc clock correction — so binding it is safe and turns backdating into a
/// detectable tamper.
/// </para>
///
/// <para>
/// <b>Left out.</b> <c>TokensIn/Out</c> and <c>CostUsd</c> — billing metrics, not security
/// controls; a wrong cost is a metering bug, not a forged record. If cost ever becomes an audited
/// control (budget-enforcement disputes), fold it in here and the chain re-anchors on the next
/// fresh volume.
/// </para>
///
/// <para>
/// Hashing raw payload <em>bytes</em> (not a decoded string) keeps verification exact even for a
/// payload that would not survive a UTF-8 → UTF-16 → UTF-8 round trip.
/// </para>
/// </summary>
public static class ChainHash
{
    /// <summary>Seed for the first event of a run.</summary>
    public const string Genesis = "genesis";

    /// <summary>
    /// The millisecond-truncated timestamp that must be both stored and hashed. Callers persist
    /// this exact value on the event so that recomputation matches after a database round trip.
    /// </summary>
    public static DateTimeOffset NormalizeTs(DateTimeOffset ts) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ts.ToUnixTimeMilliseconds());

    /// <summary>
    /// Computes the chain hash for one event, binding all security-relevant metadata. Pass the
    /// <see cref="NormalizeTs"/>-truncated timestamp — the same value stored on the row.
    /// </summary>
    public static string Compute(
        string previousHash, Guid runId, long seq, string type, string node, DateTimeOffset ts,
        ReadOnlySpan<byte> payloadBytes)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendField(hasher, Encoding.UTF8.GetBytes(previousHash));
        AppendField(hasher, runId.ToByteArray());
        AppendInt64(hasher, seq);
        AppendField(hasher, Encoding.UTF8.GetBytes(type));
        AppendField(hasher, Encoding.UTF8.GetBytes(node));
        AppendInt64(hasher, ts.ToUnixTimeMilliseconds());
        AppendField(hasher, payloadBytes);

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    /// <summary>String-payload overload; encodes the payload as UTF-8.</summary>
    public static string Compute(
        string previousHash, Guid runId, long seq, string type, string node, DateTimeOffset ts,
        string payload) =>
        Compute(previousHash, runId, seq, type, node, ts, Encoding.UTF8.GetBytes(payload));

    private static void AppendInt64(IncrementalHash hasher, long value)
    {
        Span<byte> buf = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buf, value);
        AppendField(hasher, buf);
    }

    private static void AppendField(IncrementalHash hasher, ReadOnlySpan<byte> field)
    {
        Span<byte> len = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(len, field.Length);
        hasher.AppendData(len);
        hasher.AppendData(field);
    }
}
