using System.Text.Json;

namespace Harness.Eval;

/// <summary>
/// Which JSON fields the comparator keys on. Everything else in an item — free-text rationale,
/// message, case lists, ordering — is treated as cosmetic and ignored. This is what makes a golden
/// comparison tolerant: two runs that phrase a finding differently but agree on WHAT and HOW SEVERE
/// still match; a run that drops a required finding or changes its severity does not.
/// </summary>
public sealed record GoldenSchema(string ItemsField, string IdentityField, string CategoryField)
{
    /// <summary>The shape produced by test-generation's gather node (schemas/test-plan.json).</summary>
    public static readonly GoldenSchema TestPlan = new("targets", "symbol", "priority");

    /// <summary>The shape produced by pr-review's review node (schemas/review-findings.json).</summary>
    public static readonly GoldenSchema ReviewFindings = new("findings", "file", "severity");
}

public enum DiffKind
{
    /// <summary>A required item in the golden is absent from the candidate. A regression.</summary>
    Missing,
    /// <summary>A matched item's category (severity/priority) changed. A regression.</summary>
    CategoryMismatch,
    /// <summary>The candidate has an item the golden does not. Tolerated; reported as a note.</summary>
    Extra,
}

public sealed record ItemDiff(DiffKind Kind, string Identity, string Message);

/// <summary>Outcome of comparing a candidate structured output against a golden one.</summary>
public sealed record GoldenComparison(IReadOnlyList<ItemDiff> Diffs)
{
    /// <summary>
    /// A match means no regressions: every required item is present with the same category. Extra
    /// items (a candidate that legitimately found more) do not fail the comparison.
    /// </summary>
    public bool IsMatch => Regressions.Count == 0;

    public IReadOnlyList<ItemDiff> Regressions =>
        Diffs.Where(d => d.Kind is DiffKind.Missing or DiffKind.CategoryMismatch).ToList();

    /// <summary>Human-readable, line-per-diff explanation — the "explainable" half of the diff.</summary>
    public string Explain() =>
        Diffs.Count == 0
            ? "candidate matches golden on identity + category (cosmetic differences ignored)"
            : string.Join("\n", Diffs.Select(d => $"[{d.Kind}] {d.Identity}: {d.Message}"));
}

/// <summary>
/// Offline comparator for golden-run evaluation (design-spec §5, M2). It compares two ALREADY
/// PRODUCED structured outputs — it never calls a model or the gateway. Comparison is tolerant and
/// explainable rather than exact string equality: it matches items by a stable identity field,
/// requires every golden item to be present, requires matched categories, and ignores free text and
/// ordering. That is enough to catch a real regression (a dropped or downgraded finding) while
/// tolerating cosmetic variation between runs.
/// </summary>
public static class GoldenComparator
{
    public static GoldenComparison Compare(string goldenJson, string candidateJson, GoldenSchema schema)
    {
        var golden = Parse(goldenJson, schema);
        var candidate = Parse(candidateJson, schema);
        var diffs = new List<ItemDiff>();

        foreach (var (id, cat) in golden)
        {
            if (!candidate.TryGetValue(id, out var candCat))
                diffs.Add(new ItemDiff(DiffKind.Missing, id,
                    $"required item absent from candidate (golden {schema.CategoryField}={cat})"));
            else if (!string.Equals(cat, candCat, StringComparison.OrdinalIgnoreCase))
                diffs.Add(new ItemDiff(DiffKind.CategoryMismatch, id,
                    $"{schema.CategoryField} changed: golden={cat} candidate={candCat}"));
        }

        foreach (var (id, cat) in candidate)
            if (!golden.ContainsKey(id))
                diffs.Add(new ItemDiff(DiffKind.Extra, id,
                    $"candidate adds an item not in golden ({schema.CategoryField}={cat})"));

        return new GoldenComparison(diffs);
    }

    /// <summary>Reads two files and compares them. Convenience for fixture-based tests.</summary>
    public static GoldenComparison CompareFiles(string goldenPath, string candidatePath, GoldenSchema schema) =>
        Compare(File.ReadAllText(goldenPath), File.ReadAllText(candidatePath), schema);

    /// <summary>identity → category, one entry per item. A later duplicate identity wins (PoC).</summary>
    private static Dictionary<string, string> Parse(string json, GoldenSchema schema)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(schema.ItemsField, out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new FormatException($"Expected an array property '{schema.ItemsField}' at the JSON root.");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var id = item.TryGetProperty(schema.IdentityField, out var idEl) ? idEl.GetString() : null;
            if (id is null)
                throw new FormatException($"An item is missing its identity field '{schema.IdentityField}'.");
            var cat = item.TryGetProperty(schema.CategoryField, out var catEl) ? catEl.GetString() : null;
            map[id] = cat ?? "";
        }
        return map;
    }
}
