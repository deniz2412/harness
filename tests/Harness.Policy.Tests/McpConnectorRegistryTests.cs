using Xunit;

namespace Harness.Policy.Tests;

/// <summary>
/// The M7c MCP connector registry: the declarative allowlist deciding which external MCP toolsets
/// the platform may mount, which operations each exposes, and whether they may reach a write-capable
/// node. Fail-closed — an absent file mounts nothing, a malformed file blocks startup, and every
/// query is deny-by-default.
/// </summary>
public class McpConnectorRegistryTests
{
    private const string ValidYaml = """
        connectors:
          - namespace: docs
            description: Internal knowledge-base search (stub connector).
            write_capable: false
            operations:
              - search
              - get_page
          - namespace: jira
            description: Issue tracker.
            write_capable: true
            operations:
              - search
              - create_issue
        """;

    // ---- valid multi-connector parse ----

    [Fact]
    public void Parses_a_valid_multi_connector_file()
    {
        var reg = McpConnectorRegistry.FromYaml(ValidYaml);

        Assert.Equal(2, reg.Connectors.Count);

        var docs = reg.Connectors[0];
        Assert.Equal("docs", docs.Namespace);
        Assert.Equal("Internal knowledge-base search (stub connector).", docs.Description);
        Assert.False(docs.WriteCapable);
        Assert.Equal(new[] { "search", "get_page" }, docs.Operations);

        var jira = reg.Connectors[1];
        Assert.Equal("jira", jira.Namespace);
        Assert.True(jira.WriteCapable);

        Assert.True(reg.IsDeclared("docs"));
        Assert.True(reg.IsDeclared("jira"));
        Assert.False(reg.IsDeclared("sonar"));
    }

    // ---- absent file → empty registry (mounts nothing) ----

    [Fact]
    public void Absent_file_yields_an_empty_registry_that_mounts_nothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-connectors-{Guid.NewGuid():N}.yaml");
        Assert.False(File.Exists(missing));

        var reg = McpConnectorRegistry.FromFile(missing);

