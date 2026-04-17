using DocumentForge.Core;

namespace DocumentForge.Query;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public Parser(List<Token> tokens)
    {
        _tokens = tokens;
    }

    private Token Current => _tokens[_pos];
    private Token Peek(int offset = 1) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

    private Token Expect(TokenType type)
    {
        if (Current.Type != type)
            throw new QueryParseException($"Expected {type}, got {Current.Type} ('{Current.Value}')", Current.Position);
        var token = Current;
        _pos++;
        return token;
    }

    private bool Match(TokenType type)
    {
        if (Current.Type == type) { _pos++; return true; }
        return false;
    }

    public Statement Parse()
    {
        return Current.Type switch
        {
            TokenType.Select => ParseSelect(),
            TokenType.Insert => ParseInsert(),
            TokenType.Update => ParseUpdate(),
            TokenType.Delete => ParseDelete(),
            TokenType.Create => ParseCreate(),
            TokenType.Drop => ParseDrop(),
            _ => throw new QueryParseException($"Unexpected token: {Current.Type}", Current.Position)
        };
    }

    private Statement ParseSelect()
    {
        Expect(TokenType.Select);

        // Check for COUNT(*)
        if (Current.Type == TokenType.Count)
        {
            _pos++;
            Expect(TokenType.LeftParen);
            Expect(TokenType.Star);
            Expect(TokenType.RightParen);
            Expect(TokenType.From);
            var countCollection = ReadIdentifierPath();
            Expression? countWhere = null;
            if (Current.Type == TokenType.Where)
            {
                _pos++;
                countWhere = ParseExpression();
            }
            return new CountStatement { Collection = countCollection, Where = countWhere };
        }

        var stmt = new SelectStatement();

        // Fields - can be *, paths, or aggregate functions like SUM(path), COUNT(*)
        if (Current.Type == TokenType.Star)
        {
            stmt.Fields.Add("*");
            _pos++;
        }
        else
        {
            ParseSelectItem(stmt);
            while (Match(TokenType.Comma))
                ParseSelectItem(stmt);
        }

        Expect(TokenType.From);
        stmt.Collection = ReadIdentifierPath();

        // JOIN ... ON leftPath = rightPath
        if (Current.Type == TokenType.Join)
        {
            _pos++;
            var joinCollection = ReadIdentifierPath();
            Expect(TokenType.On);

            // Parse left side: must be prefixed with collection name (e.g., orders.flights[0].flightNumber)
            var leftFullPath = ReadIdentifierPath();
            Expect(TokenType.Equals);
            var rightFullPath = ReadIdentifierPath();

            // Split the prefix.path notation
            var (leftColl, leftPath) = SplitCollectionPath(leftFullPath, stmt.Collection, joinCollection);
            var (rightColl, rightPath) = SplitCollectionPath(rightFullPath, stmt.Collection, joinCollection);

            stmt.Join = new JoinClause
            {
                Collection = joinCollection,
                LeftCollection = leftColl,
                LeftPath = leftPath,
                RightCollection = rightColl,
                RightPath = rightPath
            };
        }

        // WHERE
        if (Current.Type == TokenType.Where)
        {
            _pos++;
            stmt.Where = ParseExpression();
        }

        // GROUP BY
        if (Current.Type == TokenType.Group)
        {
            _pos++;
            stmt.GroupByPaths.Add(ReadIdentifierPath());
            while (Match(TokenType.Comma))
                stmt.GroupByPaths.Add(ReadIdentifierPath());
        }

        // ORDER BY
        if (Current.Type == TokenType.OrderBy)
        {
            _pos++;
            stmt.OrderByPath = ReadIdentifierPath();
            if (Current.Type == TokenType.Desc) { stmt.OrderDescending = true; _pos++; }
            else if (Current.Type == TokenType.Asc) { _pos++; }
        }

        // LIMIT
        if (Current.Type == TokenType.Limit)
        {
            _pos++;
            stmt.Limit = int.Parse(Expect(TokenType.NumberLiteral).Value);
        }

        // OFFSET
        if (Current.Type == TokenType.Offset)
        {
            _pos++;
            stmt.Offset = int.Parse(Expect(TokenType.NumberLiteral).Value);
        }

        return stmt;
    }

    private InsertStatement ParseInsert()
    {
        Expect(TokenType.Insert);
        Expect(TokenType.Into);
        var collection = ReadIdentifierPath();

        // Optional VALUES keyword
        if (Current.Type == TokenType.Values)
            _pos++;

        var json = Expect(TokenType.JsonLiteral).Value;
        return new InsertStatement { Collection = collection, JsonDocument = json };
    }

    private UpdateStatement ParseUpdate()
    {
        Expect(TokenType.Update);
        var collection = ReadIdentifierPath();
        Expect(TokenType.Set);

        var stmt = new UpdateStatement { Collection = collection };

        do
        {
            var path = ReadIdentifierPath();
            Expect(TokenType.Equals);
            var (value, valueType) = ReadLiteralValue();
            stmt.SetClauses.Add(new SetClause { Path = path, Value = value, ValueType = valueType });
        } while (Match(TokenType.Comma));

        if (Current.Type == TokenType.Where)
        {
            _pos++;
            stmt.Where = ParseExpression();
        }

        return stmt;
    }

    private DeleteStatement ParseDelete()
    {
        Expect(TokenType.Delete);
        Expect(TokenType.From);
        var collection = ReadIdentifierPath();

        Expression? where = null;
        if (Current.Type == TokenType.Where)
        {
            _pos++;
            where = ParseExpression();
        }

        return new DeleteStatement { Collection = collection, Where = where };
    }

    private Statement ParseCreate()
    {
        Expect(TokenType.Create);
        bool isUnique = Match(TokenType.Unique);
        Expect(TokenType.Index);

        var indexName = Expect(TokenType.Identifier).Value;
        Expect(TokenType.On);
        var collection = ReadIdentifierPath();
        Expect(TokenType.LeftParen);

        // Support composite indexes: (path1, path2, ...)
        var paths = new List<string> { ReadIdentifierPath() };
        while (Match(TokenType.Comma))
            paths.Add(ReadIdentifierPath());

        Expect(TokenType.RightParen);
        var jsonPath = string.Join(",", paths);

        return new CreateIndexStatement
        {
            IndexName = indexName,
            Collection = collection,
            JsonPath = jsonPath,
            IsUnique = isUnique
        };
    }

    private Statement ParseDrop()
    {
        Expect(TokenType.Drop);
        Expect(TokenType.Index);
        var indexName = Expect(TokenType.Identifier).Value;
        Expect(TokenType.On);
        var collection = ReadIdentifierPath();
        return new DropIndexStatement { IndexName = indexName, Collection = collection };
    }

    private Expression ParseExpression()
    {
        return ParseOr();
    }

    private Expression ParseOr()
    {
        var left = ParseAnd();
        while (Current.Type == TokenType.Or)
        {
            _pos++;
            var right = ParseAnd();
            left = new LogicalExpression { Left = left, Operator = TokenType.Or, Right = right };
        }
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseUnary();
        while (Current.Type == TokenType.And)
        {
            _pos++;
            var right = ParseUnary();
            left = new LogicalExpression { Left = left, Operator = TokenType.And, Right = right };
        }
        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.Type == TokenType.Not)
        {
            _pos++;
            return new NotExpression { Inner = ParseUnary() };
        }
        if (Current.Type == TokenType.LeftParen)
        {
            _pos++;
            var expr = ParseExpression();
            Expect(TokenType.RightParen);
            return expr;
        }
        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        var path = ReadIdentifierPath();

        // IS NULL / IS NOT NULL
        if (Current.Type == TokenType.Is)
        {
            _pos++;
            bool isNotNull = Match(TokenType.Not);
            Expect(TokenType.Null);
            return new IsNullExpression { JsonPath = path, IsNotNull = isNotNull };
        }

        var op = Current.Type;
        if (op is not (TokenType.Equals or TokenType.NotEquals or
            TokenType.GreaterThan or TokenType.LessThan or
            TokenType.GreaterThanOrEqual or TokenType.LessThanOrEqual or
            TokenType.Like))
        {
            throw new QueryParseException($"Expected comparison operator, got {op}", Current.Position);
        }
        _pos++;

        var (value, valueType) = ReadLiteralValue();
        return new ComparisonExpression
        {
            JsonPath = path,
            Operator = op,
            Value = value,
            ValueType = valueType
        };
    }

    private string ReadIdentifierPath()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Expect(TokenType.Identifier).Value);

        while (Current.Type == TokenType.Dot ||
               Current.Type == TokenType.LeftBracket)
        {
            if (Current.Type == TokenType.Dot)
            {
                _pos++;
                sb.Append('.');
                sb.Append(Expect(TokenType.Identifier).Value);
            }
            else if (Current.Type == TokenType.LeftBracket)
            {
                _pos++;
                sb.Append('[');
                if (Current.Type == TokenType.Star)
                {
                    sb.Append('*');
                    _pos++;
                }
                else
                {
                    sb.Append(Expect(TokenType.NumberLiteral).Value);
                }
                Expect(TokenType.RightBracket);
                sb.Append(']');
            }
        }

        return sb.ToString();
    }

    private (object? value, TokenType type) ReadLiteralValue()
    {
        var token = Current;
        _pos++;

        return token.Type switch
        {
            TokenType.StringLiteral => (token.Value, TokenType.StringLiteral),
            TokenType.NumberLiteral => (double.Parse(token.Value), TokenType.NumberLiteral),
            TokenType.True => (true, TokenType.True),
            TokenType.False => (false, TokenType.False),
            TokenType.Null => (null, TokenType.Null),
            _ => throw new QueryParseException($"Expected literal value, got {token.Type}", token.Position)
        };
    }

    /// <summary>
    /// Parse one item in the SELECT list: either a field path or an aggregate function call.
    /// Examples: name  |  passenger.lastName  |  SUM(price)  |  COUNT(*)  |  AVG(fare.total)
    /// </summary>
    private void ParseSelectItem(SelectStatement stmt)
    {
        var tok = Current.Type;
        if (tok is TokenType.Sum or TokenType.Avg or TokenType.Min or TokenType.Max or TokenType.Count)
        {
            var fn = tok switch
            {
                TokenType.Sum => AggregateFunction.Sum,
                TokenType.Avg => AggregateFunction.Avg,
                TokenType.Min => AggregateFunction.Min,
                TokenType.Max => AggregateFunction.Max,
                TokenType.Count => AggregateFunction.Count,
                _ => throw new QueryParseException($"Unknown aggregate: {tok}", Current.Position)
            };
            var fnName = tok.ToString().ToUpperInvariant();
            _pos++;
            Expect(TokenType.LeftParen);

            string path;
            if (Current.Type == TokenType.Star)
            {
                path = "*";
                _pos++;
            }
            else
            {
                path = ReadIdentifierPath();
            }
            Expect(TokenType.RightParen);

            var alias = path == "*" ? $"{fnName}(*)" : $"{fnName}({path})";
            stmt.Aggregates.Add(new AggregateField { Function = fn, Path = path, Alias = alias });
        }
        else
        {
            stmt.Fields.Add(ReadIdentifierPath());
        }
    }

    /// <summary>
    /// Split "collection.field.subfield" into (collection, "field.subfield").
    /// If the prefix doesn't match either known collection, treat the whole thing as the path.
    /// </summary>
    private static (string Collection, string Path) SplitCollectionPath(
        string fullPath, string outerCollection, string joinCollection)
    {
        int firstDot = fullPath.IndexOf('.');
        if (firstDot < 0)
        {
            // No prefix - default to outer collection
            return (outerCollection, fullPath);
        }

        var prefix = fullPath.Substring(0, firstDot);
        var rest = fullPath.Substring(firstDot + 1);

        if (string.Equals(prefix, outerCollection, StringComparison.OrdinalIgnoreCase))
            return (outerCollection, rest);
        if (string.Equals(prefix, joinCollection, StringComparison.OrdinalIgnoreCase))
            return (joinCollection, rest);

        // Prefix doesn't match known collections - treat entire path as path on outer
        return (outerCollection, fullPath);
    }
}
