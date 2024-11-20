using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Index;

namespace DocumentForge.Query;

/// <summary>
/// Walks a <see cref="ValueExpression"/> tree and produces a
/// <see cref="BsonValue"/>. Used by the WHERE comparison evaluator and the
/// UPDATE SET path to expand <c>NEWID()</c>, <c>LOWER(email)</c>, and
/// similar at evaluation time.
///
/// <para>
/// <paramref name="docContext"/> is the row currently being evaluated. Required
/// for <see cref="PathValueExpression"/> resolution; can be null in
/// pre-row contexts (e.g. an index probe where we just need the literal/
/// function value to look up). Passing null and then hitting a path is a
/// caller error and throws.
/// </para>
/// </summary>
public static class ValueEvaluator
{
    public static BsonValue Evaluate(ValueExpression expr, BsonDocument? docContext)
    {
        return expr switch
        {
            LiteralValueExpression lit => LiteralToBson(lit),
            PathValueExpression path => ResolvePath(path, docContext),
            FunctionCallValueExpression fn => InvokeFunction(fn, docContext),
            _ => BsonValue.Null,
        };
    }

    private static BsonValue ResolvePath(PathValueExpression path, BsonDocument? doc)
    {
        if (doc is null)
            throw new DocumentForgeException(
                $"Path '{path.Path}' cannot be resolved without a document context. " +
                "(This usually means the value appears in a position the engine couldn't bind to a row, e.g. an index probe.)");
        return JsonPathExtractor.Extract(doc, path.Path);
    }

    private static BsonValue InvokeFunction(FunctionCallValueExpression fn, BsonDocument? doc)
    {
        // Eager evaluation of args. Lazy/short-circuit semantics aren't needed
        // for the current function set — the only function with conceptually
        // lazy args (COALESCE) is small enough that evaluating all args and
        // picking the first non-null is the same answer for the same cost.
        var argValues = new BsonValue[fn.Args.Count];
        for (int i = 0; i < fn.Args.Count; i++)
            argValues[i] = Evaluate(fn.Args[i], doc);
        return ScalarFunctions.Invoke(fn.Name, argValues);
    }

    private static BsonValue LiteralToBson(LiteralValueExpression lit) => lit.ValueType switch
    {
        TokenType.StringLiteral => BsonValue.FromString((string)lit.Value!),
        TokenType.NumberLiteral => NumberLiteralToBson((double)lit.Value!),
        TokenType.True => BsonValue.FromBool(true),
        TokenType.False => BsonValue.FromBool(false),
        TokenType.Null => BsonValue.Null,
        _ => BsonValue.Null,
    };

    private static BsonValue NumberLiteralToBson(double d)
    {
        // Preserve integer-vs-float distinction at the BSON level so downstream
        // index probes don't treat 1 and 1.0 as different keys (BTreeIndex
        // hashes on the underlying type-tagged value).
        if (d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
            return BsonValue.FromInt32((int)d);
        return BsonValue.FromDouble(d);
    }
}