        Assert.Empty(reg.Connectors);
        Assert.False(reg.IsDeclared("docs"));
        Assert.False(reg.IsAllowed("docs", "search"));
        Assert.False(reg.IsWriteCapable("docs"));
    }

    [Fact]
    public void Present_file_is_read_and_parsed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"connectors-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, ValidYaml);
        try
        {
            var reg = McpConnectorRegistry.FromFile(path);
            Assert.True(reg.IsAllowed("docs", "search"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_connectors_list_declares_nothing()
    {
        var reg = McpConnectorRegistry.FromYaml("connectors: []\n");

        Assert.Empty(reg.Connectors);
        Assert.False(reg.IsAllowed("docs", "search"));
    }

    // ---- fail-closed: malformed content throws ----

    [Fact]
    public void Malformed_yaml_throws()
    {
        var ex = Assert.Throws<PolicyViolationException>(
            () => McpConnectorRegistry.FromYaml("connectors: [ : : ]\n  bad"));
        Assert.Equal("mcp-connector-registry", ex.Stage);
    }

    [Fact]
    public void Duplicate_namespace_throws()
    {
        const string yaml = """
            connectors:
              - namespace: docs
                operations: [search]
              - namespace: docs
                operations: [get_page]
            """;

        var ex = Assert.Throws<PolicyViolationException>(() => McpConnectorRegistry.FromYaml(yaml));
        Assert.Equal("mcp-connector-registry", ex.Stage);
        Assert.Contains("twice", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("repo")]
    [InlineData("codesearch")]
    [InlineData("GitHub")]   // reserved match is case-insensitive
    public void Namespace_equal_to_a_reserved_built_in_throws(string reserved)
    {
        var yaml = $"connectors:\n  - namespace: {reserved}\n    operations: [search]\n";

        var ex = Assert.Throws<PolicyViolationException>(() => McpConnectorRegistry.FromYaml(yaml));
        Assert.Equal("mcp-connector-registry", ex.Stage);
        Assert.Contains("shadows a curated built-in", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_namespace_throws()
    {
        const string yaml = """
            connectors:
              - namespace: "   "
                operations: [search]
            """;

        Assert.Throws<PolicyViolationException>(() => McpConnectorRegistry.FromYaml(yaml));
    }

    [Fact]
    public void Blank_operation_throws()
    {
        const string yaml = """
            connectors:
              - namespace: docs
                operations:
                  - search
                  - "  "
            """;

        var ex = Assert.Throws<PolicyViolationException>(() => McpConnectorRegistry.FromYaml(yaml));
        Assert.Equal("mcp-connector-registry", ex.Stage);
    }

    [Theory]
    [InlineData("Docs")]        // uppercase
    [InlineData("docs.kb")]     // dotted — would span segments
    [InlineData("docs/kb")]     // slash
    [InlineData("docs kb")]     // whitespace
    [InlineData("1docs")]       // must start with a letter
    public void Non_identifier_namespace_throws(string ns)
    {
        var yaml = $"connectors:\n  - namespace: \"{ns}\"\n    operations: [search]\n";

        Assert.Throws<PolicyViolationException>(() => McpConnectorRegistry.FromYaml(yaml));
    }

    // ---- IsAllowed: declared ns + listed op only, deny-by-default ----

    [Fact]
    public void IsAllowed_is_true_only_for_a_declared_ns_and_a_listed_op()
    {
        var reg = McpConnectorRegistry.FromYaml(ValidYaml);

        Assert.True(reg.IsAllowed("docs", "search"));
        Assert.True(reg.IsAllowed("docs", "get_page"));
    }

    [Fact]
    public void IsAllowed_is_false_for_an_unlisted_op_on_a_declared_ns()
    {
        var reg = McpConnectorRegistry.FromYaml(ValidYaml);

        // "docs" is declared but does not list "delete_page".
        Assert.False(reg.IsAllowed("docs", "delete_page"));
        // jira's "create_issue" must not be reachable through the docs namespace.
        Assert.False(reg.IsAllowed("docs", "create_issue"));
    }

    [Theory]
    [InlineData(null, "search")]
    [InlineData("docs", null)]
    [InlineData("", "search")]
    [InlineData("docs", "  ")]
    [InlineData("unknown", "search")]
    public void IsAllowed_is_deny_by_default_for_null_blank_or_unknown(string? ns, string? op)
    {
        var reg = McpConnectorRegistry.FromYaml(ValidYaml);

        Assert.False(reg.IsAllowed(ns, op));
    }

    // ---- IsWriteCapable ----

    [Fact]
    public void IsWriteCapable_reflects_the_flag_and_is_false_for_unknown()
    {
        var reg = McpConnectorRegistry.FromYaml(ValidYaml);

        Assert.True(reg.IsWriteCapable("jira"));    // write_capable: true
        Assert.False(reg.IsWriteCapable("docs"));   // write_capable: false
        Assert.False(reg.IsWriteCapable("unknown"));
        Assert.False(reg.IsWriteCapable(null));
    }

    [Fact]
    public void Write_capable_defaults_to_false_when_omitted()
    {
        const string yaml = """
            connectors:
              - namespace: docs
                operations: [search]
            """;

        var reg = McpConnectorRegistry.FromYaml(yaml);
        Assert.False(reg.IsWriteCapable("docs"));
        Assert.False(reg.Connectors[0].WriteCapable);
    }

    // ---- TryParseToolName ----

    [Fact]
    public void TryParseToolName_splits_on_the_first_dot()
    {
        Assert.True(McpConnectorRegistry.TryParseToolName("docs.search", out var ns, out var op));
        Assert.Equal("docs", ns);
        Assert.Equal("search", op);
    }

    [Fact]
    public void TryParseToolName_splits_on_the_first_dot_only()
    {
        // The namespace is a single segment; anything after the first dot is the operation.
        Assert.True(McpConnectorRegistry.TryParseToolName("docs.pages.get", out var ns, out var op));
        Assert.Equal("docs", ns);
        Assert.Equal("pages.get", op);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("search")]      // no dot
    [InlineData(".search")]     // empty namespace
    [InlineData("docs.")]       // empty operation
    [InlineData(".")]           // both empty
    public void TryParseToolName_rejects_dotless_or_empty_part_names(string? name)
    {
        Assert.False(McpConnectorRegistry.TryParseToolName(name, out var ns, out var op));
        Assert.Null(ns);
        Assert.Null(op);
    }

    // ---- TryGet surface ----

    [Fact]
    public void TryGet_returns_the_declaration_for_a_declared_ns()
    {
        var reg = McpConnectorRegistry.FromYaml(ValidYaml);

        Assert.True(reg.TryGet("jira", out var decl));
        Assert.Equal("jira", decl.Namespace);
        Assert.True(decl.WriteCapable);

        Assert.False(reg.TryGet("unknown", out _));
    }
}
