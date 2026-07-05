using System.Data;
using DocumentForge.Studio.Core.Query;

namespace DocumentForge.Studio.Core.Tests;

public sealed class SqlTextTests
{
    [Theory]
    [InlineData("SELECT * FROM orders", true)]
    [InlineData("  select 1", true)]
    [InlineData("INSERT INTO x VALUES {}", false)]
    [InlineData("", false)]
    public void IsSelect_Detects_Selects(string sql, bool expected) =>
        Assert.Equal(expected, SqlText.IsSelect(sql));

    [Fact]
    public void EnsureLimit_Appends_To_Unbounded_Select()
    {
        Assert.Equal("SELECT * FROM orders LIMIT 1000", SqlText.EnsureLimit("SELECT * FROM orders", 1000));
        Assert.Equal("SELECT * FROM orders LIMIT 1000", SqlText.EnsureLimit("SELECT * FROM orders;", 1000));
        Assert.Equal("SELECT * FROM orders LIMIT 1000", SqlText.EnsureLimit("SELECT * FROM orders  \n ", 1000));
    }

    [Fact]
    public void EnsureLimit_Leaves_Existing_Limit_And_NonSelects()
    {
        Assert.Equal("SELECT * FROM orders LIMIT 5", SqlText.EnsureLimit("SELECT * FROM orders LIMIT 5", 1000));
        Assert.Equal("DELETE FROM orders", SqlText.EnsureLimit("DELETE FROM orders", 1000));
    }

    [Fact]
    public void EnsureLimit_Disabled_When_Limit_NonPositive() =>
        Assert.Equal("SELECT * FROM orders", SqlText.EnsureLimit("SELECT * FROM orders", 0));

    [Theory]
    [InlineData("SELECT * FROM x", "SELECT")]
    [InlineData("  update x set a=1", "UPDATE")]
    [InlineData("", "")]
    public void LeadingKeyword_Extracts_Verb(string sql, string expected) =>
        Assert.Equal(expected, SqlText.LeadingKeyword(sql));

    [Fact]
    public void BuildCreateIndex_Single_Composite_And_Unique()
    {
        Assert.Equal("CREATE INDEX idx_pnr ON orders(pnr)",
            SqlText.BuildCreateIndex("orders", "idx_pnr", ["pnr"], unique: false));
        Assert.Equal("CREATE UNIQUE INDEX idx_pnr ON orders(pnr)",
            SqlText.BuildCreateIndex("orders", "idx_pnr", ["pnr"], unique: true));
        Assert.Equal("CREATE INDEX idx_sc ON orders(status, createdAt)",
            SqlText.BuildCreateIndex("orders", "idx_sc", ["status", " createdAt "], unique: false));
    }

    [Fact]
    public void BuildDropIndex_And_SuggestName()
    {
        Assert.Equal("DROP INDEX idx_pnr", SqlText.BuildDropIndex("idx_pnr"));
        Assert.Equal("idx_orders_status_createdAt",
            SqlText.SuggestIndexName("orders", ["status", "createdAt"]));
        Assert.Equal("idx_flights_flightNumber",
            SqlText.SuggestIndexName("flights", ["flightNumber"]));
    }

    [Theory]
    [InlineData("SELECT * FROM orders", true, "orders")]
    [InlineData("select * from flights where seatsRemaining > 0", true, "flights")]
    [InlineData("SELECT * FROM orders o JOIN flights f ON o.x = f.y", false, "")]
    [InlineData("SELECT COUNT(*) FROM orders", true, "orders")]
    [InlineData("INSERT INTO orders VALUES {}", false, "")]
    public void TrySelectSingleCollection(string sql, bool expected, string collection)
    {
        Assert.Equal(expected, SqlText.TrySelectSingleCollection(sql, out var c));
        Assert.Equal(collection, c);
    }
}

public sealed class DocumentEditTests
{
    [Fact]
    public void ApplyCellEdit_Preserves_Field_Types()
    {
        var json = """{"pnr":"ABC","seats":3,"active":true,"_etag":"e1"}""";

        Assert.Contains("\"pnr\":\"XYZ\"", DocumentEdit.ApplyCellEdit(json, "pnr", "XYZ"));
        // number field stays a number (unquoted)
        Assert.Contains("\"seats\":5", DocumentEdit.ApplyCellEdit(json, "seats", "5"));
        // bool field stays a bool
        Assert.Contains("\"active\":false", DocumentEdit.ApplyCellEdit(json, "active", "false"));
    }

    [Fact]
    public void ApplyCellEdit_Reparses_Object_And_Array_Cells()
    {
        var json = """{"passenger":{"first":"Jane"},"tags":[1,2]}""";
        Assert.Contains("\"first\":\"Ann\"",
            DocumentEdit.ApplyCellEdit(json, "passenger", """{"first":"Ann"}"""));
        Assert.Contains("\"tags\":[3,4]",
            DocumentEdit.ApplyCellEdit(json, "tags", "[3,4]"));
    }

    [Fact]
    public void ApplyCellEdit_Rejects_Bad_Json_For_Object_Field()
    {
        var json = """{"passenger":{"first":"Jane"}}""";
        Assert.Throws<FormatException>(() => DocumentEdit.ApplyCellEdit(json, "passenger", "not json"));
    }

