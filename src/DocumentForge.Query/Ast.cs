namespace DocumentForge.Query;

// --- Statement types ---
public abstract class Statement { }

public sealed class SelectStatement : Statement
{
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

public sealed class JoinClause
{
    public string Collection { get; set; } = "";
    // Join condition: LeftPath (on outer collection) == RightPath (on joined collection)
    // e.g., "orders.flights[0].flightNumber" == "flights.flightNumber"
    public string LeftPath { get; set; } = "";
    public string LeftCollection { get; set; } = "";
    public string RightPath { get; set; } = "";
    public string RightCollection { get; set; } = "";
}

public sealed class InsertStatement : Statement
{
    public string Collection { get; set; } = "";
    public string JsonDocument { get; set; } = "";
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

// --- Expressions (WHERE clause) ---
public abstract class Expression { }

public sealed class ComparisonExpression : Expression
{
    public string JsonPath { get; set; } = "";
    public TokenType Operator { get; set; }
    public object? Value { get; set; }
    public TokenType ValueType { get; set; }
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
