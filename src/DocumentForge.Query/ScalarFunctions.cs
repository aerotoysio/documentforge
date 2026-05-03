using DocumentForge.Document;
using DocumentForge.Index;

namespace DocumentForge.Query;

/// <summary>
/// Built-in scalar SQL functions: <c>NEWID()</c>, <c>GETDATE()</c>,
/// <c>LOWER(x)</c>, <c>COALESCE(...)</c>, etc.
///
/// <para>
/// Functions are dispatched by case-insensitive name. Unknown names throw
/// <see cref="DocumentForge.Core.QueryParseException"/>-equivalent at evaluation
/// time so a typo'd <c>NWID()</c> doesn't silently return null. Common SQL
/// dialect aliases (NOW = GETDATE, IFNULL = COALESCE, UUID = NEWID) are
/// registered as separate entries pointing at the same implementation —
/// cheaper than aliasing logic and keeps the dispatch table self-documenting.
/// </para>
///
/// <para>
/// Args are pre-evaluated to <see cref="BsonValue"/> by the caller. NULL
/// propagation rules follow standard SQL: most functions return NULL on a
/// NULL argument; <see cref="Coalesce"/> is the deliberate exception (its
/// whole job is to skip NULLs).
/// </para>
/// </summary>
internal static class ScalarFunctions
{
    private static readonly Dictionary<string, Func<IReadOnlyList<BsonValue>, BsonValue>> Registry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ID generation — single fresh GUID per call.
            ["NEWID"] = NewId,
            ["UUID"] = NewId,
            ["GENERATE_UUID"] = NewId,

            // Server-clock timestamps.
            ["GETDATE"] = Now,
            ["NOW"] = Now,
            ["CURRENT_TIMESTAMP"] = Now,

            // String transforms.
            ["LOWER"] = Lower,
            ["UPPER"] = Upper,
            ["LENGTH"] = Length,
            ["LEN"] = Length, // T-SQL alias
            ["TRIM"] = Trim,

            // Null coalescing.
            ["COALESCE"] = Coalesce,
            ["IFNULL"] = Coalesce,
            ["ISNULL"] = Coalesce, // T-SQL spelling; same semantics here
        };

    public static bool IsKnown(string name) => Registry.ContainsKey(name);

    public static BsonValue Invoke(string name, IReadOnlyList<BsonValue> args)
    {
        if (!Registry.TryGetValue(name, out var fn))
            throw new DocumentForge.Core.DocumentForgeException(
                $"Unknown scalar function '{name}'. Known functions: " +
                string.Join(", ", Registry.Keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k)) + ".");
        return fn(args);
    }

    private static BsonValue NewId(IReadOnlyList<BsonValue> args)
    {
        ExpectArity("NEWID", args, expected: 0);
        return BsonValue.FromString(Guid.NewGuid().ToString("D"));
    }

    private static BsonValue Now(IReadOnlyList<BsonValue> args)
    {
        // NOW() / GETDATE() take no args. Allow zero args; reject any.
        // (CURRENT_TIMESTAMP without parens is a SQL-92 special form; we
        // require parens for consistency with the function-call grammar.)
        ExpectArity("NOW/GETDATE/CURRENT_TIMESTAMP", args, expected: 0);
        return BsonValue.FromDateTime(DateTime.UtcNow);
    }

    private static BsonValue Lower(IReadOnlyList<BsonValue> args)
    {
        ExpectArity("LOWER", args, expected: 1);
        if (args[0].IsNull) return BsonValue.Null;
        return BsonValue.FromString(args[0].ToString().ToLowerInvariant());
    }

    private static BsonValue Upper(IReadOnlyList<BsonValue> args)
    {
        ExpectArity("UPPER", args, expected: 1);
        if (args[0].IsNull) return BsonValue.Null;
        return BsonValue.FromString(args[0].ToString().ToUpperInvariant());
    }

    private static BsonValue Length(IReadOnlyList<BsonValue> args)
    {
        ExpectArity("LENGTH", args, expected: 1);
        if (args[0].IsNull) return BsonValue.Null;
        // Code-point-wise count of the string form. For arrays this returns
        // the JSON string length, not the element count — keep that simple
        // for now; ARRAY_LENGTH can be a separate function later.
        return BsonValue.FromInt32(args[0].ToString().Length);
    }

    private static BsonValue Trim(IReadOnlyList<BsonValue> args)
    {
        ExpectArity("TRIM", args, expected: 1);
        if (args[0].IsNull) return BsonValue.Null;
        return BsonValue.FromString(args[0].ToString().Trim());
    }

    private static BsonValue Coalesce(IReadOnlyList<BsonValue> args)
    {
        if (args.Count == 0)
            throw new DocumentForge.Core.DocumentForgeException(
                "COALESCE / IFNULL requires at least one argument.");
        foreach (var a in args)
            if (!a.IsNull) return a;
        return BsonValue.Null;
    }

    private static void ExpectArity(string fn, IReadOnlyList<BsonValue> args, int expected)
    {
        if (args.Count != expected)
            throw new DocumentForge.Core.DocumentForgeException(
                $"{fn} expects {expected} argument(s), got {args.Count}.");
    }
}
