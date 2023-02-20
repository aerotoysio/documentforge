using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;

namespace DocumentForge.Engine.Linq;

/// <summary>
/// Strongly-typed access to a DocumentForge collection. Translates a focused subset of
/// LINQ expressions into SQL strings that our query engine understands.
///
/// Supported:
///   Where(o => o.Field == value)   -- equality, inequality, >, >=, <, <=, &&, ||
///   Where(o => o.Nested.Field == x) -- nested path navigation
///   OrderBy / OrderByDescending
///   Take / Skip
///   Count()
///   First() / FirstOrDefault()
///   ToList()
///
/// Things NOT supported (use db.Execute SQL directly for these):
///   Joins, GroupBy, aggregates other than Count, Contains, string methods.
/// </summary>
public sealed class LinqCollection<T> where T : class
{
    private readonly DocumentForgeDb _db;
    private readonly string _collectionName;

    public LinqCollection(DocumentForgeDb db, string collectionName)
    {
        _db = db;
        _collectionName = collectionName;
    }

    public LinqQuery<T> Where(Expression<Func<T, bool>> predicate) =>
        new LinqQuery<T>(_db, _collectionName).Where(predicate);

    public LinqQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) =>
        new LinqQuery<T>(_db, _collectionName).OrderBy(keySelector);

    public LinqQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) =>
        new LinqQuery<T>(_db, _collectionName).OrderByDescending(keySelector);

    public LinqQuery<T> Take(int n) => new LinqQuery<T>(_db, _collectionName).Take(n);
    public LinqQuery<T> Skip(int n) => new LinqQuery<T>(_db, _collectionName).Skip(n);

    public List<T> ToList() => new LinqQuery<T>(_db, _collectionName).ToList();
    public long Count() => new LinqQuery<T>(_db, _collectionName).Count();
    public T? FirstOrDefault() => new LinqQuery<T>(_db, _collectionName).FirstOrDefault();

    /// <summary>Insert a typed object. The object is serialized to JSON then stored.</summary>
    public DocumentId Insert(T item)
    {
        var json = JsonSerializer.Serialize(item);
        return _db.Insert(_collectionName, BsonDocument.FromJson(json));
    }
}

/// <summary>
/// Fluent query builder that materializes on terminal operations.
/// </summary>
public sealed class LinqQuery<T> where T : class
{
    private readonly DocumentForgeDb _db;
    private readonly string _collectionName;
    private string? _whereClause;
    private string? _orderByPath;
    private bool _orderDescending;
    private int? _limit;
    private int? _skip;

    public LinqQuery(DocumentForgeDb db, string collectionName)
    {
        _db = db;
        _collectionName = collectionName;
    }

    public LinqQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        var sql = ExpressionTranslator.Translate(predicate.Body, predicate.Parameters[0]);
        _whereClause = _whereClause is null ? sql : $"({_whereClause}) AND ({sql})";
        return this;
    }

    public LinqQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderByPath = ExpressionTranslator.ExtractPath(keySelector.Body, keySelector.Parameters[0]);
        _orderDescending = false;
        return this;
    }

    public LinqQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderByPath = ExpressionTranslator.ExtractPath(keySelector.Body, keySelector.Parameters[0]);
        _orderDescending = true;
        return this;
    }

    public LinqQuery<T> Take(int n) { _limit = n; return this; }
    public LinqQuery<T> Skip(int n) { _skip = n; return this; }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public List<T> ToList()
    {
        var result = _db.Execute(BuildSql());
        return result.Documents.Select(d => JsonSerializer.Deserialize<T>(d.ToJson(), _jsonOptions)!).ToList();
    }

    public T? FirstOrDefault()
    {
        _limit = 1;
        var list = ToList();
        return list.Count > 0 ? list[0] : null;
    }

    public long Count()
    {
        var sql = "SELECT COUNT(*) FROM " + _collectionName +
                  (_whereClause is not null ? " WHERE " + _whereClause : "");
        var r = _db.Execute(sql);
        return r.Documents.Count > 0 ? r.Documents[0]["count"].AsInt64 : 0;
    }

    private string BuildSql()
    {
        var sb = new StringBuilder("SELECT * FROM ").Append(_collectionName);
        if (_whereClause is not null) sb.Append(" WHERE ").Append(_whereClause);
        if (_orderByPath is not null) sb.Append(" ORDER BY ").Append(_orderByPath).Append(_orderDescending ? " DESC" : " ASC");
        if (_limit.HasValue) sb.Append(" LIMIT ").Append(_limit.Value);
        if (_skip.HasValue) sb.Append(" OFFSET ").Append(_skip.Value);
        return sb.ToString();
    }
}