    [Fact]
    public void ReadField_Extracts_Id_And_Etag()
    {
        var json = """{"_id":"abc123","_etag":"e9","name":"x"}""";
        Assert.Equal("abc123", DocumentEdit.ReadField(json, "_id"));
        Assert.Equal("e9", DocumentEdit.ReadField(json, "_etag"));
        Assert.Null(DocumentEdit.ReadField(json, "missing"));
    }
}

public sealed class JsonDocumentToolsTests
{
    [Fact]
    public void IsJsonObject_Accepts_Objects_Rejects_Others()
    {
        Assert.True(JsonDocumentTools.IsJsonObject("""{"a":1}""", out _));
        Assert.False(JsonDocumentTools.IsJsonObject("[1,2]", out var arrErr));
        Assert.NotNull(arrErr);
        Assert.False(JsonDocumentTools.IsJsonObject("{bad", out var badErr));
        Assert.NotNull(badErr);
    }

    [Fact]
    public void TryPrettyPrint_Formats_Valid_And_Reports_Invalid()
    {
        Assert.True(JsonDocumentTools.TryPrettyPrint("""{"a":1}""", out var pretty, out _));
        Assert.Contains("\n", pretty);
        Assert.False(JsonDocumentTools.TryPrettyPrint("{bad", out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void TemplateFromSample_Strips_Id_And_Etag()
    {
        var template = JsonDocumentTools.TemplateFromSample("""{"_id":"x","_etag":"y","pnr":"ABC","seats":3}""");
        Assert.DoesNotContain("_id", template);
        Assert.DoesNotContain("_etag", template);
        Assert.Contains("pnr", template);
        Assert.Contains("seats", template);
    }

    [Fact]
    public void TemplateFromSample_Falls_Back_To_Empty_Object()
    {
        Assert.Contains("{", JsonDocumentTools.TemplateFromSample(null));
        Assert.Contains("{", JsonDocumentTools.TemplateFromSample("not json"));
    }
}

public sealed class SqlReferenceTests
{
    [Fact]
    public void Entries_Are_Populated_And_Well_Formed()
    {
        Assert.NotEmpty(SqlReference.Entries);
        Assert.All(SqlReference.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.False(string.IsNullOrWhiteSpace(e.Category));
            Assert.False(string.IsNullOrWhiteSpace(e.Example));
        });
    }

    [Fact]
    public void Search_Empty_Returns_All()
    {
        Assert.Equal(SqlReference.Entries.Count, SqlReference.Search("").Count);
        Assert.Equal(SqlReference.Entries.Count, SqlReference.Search(null).Count);
    }

    [Theory]
    [InlineData("join")]
    [InlineData("BETWEEN")]
    [InlineData("index")]
    public void Search_Finds_Relevant_Entries(string term)
    {
        var results = SqlReference.Search(term);
        Assert.NotEmpty(results);
        Assert.True(results.Count < SqlReference.Entries.Count);
    }

    [Fact]
    public void Search_Is_CaseInsensitive_And_Covers_Gotchas()
    {
        Assert.NotEmpty(SqlReference.Search("between"));   // the "not supported" gotcha
        Assert.Contains(SqlReference.Entries, e => e.Category == "Gotchas");
    }
}

public sealed class ResultTableTests
{
    [Fact]
    public void Build_Unions_Columns_In_First_Seen_Order()
    {
        var docs = new[]
        {
            """{"pnr":"ABC","status":"CONFIRMED"}""",
            """{"pnr":"XYZ","seats":3}""",
        };

        var table = ResultTable.Build(docs);

        Assert.Equal(new[] { "pnr", "status", "seats" }, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("ABC", table.Rows[0]["pnr"]);
        Assert.Equal(DBNull.Value, table.Rows[0]["seats"]); // missing key → empty cell
        Assert.Equal("3", table.Rows[1]["seats"]);
    }

    [Fact]
    public void Build_Renders_Nested_Objects_As_Compact_Json()
    {
        var table = ResultTable.Build([ """{"passenger":{"firstName":"Jane"},"tags":[1,2]}""" ]);

        Assert.Equal("""{"firstName":"Jane"}""", table.Rows[0]["passenger"]);
        Assert.Equal("[1,2]", table.Rows[0]["tags"]);
    }

    [Fact]
    public void Build_Handles_Non_Object_And_Malformed_Docs()
    {
        var table = ResultTable.Build([ "42", "not json" ]);
        Assert.Single(table.Columns);
        Assert.Equal("value", table.Columns[0].ColumnName);
        Assert.Equal("42", table.Rows[0]["value"]);
        Assert.Equal("not json", table.Rows[1]["value"]);
    }

    [Fact]
    public void PrettyJson_Indents_And_Notes_Truncation()
    {
        var docs = Enumerable.Range(0, 5).Select(i => $$"""{"n":{{i}}}""").ToList();

        var pretty = ResultTable.PrettyJson(docs, cap: 2);

        Assert.Contains("\"n\": 0", pretty);
        Assert.Contains("3 more document(s) not shown", pretty);
    }
}
