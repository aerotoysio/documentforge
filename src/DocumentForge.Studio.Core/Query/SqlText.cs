using System.Text.RegularExpressions;

namespace DocumentForge.Studio.Core.Query;

/// <summary>Small, transport-neutral helpers for reasoning about a SQL string
/// before it's sent to the engine. Kept pure so the query workbench logic is
/// unit-testable without a UI or a live connection.</summary>
public static partial class SqlText
{
    [GeneratedRegex(@"^\s*SELECT\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelectRegex();

    [GeneratedRegex(@"\bLIMIT\b", RegexOptions.IgnoreCase)]
    private static partial Regex LimitRegex();

    [GeneratedRegex(@"\bJOIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex JoinRegex();

    [GeneratedRegex(@"^\s*SELECT\b.*?\bFROM\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SelectFromRegex();

    public static bool IsSelect(string sql) => !string.IsNullOrWhiteSpace(sql) && SelectRegex().IsMatch(sql);

    public static bool HasLimit(string sql) => LimitRegex().IsMatch(sql);

    /// <summary>The first statement's leading keyword, upper-cased (e.g. "SELECT",
    /// "INSERT"), or "" for an empty string. Used to label results.</summary>
    public static string LeadingKeyword(string sql)
    {
        var trimmed = sql.TrimStart();
        var end = 0;
        while (end < trimmed.Length && (char.IsLetter(trimmed[end]))) end++;
        return trimmed[..end].ToUpperInvariant();
    }

    /// <summary>Appends a LIMIT clause to an unbounded SELECT so the workbench
    /// never tries to pull a whole collection into a grid. No-op for non-SELECTs,
    /// or for a SELECT that already has its own LIMIT.</summary>
    public static string EnsureLimit(string sql, int limit)
    {
        if (limit <= 0 || !IsSelect(sql) || HasLimit(sql)) return sql;
        var trimmed = sql.TrimEnd().TrimEnd(';').TrimEnd();
        return $"{trimmed} LIMIT {limit}";
    }

    /// <summary>Builds a CREATE INDEX statement. Multiple paths produce a
    /// composite index. Runs identically over HTTP (/db/{name}/query) and a
    /// direct-file connection, so index DDL needs no transport-specific code.</summary>
    public static string BuildCreateIndex(string collection, string indexName, IEnumerable<string> paths, bool unique)
    {
        var pathList = string.Join(", ", paths.Select(p => p.Trim()).Where(p => p.Length > 0));
        var uniqueKeyword = unique ? "UNIQUE " : "";
        return $"CREATE {uniqueKeyword}INDEX {indexName} ON {collection}({pathList})";
    }

    public static string BuildDropIndex(string indexName) => $"DROP INDEX {indexName}";

    /// <summary>True when a SELECT targets exactly one collection (no JOIN), so
    /// its rows can be written back by <c>_id</c>. This is a necessary condition
    /// for an editable grid; the caller still checks that rows actually carry
    /// <c>_id</c>/<c>_etag</c> (aggregates/projections won't).</summary>
    public static bool TrySelectSingleCollection(string sql, out string collection)
    {
        collection = "";
        if (!IsSelect(sql) || JoinRegex().IsMatch(sql)) return false;
        var match = SelectFromRegex().Match(sql);
        if (!match.Success) return false;
        collection = match.Groups[1].Value;
        return true;
    }

    /// <summary>A reasonable default index name from a collection + its paths,
    /// e.g. idx_orders_status_createdAt. Callers can override.</summary>
    public static string SuggestIndexName(string collection, IEnumerable<string> paths)
    {
        var suffix = string.Join("_", paths
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(Sanitize));
        return $"idx_{Sanitize(collection)}_{suffix}".TrimEnd('_');
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
