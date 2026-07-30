using Ts.Core.Definition;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>
/// The definition reader is only as trustworthy as the parser under it, so the subset is pinned
/// here directly rather than tested through the channel schema alone.
/// </summary>
public class YamlTests
{
    [Fact]
    public void ReadsNestedMappings()
    {
        var root = Yaml.ParseDocument("""
            outer:
              inner:
                leaf: 7
            """);

        var outer = Assert.IsType<YamlMapping>(root.Find("outer"));
        var inner = Assert.IsType<YamlMapping>(outer.Find("inner"));
        Assert.Equal("7", Assert.IsType<YamlScalar>(inner.Find("leaf")).Value);
    }

    [Fact]
    public void ReadsSequencesOfMappingsAtEitherIndentStyle()
    {
        const string indented = """
            items:
              - name: a
                value: 1
              - name: b
                value: 2
            """;

        const string flush = """
            items:
            - name: a
              value: 1
            - name: b
              value: 2
            """;

        foreach (var text in new[] { indented, flush })
        {
            var items = Assert.IsType<YamlSequence>(Yaml.ParseDocument(text).Find("items"));
            Assert.Equal(2, items.Items.Count);

            var second = Assert.IsType<YamlMapping>(items.Items[1]);
            Assert.Equal("b", Assert.IsType<YamlScalar>(second.Find("name")).Value);
            Assert.Equal("2", Assert.IsType<YamlScalar>(second.Find("value")).Value);
        }
    }

    [Fact]
    public void ReadsScalarSequences()
    {
        var items = Assert.IsType<YamlSequence>(Yaml.ParseDocument("""
            items:
              - one
              - two
            """).Find("items"));

        Assert.Equal(new[] { "one", "two" },
            items.Items.Select(i => Assert.IsType<YamlScalar>(i).Value));
    }

    [Fact]
    public void KeysAreCaseInsensitiveButOrderIsPreserved()
    {
        var root = Yaml.ParseDocument("""
            Alpha: 1
            beta: 2
            """);

        Assert.NotNull(root.Find("ALPHA"));
        Assert.Equal(new[] { "Alpha", "beta" }, root.Keys);
    }

    [Fact]
    public void StripsCommentsButNotInsideQuotes()
    {
        var root = Yaml.ParseDocument("""
            # leading comment
            plain: 5   # trailing comment
            quoted: "value # not a comment"
            """);

        Assert.Equal("5", Assert.IsType<YamlScalar>(root.Find("plain")).Value);
        Assert.Equal("value # not a comment", Assert.IsType<YamlScalar>(root.Find("quoted")).Value);
    }

    [Fact]
    public void UnescapesDoubleQuotedStrings()
    {
        var root = Yaml.ParseDocument("""
            escaped: "a\tb"
            """);

        Assert.Equal("a\tb", Assert.IsType<YamlScalar>(root.Find("escaped")).Value);
    }

    [Fact]
    public void ValuesKeepInnerColonsAndSlashes()
    {
        var root = Yaml.ParseDocument("unit: m/s");

        Assert.Equal("m/s", Assert.IsType<YamlScalar>(root.Find("unit")).Value);
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        var error = Assert.Throws<DefinitionException>(
            () => Yaml.ParseDocument("a: 1\na: 2"));

        Assert.Contains("Duplicate key", error.Message);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void RejectsTabIndentation()
    {
        var error = Assert.Throws<DefinitionException>(
            () => Yaml.ParseDocument("a:\n\tb: 1"));

        Assert.Contains("Tabs", error.Message);
    }

    [Fact]
    public void RejectsALineThatIsNotAKeyValuePair()
    {
        Assert.Throws<DefinitionException>(() => Yaml.ParseDocument("just some words"));
    }

    [Fact]
    public void EmptyDocumentIsAnEmptyMapping()
    {
        Assert.Equal(0, Yaml.ParseDocument("\n  \n# only a comment\n").Count);
    }
}
