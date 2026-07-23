using Xunit;

namespace Harness.Eval;

/// <summary>
/// Proves the golden-run comparator (design-spec §5, M2) does the two things that make it worth
/// having: it TOLERATES cosmetic variation between runs, and it CATCHES a real regression. Fully
/// offline — it compares fixture files, never a live model.
/// </summary>
public class GoldenComparatorTests
{
    private static string FixturesDir() => Path.Combine(AppContext.BaseDirectory, "fixtures");
    private static string Fixture(string name) => Path.Combine(FixturesDir(), name);

    [Fact]
    public void Cosmetic_variation_still_matches_the_golden()
    {
        // Same three targets, same priorities, but reworded rationales, reordered, and one extra
        // proposed target. None of that is a regression.
        var result = GoldenComparator.CompareFiles(
            Fixture("test-plan.golden.json"),
            Fixture("test-plan.candidate-pass.json"),
            GoldenSchema.TestPlan);

        Assert.True(result.IsMatch, result.Explain());
        Assert.Empty(result.Regressions);

        // The extra target is reported (explainable) but does not fail the comparison.
        var extra = Assert.Single(result.Diffs, d => d.Kind == DiffKind.Extra);
        Assert.Equal("Money.Round", extra.Identity);
    }

    [Fact]
    public void Dropped_and_downgraded_findings_are_caught()
    {
        // The failing candidate drops CouponParser.Parse entirely and downgrades the critical tier
        // bug to "low". Both are regressions the comparator must catch.
        var result = GoldenComparator.CompareFiles(
            Fixture("test-plan.golden.json"),
            Fixture("test-plan.candidate-fail.json"),
            GoldenSchema.TestPlan);

        Assert.False(result.IsMatch);

        Assert.Contains(result.Regressions,
            d => d.Kind == DiffKind.Missing && d.Identity == "CouponParser.Parse");
        Assert.Contains(result.Regressions,
            d => d.Kind == DiffKind.CategoryMismatch && d.Identity == "DiscountCalculator.ApplyTier");

        // Explanation names both the missing item and the changed severity.
        var explanation = result.Explain();
        Assert.Contains("CouponParser.Parse", explanation);
        Assert.Contains("priority changed", explanation);
    }

    [Fact]
    public void Identical_output_reports_no_diffs()
    {
        var golden = File.ReadAllText(Fixture("test-plan.golden.json"));

        var result = GoldenComparator.Compare(golden, golden, GoldenSchema.TestPlan);

        Assert.True(result.IsMatch);
        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void Comparator_is_schema_agnostic_over_review_findings_shape()
    {
        // The same comparator works on the pr-review structured-output shape, keyed on file+severity.
        const string golden = """
            { "findings": [ { "file": "A.cs", "severity": "major", "message": "x" } ] }
            """;
        const string candidate = """
            { "findings": [ { "file": "A.cs", "severity": "minor", "message": "totally reworded" } ] }
            """;

        var result = GoldenComparator.Compare(golden, candidate, GoldenSchema.ReviewFindings);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Regressions, d => d.Kind == DiffKind.CategoryMismatch && d.Identity == "A.cs");
    }
}
