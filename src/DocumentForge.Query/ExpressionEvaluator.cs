using DocumentForge.Document;
using DocumentForge.Index;

namespace DocumentForge.Query;

/// <summary>
/// Evaluates WHERE clause expressions against a BsonDocument.
/// </summary>
public static class ExpressionEvaluator
{
    public static bool Evaluate(Expression expr, BsonDocument doc)
    {
        return expr switch
        {
            ComparisonExpression comp => EvaluateComparison(comp, doc),
            LogicalExpression logical => EvaluateLogical(logical, doc),
            NotExpression not => !Evaluate(not.Inner, doc),
            IsNullExpression isNull => EvaluateIsNull(isNull, doc),
            _ => false
        };
    }

    private static bool EvaluateComparison(ComparisonExpression expr, BsonDocument doc)
    {
        var docValue = JsonPathExtractor.Extract(doc, expr.JsonPath);
        if (docValue.IsNull && expr.Value is not null)
            return false;

        var compareValue = ConvertToComparable(expr.Value, expr.ValueType);
        int cmp;

        if (expr.Operator == TokenType.Like)
        {
            return EvaluateLike(docValue, expr.Value?.ToString() ?? "");
        }

        // Type-aware comparison
        if (docValue.IsNumeric && expr.ValueType == TokenType.NumberLiteral)
        {
            cmp = docValue.ToDouble().CompareTo((double)expr.Value!);
        }
        else if (docValue.Type == BsonType.String && expr.ValueType == TokenType.StringLiteral)
        {
            cmp = string.Compare(docValue.AsString, (string)expr.Value!, StringComparison.Ordinal);
        }
        else if (docValue.Type == BsonType.Boolean && expr.ValueType is TokenType.True or TokenType.False)
        {
            cmp = docValue.AsBoolean.CompareTo((bool)expr.Value!);
        }
        else if (docValue.Type == BsonType.DateTime && expr.ValueType == TokenType.StringLiteral)
        {
            // Try to parse the string as datetime for comparison
            if (DateTimeOffset.TryParse((string)expr.Value!, out var dt))
                cmp = docValue.AsDateTime.CompareTo(dt);
            else
                cmp = string.Compare(docValue.AsDateTime.ToString("O"), (string)expr.Value!, StringComparison.Ordinal);
        }
        else
        {
            // Fallback: string comparison
            cmp = string.Compare(docValue.ToString(), expr.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }

        return expr.Operator switch
        {
            TokenType.Equals => cmp == 0,
            TokenType.NotEquals => cmp != 0,
            TokenType.GreaterThan => cmp > 0,
            TokenType.LessThan => cmp < 0,
            TokenType.GreaterThanOrEqual => cmp >= 0,
            TokenType.LessThanOrEqual => cmp <= 0,
            _ => false
        };
    }

    private static bool EvaluateLogical(LogicalExpression expr, BsonDocument doc)
    {
        var left = Evaluate(expr.Left, doc);
        return expr.Operator switch
        {
            TokenType.And => left && Evaluate(expr.Right, doc),
            TokenType.Or => left || Evaluate(expr.Right, doc),
            _ => false
        };
    }

    private static bool EvaluateIsNull(IsNullExpression expr, BsonDocument doc)
    {
        var value = JsonPathExtractor.Extract(doc, expr.JsonPath);
        return expr.IsNotNull ? !value.IsNull : value.IsNull;
    }

    private static bool EvaluateLike(BsonValue docValue, string pattern)
    {
        if (docValue.Type != BsonType.String) return false;
        var str = docValue.AsString;

        // Simple LIKE: % = any string, _ = any single char
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(str, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static IComparable? ConvertToComparable(object? value, TokenType type)
    {
        return value as IComparable;
    }
}
