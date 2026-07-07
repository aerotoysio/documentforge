using DocumentForge.Studio.Core.Query;

namespace DocumentForge.Studio.Core.Tests;

public sealed class SqlAutocompleteTests
{
    [Theory]
    [InlineData("SELECT * FROM ord", 17, 14, "ord")]
    [InlineData("SELECT ", 7, 7, "")]
    [InlineData("WHERE passenger.la", 18, 6, "passenger.la")]
    [InlineData("", 0, 0, "")]
    public void CurrentWord_Finds_Identifier_At_Caret(string text, int caret, int expectedStart, string expectedPrefix)
    {
        var (start, prefix) = SqlAutocomplete.CurrentWord(text, caret);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedPrefix, prefix);
    }

    [Theory]
    [InlineData("SELECT * FROM ", true)]
    [InlineData("select * from ", true)]
    [InlineData("INSERT INTO ", true)]
    [InlineData("UPDATE ", true)]
    [InlineData("SELECT o.pnr FROM orders o JOIN ", true)]
    [InlineData("SELECT ", false)]
    [InlineData("WHERE status = ", false)]
    [InlineData("", false)]
    public void ExpectsCollection_After_Table_Introducers(string text, bool expected) =>
        Assert.Equal(expected, SqlAutocomplete.ExpectsCollection(text, text.Length));

    [Theory]
    [InlineData("SELECT * FROM orders WHERE ", "orders")]
    [InlineData("INSERT INTO flights VALUES {}", "flights")]
    [InlineData("update orders set x = 1", "orders")]
    [InlineData("SELECT * FROM ", null)]
    [InlineData("SELECT 1", null)]
    public void TargetCollection_Reads_The_Statement(string text, string? expected) =>
        Assert.Equal(expected, SqlAutocomplete.TargetCollection(text));

    [Fact]
    public void FlattenFields_Walks_Nested_Objects_And_Object_Arrays()
    {
        const string sample =
            """
            {"pnr":"ABC123","passenger":{"lastName":"Smith","contact":{"email":"x@y"}},
             "flights":[{"flightNumber":"AA1"}],"tags":["a","b"]}
            """;

        var fields = SqlAutocomplete.FlattenFields(sample);

        Assert.Contains("pnr", fields);
        Assert.Contains("passenger", fields);
        Assert.Contains("passenger.lastName", fields);
        Assert.Contains("passenger.contact.email", fields);
        Assert.Contains("flights", fields);
        Assert.Contains("flights[0].flightNumber", fields);
        Assert.Contains("tags", fields);          // scalar array: the path itself only
        Assert.DoesNotContain("tags[0]", fields);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void FlattenFields_Degrades_To_Empty(string? sample) =>
        Assert.Empty(SqlAutocomplete.FlattenFields(sample));

    [Fact]
    public void GetCompletions_After_FROM_Offers_Collections_Only()
    {
        var items = SqlAutocomplete.GetCompletions("SELECT * FROM ", 14,
            collections: new[] { "orders", "flights" }, fields: new[] { "pnr" });

        Assert.All(items, i => Assert.Equal(SqlCompletionKind.Collection, i.Kind));
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void GetCompletions_Filters_By_Typed_Prefix()
    {
        var items = SqlAutocomplete.GetCompletions("SELECT * FROM ord", 17,
            collections: new[] { "orders", "flights" }, fields: Array.Empty<string>());

        var item = Assert.Single(items);
        Assert.Equal("orders", item.Text);
    }

    [Fact]
    public void GetCompletions_Elsewhere_Mixes_Keywords_Functions_Fields()
    {
        var items = SqlAutocomplete.GetCompletions("SELECT * FROM orders WHERE p", 28,
            collections: new[] { "orders" }, fields: new[] { "pnr", "passenger.lastName" });

        Assert.Contains(items, i => i is { Text: "pnr", Kind: SqlCompletionKind.Field });
        Assert.Contains(items, i => i is { Text: "passenger.lastName", Kind: SqlCompletionKind.Field });
        Assert.DoesNotContain(items, i => i.Kind == SqlCompletionKind.Keyword && !i.Text.StartsWith('P') && !i.Text.StartsWith('p'));
    }

    [Fact]
    public void GetCompletions_Never_Offers_Unsupported_Keywords()
    {
        // BETWEEN and HAVING parse to errors in the engine — the dialect list
        // must not offer them (SqlReference documents this gotcha).
        Assert.DoesNotContain("BETWEEN", SqlAutocomplete.Keywords);
        Assert.DoesNotContain("HAVING", SqlAutocomplete.Keywords);
    }
}
