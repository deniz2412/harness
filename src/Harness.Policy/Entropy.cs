namespace Harness.Policy;

/// <summary>Shannon entropy, in bits per character, over a value's character distribution.</summary>
/// <remarks>
/// This is what keeps the generic <c>api_key = "..."</c> rule from firing on every string literal
/// in a diff: real credentials are near-uniform over their alphabet, prose and identifiers are not.
/// </remarks>
public static class Entropy
{
    public static double Shannon(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0d;

        var counts = new Dictionary<char, int>(value.Length);
        foreach (var c in value)
            counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;

        var length = (double)value.Length;
        var entropy = 0d;
        foreach (var n in counts.Values)
        {
            var p = n / length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}
