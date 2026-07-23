using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Harness.Policy;

/// <summary>
/// The repo allowlist: the policy control (M3) that decides which existing repositories a run may
/// act on. A run whose target repo is not listed is refused before any tool executes.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed throughout. An empty or absent allowlist permits nothing — never "allow all on
/// error". A malformed repo reference (anything that is not exactly <c>owner/name</c>) is denied.
/// A malformed <em>entry</em> in the operator-supplied allowlist is a load-time block, so a broken
/// config stops the process at startup rather than quietly widening or narrowing what runs.
/// </para>
/// <para>
/// This is <b>not</b> a mechanism to create repositories (invariant 1): it only narrows the set of
/// pre-existing repos a run may touch. Matching is case-insensitive because GitHub owners and repo
/// names are. Two entry shapes are accepted:
/// <list type="bullet">
///   <item><description><c>owner/name</c> — one exact repository.</description></item>
///   <item><description><c>owner/*</c> — every repository under one owner (org or user).</description></item>
/// </list>
/// The wildcard is deliberately owner-scoped: <c>owner/*</c> never matches <c>owner2/anything</c>.
/// </para>
/// </remarks>
public sealed class RepoAllowlist
{
    internal const string Stage = "repo-allowlist";

    /// <summary>A single path segment. Alphanumerics plus <c>. _ -</c>; no whitespace, slashes or
    /// path traversal. Bounded length keeps an adversarial config entry cheap to validate.</summary>
    private static readonly Regex Segment = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly HashSet<string> _exact;            // "owner/name", case-insensitive
    private readonly HashSet<string> _wildcardOwners;   // owners carrying an "owner/*" entry
    private readonly string[] _entries;                 // normalised, de-duplicated, for scoping

    /// <summary>
    /// Builds an allowlist from operator-supplied entries (the M3 path: the orchestrator constructs
    /// this from app configuration). A <see langword="null"/> or empty sequence yields an allowlist
    /// that denies every repo. Any present-but-malformed entry is a fail-closed block.
    /// </summary>
    /// <exception cref="PolicyViolationException">An entry is not a valid <c>owner/name</c> or
    /// <c>owner/*</c> reference.</exception>
    public RepoAllowlist(IEnumerable<string>? entries)
    {
        _exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _wildcardOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var raw in entries ?? [])
        {
            if (!TryParseEntry(raw, out var owner, out var name, out var isWildcard))
                throw new PolicyViolationException(
                    Stage,
                    "the repo allowlist contains an entry that is not a valid 'owner/name' or " +
                    "'owner/*' reference; a malformed allowlist denies every run");

            var normalised = $"{owner}/{(isWildcard ? "*" : name)}";
            var added = isWildcard ? _wildcardOwners.Add(owner) : _exact.Add(normalised);
            if (added) ordered.Add(normalised);
        }

        _entries = ordered.ToArray();
    }

    /// <summary>Convenience factory equivalent to the constructor.</summary>
    public static RepoAllowlist FromEntries(IEnumerable<string>? entries) => new(entries);

    /// <summary>
    /// Builds an allowlist from YAML of the shape <c>repos: [ owner/name, owner/* ]</c>. Provided
    /// for callers that keep the allowlist in a config file; the config-object path (the
    /// constructor) is what M3 uses. A missing or empty <c>repos:</c> list denies every run.
    /// </summary>
    /// <exception cref="PolicyViolationException">The YAML will not parse, or an entry is malformed.</exception>
    public static RepoAllowlist FromYaml(string yaml)
    {
        AllowlistFile? file;
        try
        {
            file = PolicyData.Deserializer.Deserialize<AllowlistFile>(yaml);
        }
        catch (Exception ex)
        {
            throw new PolicyViolationException(Stage, $"repo allowlist is not valid YAML: {ex.Message}");
        }

        return new RepoAllowlist(file?.Repos ?? []);
    }

    /// <summary>The normalised, de-duplicated entries, for scoping cross-repo search to what is
    /// permitted. Exact entries read <c>owner/name</c>; wildcard entries read <c>owner/*</c>.</summary>
    public IReadOnlyCollection<string> Entries => _entries;

    /// <summary>
    /// True when <paramref name="repoFullName"/> is a well-formed <c>owner/name</c> that the
    /// allowlist permits — either exactly, or via an <c>owner/*</c> wildcard. False for
    /// <see langword="null"/>, empty, or any string that is not exactly <c>owner/name</c>.
    /// </summary>
    public bool IsAllowed(string? repoFullName)
    {
        // A wildcard target ("owner/*") is a query, never a run target: it must not be "allowed".
        if (!TryParseReference(repoFullName, out var owner, out var name))
            return false;

        return _exact.Contains($"{owner}/{name}") || _wildcardOwners.Contains(owner);
    }

    /// <summary>
    /// Enforces the allowlist. Called by the orchestrator at <c>POST /runs</c> and on resume, before
    /// any tool runs. No-op when the repo is permitted; otherwise a fail-closed block.
    /// </summary>
    /// <exception cref="PolicyViolationException">The repo is malformed or not on the allowlist.</exception>
    public void Assert(string? repoFullName)
    {
        // Deliberately does not echo the raw input: an untrusted, possibly hostile run target must
        // not be reflected verbatim into a log line or error.
        if (!TryParseReference(repoFullName, out var owner, out var name))
            throw new PolicyViolationException(
                Stage, "the run target is not a valid 'owner/name' repository reference");

        if (!(_exact.Contains($"{owner}/{name}") || _wildcardOwners.Contains(owner)))
            throw new PolicyViolationException(
                Stage, $"repository '{owner}/{name}' is not on the run allowlist");
    }

    /// <summary>Parses a concrete run target. Rejects the wildcard form — <c>owner/*</c> is a scope,
    /// not a repository a run can act on.</summary>
    private static bool TryParseReference(
        string? repo, [NotNullWhen(true)] out string? owner, [NotNullWhen(true)] out string? name)
    {
        if (TryParseEntry(repo, out owner, out name, out var isWildcard) && !isWildcard)
            return true;
        owner = null;
        name = null;
        return false;
    }

    /// <summary>
    /// Parses an allowlist entry or a run target into its two segments. Accepts exactly
    /// <c>owner/name</c> and <c>owner/*</c>; rejects everything else (missing owner, extra segments,
    /// whitespace, path traversal, empty segments).
    /// </summary>
    private static bool TryParseEntry(
        string? value,
        [NotNullWhen(true)] out string? owner,
        [NotNullWhen(true)] out string? name,
        out bool isWildcard)
    {
        owner = null;
        name = null;
        isWildcard = false;

        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();

        // Path traversal never appears in a real owner or repo name; reject it outright.
        if (trimmed.Contains("..", StringComparison.Ordinal)) return false;

        // Exactly one separator: split into exactly two non-empty parts.
        var parts = trimmed.Split('/');
        if (parts.Length != 2) return false;

        var ownerPart = parts[0];
        var namePart = parts[1];

        if (!Segment.IsMatch(ownerPart)) return false;

        if (namePart == "*")
        {
            owner = ownerPart;
            name = "*";
            isWildcard = true;
            return true;
        }

        if (!Segment.IsMatch(namePart)) return false;

        owner = ownerPart;
        name = namePart;
        return true;
    }

    // ---- YAML shape (deserialization only) ----

    private sealed class AllowlistFile
    {
        public List<string>? Repos { get; set; }
    }
}
