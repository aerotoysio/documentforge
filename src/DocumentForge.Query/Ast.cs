namespace DocumentForge.Query;

// --- Statement types ---
public abstract class Statement { }

public sealed class SelectStatement : Statement
{
    public bool IsDistinct { get; set; }
    public List<string> Fields { get; set; } = new();  // ["*"] for all
    public List<AggregateField> Aggregates { get; set; } = new();
    public List<string> GroupByPaths { get; set; } = new();
    public string Collection { get; set; } = "";
    public JoinClause? Join { get; set; }
    public Expression? Where { get; set; }
    public string? OrderByPath { get; set; }
    public bool OrderDescending { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }

    public bool HasAggregates => Aggregates.Count > 0;
}

public enum AggregateFunction { Count, Sum, Avg, Min, Max }

public sealed class AggregateField
{
    public AggregateFunction Function { get; set; }
    public string Path { get; set; } = ""; // "*" for COUNT(*)
    public string Alias { get; set; } = ""; // output key in result
}

/// <summary>
/// Join algebra. <see cref="Inner"/> emits only matched pairs; <see cref="Left"/>
/// and <see cref="Right"/> null-pad the unmatched side; <see cref="Cross"/> is the
/// cartesian product (no ON clause). Issue #17 Phase A.
/// </summary>
public enum JoinType { Inner, Left, Right, Cross }

public sealed class JoinClause
{
    public string Collection { get; set; } = "";
    // Join condition: LeftPath (on outer collection) == RightPath (on joined collection)
    // e.g., "orders.flights[0].flightNumber" == "flights.flightNumber"
    public string LeftPath { get; set; } = "";
    public string LeftCollection { get; set; } = "";
    public string RightPath { get; set; } = "";
    public string RightCollection { get; set; } = "";

    /// <summary>Inner by default — preserves the pre-#17 behaviour for callers
    /// that just write <c>JOIN</c>. CROSS is the only type that doesn't carry
    /// LeftPath/RightPath (no ON clause).</summary>
    public JoinType Type { get; set; } = JoinType.Inner;
}

public sealed class InsertStatement : Statement
{
    public string Collection { get; set; } = "";

    /// <summary>
    /// Classic JSON form: <c>INSERT INTO users VALUES { "email": "..." }</c>.
    /// When <see cref="Columns"/> is non-empty, this is empty and the
    /// SQL-tuple form is used instead.
    /// </summary>
    public string JsonDocument { get; set; } = "";

    /// <summary>
    /// SQL-tuple form: <c>INSERT INTO users (email, _id) VALUES ('a@b.com', NEWID())</c>.
    /// <see cref="Columns"/> and <see cref="Values"/> are populated together
    /// and have the same length; values can be literals, paths, or function
    /// calls (composing with the #16 ValueExpression hierarchy).
    /// </summary>
    public List<string> Columns { get; set; } = new();
    public List<ValueExpression> Values { get; set; } = new();
}

public sealed class UpdateStatement : Statement
{
    public string Collection { get; set; } = "";
    public List<SetClause> SetClauses { get; set; } = new();
    public Expression? Where { get; set; }
}

public sealed class SetClause
{
    public string Path { get; set; } = "";
    public object? Value { get; set; }
    public TokenType ValueType { get; set; }
}

public sealed class DeleteStatement : Statement
{
    public string Collection { get; set; } = "";
    public Expression? Where { get; set; }
}

public sealed class CreateIndexStatement : Statement
{
    public string IndexName { get; set; } = "";
    public string Collection { get; set; } = "";
    public string JsonPath { get; set; } = "";
    public bool IsUnique { get; set; }
}

public sealed class DropIndexStatement : Statement
{
    public string IndexName { get; set; } = "";
    public string Collection { get; set; } = "";
}

public sealed class CountStatement : Statement
{
    public string Collection { get; set; } = "";
    public Expression? Where { get; set; }
}

// --- Value expressions (right-hand side of comparisons, UPDATE SET, etc.) ---
//
// Used to be represented inline as `object? Value + TokenType ValueType`; the
// abstraction makes room for scalar function calls (`NEWID()`, `LOWER(email)`)
// and document-relative paths without re-typing every call site. Existing
// callers store one of these in the legacy Value field and the evaluator
// switches on the concrete type.
public abstract class ValueExpression { }

/// <summary>A SQL literal: string, number, true/false, null.</summary>
public sealed class LiteralValueExpression : ValueExpression
{
    public object? Value { get; init; }
    public TokenType ValueType { get; init; }
}

/// <summary>
/// A document-relative path (e.g. <c>email</c>, <c>passenger.lastName</c>).
/// Resolved against the current row by the evaluator.
/// </summary>
public sealed class PathValueExpression : ValueExpression
{
    public string Path { get; init; } = "";
}

/// <summary>
/// A scalar function call: <c>NEWID()</c>, <c>GETDATE()</c>, <c>LOWER(email)</c>,
/// <c>COALESCE(nickname, name)</c>. Args can themselves be values, paths, or
/// nested function calls — composition is just recursive evaluation.
/// </summary>
public sealed class FunctionCallValueExpression : ValueExpression
{
    public string Name { get; init; } = "";
    public List<ValueExpression> Args { get; init; } = new();
}

// --- Expressions (WHERE clause) ---
public abstract class Expression { }

public sealed class ComparisonExpression : Expression
{
    public string JsonPath { get; set; } = "";
    public TokenType Operator { get; set; }
    public object? Value { get; set; }
    public TokenType ValueType { get; set; }

    /// <summary>
    /// When non-null, the comparison's LHS is computed from this expression
    /// against the current row instead of via <see cref="JsonPath"/>. Used for
    /// `WHERE LOWER(email) = 'alice'` and similar function-on-LHS shapes.
    /// JsonPath stays empty (or holds the underlying path of a single
    /// PathValueExpression) — index probes that need a literal field name
    /// fall back to a collection scan when this is set.
    /// </summary>
    public ValueExpression? LhsExpression { get; set; }
}

public sealed class LogicalExpression : Expression
{
    public Expression Left { get; set; } = null!;
    public TokenType Operator { get; set; } // And / Or
    public Expression Right { get; set; } = null!;
}

public sealed class NotExpression : Expression
{
    public Expression Inner { get; set; } = null!;
}

public sealed class IsNullExpression : Expression
{
    public string JsonPath { get; set; } = "";
    public bool IsNotNull { get; set; }
}
