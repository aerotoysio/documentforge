namespace DocumentForge.Query;

public enum TokenType
{
    // Keywords
    Select, From, Where, And, Or, Not,
    Insert, Into, Values, Update, Set, Delete,
    Create, Unique, Index, On, Drop,
    OrderBy, Asc, Desc, Limit, Offset,
    Like, In, Between, Is,
    True, False, Null,
    Count, Join,
    Sum, Avg, Min, Max, Group, By,

    // Operators
    Equals, NotEquals,
    GreaterThan, LessThan, GreaterThanOrEqual, LessThanOrEqual,

    // Symbols
    Star, Comma, Dot, LeftParen, RightParen,
    LeftBracket, RightBracket,

    // Literals
    Identifier, StringLiteral, NumberLiteral,
    JsonLiteral,

    // Special
    Eof
}

public sealed class Token
{
    public TokenType Type { get; }
    public string Value { get; }
    public int Position { get; }

    public Token(TokenType type, string value, int position)
    {
        Type = type;
        Value = value;
        Position = position;
    }

    public override string ToString() => $"{Type}({Value})@{Position}";
}
