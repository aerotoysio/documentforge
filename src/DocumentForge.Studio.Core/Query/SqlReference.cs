namespace DocumentForge.Studio.Core.Query;

/// <summary>One entry in the DocumentForge SQL cheat-sheet.</summary>
public sealed record SqlReferenceEntry(
    string Category,
    string Title,
    string Syntax,
    string Example,
    string? Notes = null);

/// <summary>A curated, searchable reference for DocumentForge's SQL dialect.
/// Content is accurate to what the engine actually supports (including the
/// gotchas), so it doubles as a "what works" guide. Pure/data-only.</summary>
public static class SqlReference
{
    public static IReadOnlyList<SqlReferenceEntry> Entries { get; } = new SqlReferenceEntry[]
    {
        // --- Query ---
        new("Query", "SELECT",
            "SELECT <cols|*> FROM <collection> [WHERE ...] [ORDER BY ...] [LIMIT n] [OFFSET n]",
            "SELECT * FROM orders WHERE status = 'CONFIRMED' ORDER BY pnr LIMIT 100"),
        new("Query", "SELECT specific fields",
            "SELECT field1, nested.field2 FROM <collection>",
            "SELECT pnr, passenger.lastName FROM orders"),
        new("Query", "DISTINCT",
            "SELECT DISTINCT <field> FROM <collection>",
            "SELECT DISTINCT status FROM orders"),
        new("Query", "LIMIT / OFFSET (paging)",
            "... LIMIT <n> OFFSET <m>",
            "SELECT * FROM orders ORDER BY pnr LIMIT 20 OFFSET 40",
            "OFFSET scans skipped rows, so deep paging is O(n)."),
        new("Query", "ORDER BY",
            "... ORDER BY <field> [ASC|DESC]",
            "SELECT * FROM flights ORDER BY seatsRemaining DESC",
            "Single sort key; sorts in memory."),

        // --- Filtering ---
        new("Filtering", "Comparison operators",
            "WHERE <field> <op> <value>   (= != > < >= <=)",
            "SELECT * FROM flights WHERE seatsRemaining > 0"),
        new("Filtering", "AND / OR / NOT",
            "WHERE <cond> AND <cond> OR NOT <cond>",
            "SELECT * FROM orders WHERE status = 'HELD' AND passenger.lastName = 'Smith'"),
        new("Filtering", "IN list",
            "WHERE <field> IN (v1, v2, ...)",
            "SELECT * FROM orders WHERE status IN ('HELD', 'CONFIRMED')",
            "Lowered to OR internally."),
        new("Filtering", "IS NULL / IS NOT NULL",
            "WHERE <field> IS [NOT] NULL",
            "SELECT * FROM orders WHERE cancelledAt IS NULL"),
        new("Filtering", "LIKE (pattern match)",
            "WHERE <field> LIKE '<pattern>'   (% = any, _ = one char)",
            "SELECT * FROM orders WHERE pnr LIKE 'AB%'"),

        // --- Joins ---
        new("Joins", "INNER JOIN",
            "SELECT ... FROM a JOIN b ON a.key = b.key",
            "SELECT o.pnr, f.from, f.to FROM orders o JOIN flights f ON o.flightNumber = f.flightNumber"),
        new("Joins", "LEFT / RIGHT / CROSS JOIN",
            "FROM a LEFT|RIGHT [OUTER] JOIN b ON ...   |   FROM a CROSS JOIN b",
            "SELECT * FROM orders o LEFT JOIN flights f ON o.flightNumber = f.flightNumber",
            "Left-deep chains, hash joins."),

        // --- Aggregation ---
        new("Aggregation", "COUNT",
            "SELECT COUNT(*) FROM <collection> [WHERE ...]",
            "SELECT COUNT(*) FROM orders WHERE status = 'CONFIRMED'"),
        new("Aggregation", "SUM / AVG / MIN / MAX",
            "SELECT SUM(<path>), AVG(<path>) FROM <collection>",
            "SELECT AVG(seatsRemaining) FROM flights"),
        new("Aggregation", "GROUP BY",
            "SELECT <field>, COUNT(*) FROM <collection> GROUP BY <field>",
            "SELECT status, COUNT(*) FROM orders GROUP BY status"),
        new("Aggregation", "COUNT(DISTINCT ...)",
            "SELECT COUNT(DISTINCT <path>) FROM <collection>",
            "SELECT COUNT(DISTINCT passenger.lastName) FROM orders"),

        // --- Modifying data ---
        new("Modifying data", "INSERT (JSON form)",
            "INSERT INTO <collection> VALUES { ...json... }",
            "INSERT INTO orders VALUES { \"pnr\": \"ABC123\", \"status\": \"HELD\" }",
            "_id and _etag are auto-assigned."),
        new("Modifying data", "INSERT (column form)",
            "INSERT INTO <collection> (c1, c2) VALUES (v1, v2)",
            "INSERT INTO orders (pnr, status) VALUES ('ABC123', 'HELD')"),
        new("Modifying data", "UPDATE",
            "UPDATE <collection> SET <path> = <value> [WHERE ...]",
            "UPDATE flights SET seatsRemaining = 0 WHERE flightNumber = 'AA100'",
            "Last-write-wins; SQL UPDATE is not ETag-guarded."),
        new("Modifying data", "DELETE",
            "DELETE FROM <collection> [WHERE ...]",
            "DELETE FROM orders WHERE status = 'EXPIRED'"),

        // --- Indexes ---
        new("Indexes", "CREATE INDEX",
            "CREATE [UNIQUE] INDEX <name> ON <collection>(<path>[, <path2>])",
            "CREATE UNIQUE INDEX idx_pnr ON orders(pnr)",
            "Multiple paths = composite (equality-prefix)."),
        new("Indexes", "DROP INDEX",
            "DROP INDEX <name>",
            "DROP INDEX idx_pnr"),
        new("Indexes", "Array (multikey) index",
            "CREATE INDEX <name> ON <collection>(<arrayPath>[*].<field>)",
            "CREATE INDEX idx_leg ON orders(flights[*].flightNumber)",
            "Indexes each array element — good for multi-leg PNRs."),

        // --- Functions ---
        new("Functions", "NEWID / GETDATE / NOW",
            "NEWID()   GETDATE()   NOW()",
            "UPDATE orders SET updatedAt = GETDATE() WHERE pnr = 'ABC123'"),
        new("Functions", "String functions",
            "LOWER(x)  UPPER(x)  LEN(x)  SUBSTRING(x, start, length)",
            "UPDATE orders SET pnr = UPPER(pnr)"),

        // --- JSON paths ---
        new("JSON paths", "Nested + array access",
            "dot notation: a.b.c   |   arrays: items[0], items[1].price",
            "SELECT flights[0].from, passenger.lastName FROM orders"),

        // --- Gotchas ---
        new("Gotchas", "BETWEEN is NOT supported",
            "Use >= AND <= instead of BETWEEN",
            "SELECT * FROM flights WHERE seatsRemaining >= 1 AND seatsRemaining <= 9",
            "BETWEEN parses to an error in the current engine."),
        new("Gotchas", "No HAVING / subqueries / UNION",
            "Not supported — filter in the app or restructure the query",
            "-- SELECT ... GROUP BY x HAVING COUNT(*) > 1   (not supported)"),
        new("Gotchas", "String literals",
            "Single-quote strings; double a quote to escape it",
            "SELECT * FROM passengers WHERE lastName = 'O''Brien'"),
    };

    /// <summary>Case-insensitive search across title, category, syntax, and
    /// example. Empty query returns everything.</summary>
    public static IReadOnlyList<SqlReferenceEntry> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Entries;
        var q = query.Trim();
        return Entries.Where(e =>
            Contains(e.Title, q) || Contains(e.Category, q) ||
            Contains(e.Syntax, q) || Contains(e.Example, q) ||
            (e.Notes is not null && Contains(e.Notes, q))).ToList();
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