/// <summary>
/// Walks a LINQ expression tree and produces a SQL fragment our engine understands.
/// </summary>
internal static class ExpressionTranslator
{
    public static string Translate(Expression expr, ParameterExpression param)
    {
        return expr switch
        {
            BinaryExpression b => TranslateBinary(b, param),
            UnaryExpression u when u.NodeType == ExpressionType.Not => "NOT (" + Translate(u.Operand, param) + ")",
            UnaryExpression u when u.NodeType == ExpressionType.Convert => Translate(u.Operand, param),
            MemberExpression m => ExtractPath(m, param),  // boolean field: Where(x => x.IsActive)
            _ => throw new NotSupportedException($"Unsupported expression: {expr.NodeType}")
        };
    }

    private static string TranslateBinary(BinaryExpression b, ParameterExpression param)
    {
        if (b.NodeType == ExpressionType.AndAlso)
            return "(" + Translate(b.Left, param) + ") AND (" + Translate(b.Right, param) + ")";
        if (b.NodeType == ExpressionType.OrElse)
            return "(" + Translate(b.Left, param) + ") OR (" + Translate(b.Right, param) + ")";

        var op = b.NodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.LessThan => "<",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException($"Unsupported operator: {b.NodeType}")
        };

        // Figure out which side is the column and which is the value
        var leftPath = TryExtractPath(b.Left, param);
        var rightPath = TryExtractPath(b.Right, param);

        string colPath;
        Expression valueExpr;
        if (leftPath is not null && rightPath is null) { colPath = leftPath; valueExpr = b.Right; }
        else if (rightPath is not null && leftPath is null) { colPath = rightPath; valueExpr = b.Left; op = FlipOp(op); }
        else throw new NotSupportedException("Could not determine column vs value in binary expression.");

        var value = EvaluateConstant(valueExpr);
        return $"{colPath} {op} {FormatLiteral(value)}";
    }

    private static string FlipOp(string op) => op switch
    {
        ">" => "<", "<" => ">", ">=" => "<=", "<=" => ">=", _ => op
    };

    public static string ExtractPath(Expression expr, ParameterExpression param)
    {
        var p = TryExtractPath(expr, param);
        return p ?? throw new NotSupportedException($"Cannot extract path from {expr}");
    }

    private static string? TryExtractPath(Expression expr, ParameterExpression param)
    {
        if (expr is UnaryExpression u && u.NodeType == ExpressionType.Convert)
            return TryExtractPath(u.Operand, param);
        if (expr is not MemberExpression m) return null;

        var parts = new List<string>();
        Expression? current = m;
        while (current is MemberExpression me)
        {
            parts.Add(LowerCamel(me.Member.Name));
            current = me.Expression;
        }
        if (current != param) return null;
        parts.Reverse();
        return string.Join(".", parts);
    }

    private static string LowerCamel(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    private static object? EvaluateConstant(Expression expr)
    {
        if (expr is ConstantExpression c) return c.Value;
        // Compile and evaluate - handles captured variables and member access
        var lambda = Expression.Lambda(expr);
        return lambda.Compile().DynamicInvoke();
    }

    private static string FormatLiteral(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s.Replace("'", "''")}'",
        bool b => b ? "true" : "false",
        DateTime dt => $"'{dt:yyyy-MM-ddTHH:mm:ss}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-ddTHH:mm:ss}'",
        _ => value.ToString() ?? "NULL"
    };
}
