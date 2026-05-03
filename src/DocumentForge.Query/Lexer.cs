using DocumentForge.Core;

namespace DocumentForge.Query;

public sealed class Lexer
{
    private readonly string _input;
    private int _pos;
    private readonly List<Token> _tokens = new();

    private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = TokenType.Select,
        ["FROM"] = TokenType.From,
        ["WHERE"] = TokenType.Where,
        ["AND"] = TokenType.And,
        ["OR"] = TokenType.Or,
        ["NOT"] = TokenType.Not,
        ["INSERT"] = TokenType.Insert,
        ["INTO"] = TokenType.Into,
        ["VALUES"] = TokenType.Values,
        ["UPDATE"] = TokenType.Update,
        ["SET"] = TokenType.Set,
        ["DELETE"] = TokenType.Delete,
        ["CREATE"] = TokenType.Create,
        ["UNIQUE"] = TokenType.Unique,
        ["INDEX"] = TokenType.Index,
        ["ON"] = TokenType.On,
        ["DROP"] = TokenType.Drop,
        ["ORDER"] = TokenType.OrderBy, // handled specially with BY
        ["ASC"] = TokenType.Asc,
        ["DESC"] = TokenType.Desc,
        ["LIMIT"] = TokenType.Limit,
        ["OFFSET"] = TokenType.Offset,
        ["LIKE"] = TokenType.Like,
        ["IN"] = TokenType.In,
        ["BETWEEN"] = TokenType.Between,
        ["IS"] = TokenType.Is,
        ["TRUE"] = TokenType.True,
        ["FALSE"] = TokenType.False,
        ["NULL"] = TokenType.Null,
        ["COUNT"] = TokenType.Count,
        ["JOIN"] = TokenType.Join,
        ["LEFT"] = TokenType.Left,
        ["RIGHT"] = TokenType.Right,
        ["CROSS"] = TokenType.Cross,
        ["OUTER"] = TokenType.Outer, // optional sugar in `LEFT OUTER JOIN` / `RIGHT OUTER JOIN`
        ["SUM"] = TokenType.Sum,
        ["AVG"] = TokenType.Avg,
        ["MIN"] = TokenType.Min,
        ["MAX"] = TokenType.Max,
        ["GROUP"] = TokenType.Group, // followed by BY (handled specially)
        ["DISTINCT"] = TokenType.Distinct,
    };

    public Lexer(string input)
    {
        _input = input;
    }

    public List<Token> Tokenize()
    {
        _tokens.Clear();
        _pos = 0;

        while (_pos < _input.Length)
        {
            SkipWhitespace();
            if (_pos >= _input.Length) break;

            var ch = _input[_pos];

            if (ch == '\'')
                ReadStringLiteral();
            else if (ch == '{')
                ReadJsonLiteral();
            else if (char.IsDigit(ch) || (ch == '-' && _pos + 1 < _input.Length && char.IsDigit(_input[_pos + 1])))
                ReadNumber();
            else if (char.IsLetter(ch) || ch == '_')
                ReadIdentifierOrKeyword();
            else if (ch == '*') { _tokens.Add(new Token(TokenType.Star, "*", _pos)); _pos++; }
            else if (ch == ',') { _tokens.Add(new Token(TokenType.Comma, ",", _pos)); _pos++; }
            else if (ch == '(') { _tokens.Add(new Token(TokenType.LeftParen, "(", _pos)); _pos++; }
            else if (ch == ')') { _tokens.Add(new Token(TokenType.RightParen, ")", _pos)); _pos++; }
            else if (ch == '[') { _tokens.Add(new Token(TokenType.LeftBracket, "[", _pos)); _pos++; }
            else if (ch == ']') { _tokens.Add(new Token(TokenType.RightBracket, "]", _pos)); _pos++; }
            else if (ch == '.' && !(_pos + 1 < _input.Length && char.IsDigit(_input[_pos + 1])))
            { _tokens.Add(new Token(TokenType.Dot, ".", _pos)); _pos++; }
            else if (ch == '=' && Peek(1) != '!') { _tokens.Add(new Token(TokenType.Equals, "=", _pos)); _pos++; }
            else if (ch == '!' && Peek(1) == '=') { _tokens.Add(new Token(TokenType.NotEquals, "!=", _pos)); _pos += 2; }
            else if (ch == '<' && Peek(1) == '=') { _tokens.Add(new Token(TokenType.LessThanOrEqual, "<=", _pos)); _pos += 2; }
            else if (ch == '>' && Peek(1) == '=') { _tokens.Add(new Token(TokenType.GreaterThanOrEqual, ">=", _pos)); _pos += 2; }
            else if (ch == '<' && Peek(1) == '>') { _tokens.Add(new Token(TokenType.NotEquals, "<>", _pos)); _pos += 2; }
            else if (ch == '<') { _tokens.Add(new Token(TokenType.LessThan, "<", _pos)); _pos++; }
            else if (ch == '>') { _tokens.Add(new Token(TokenType.GreaterThan, ">", _pos)); _pos++; }
            else
                throw new QueryParseException($"Unexpected character: '{ch}'", _pos);
        }

        _tokens.Add(new Token(TokenType.Eof, "", _pos));
        return _tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < _input.Length && char.IsWhiteSpace(_input[_pos]))
            _pos++;
    }

    private char Peek(int offset)
    {
        var idx = _pos + offset;
        return idx < _input.Length ? _input[idx] : '\0';
    }

    private void ReadStringLiteral()
    {
        int start = _pos;
        _pos++; // skip opening quote
        var sb = new System.Text.StringBuilder();
        while (_pos < _input.Length && _input[_pos] != '\'')
        {
            if (_input[_pos] == '\\' && _pos + 1 < _input.Length)
            {
                _pos++;
                sb.Append(_input[_pos]);
            }
            else
            {
                sb.Append(_input[_pos]);
            }
            _pos++;
        }
        if (_pos >= _input.Length)
            throw new QueryParseException("Unterminated string literal", start);
        _pos++; // skip closing quote
        _tokens.Add(new Token(TokenType.StringLiteral, sb.ToString(), start));
    }

    private void ReadJsonLiteral()
    {
        int start = _pos;
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        bool inString = false;

        while (_pos < _input.Length)
        {
            var ch = _input[_pos];
            sb.Append(ch);

            if (inString)
            {
                if (ch == '\\' && _pos + 1 < _input.Length)
                {
                    _pos++;
                    sb.Append(_input[_pos]);
                }
                else if (ch == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { _pos++; break; } }
            }
            _pos++;
        }

        _tokens.Add(new Token(TokenType.JsonLiteral, sb.ToString(), start));
    }

    private void ReadNumber()
    {
        int start = _pos;
        if (_input[_pos] == '-') _pos++;
        while (_pos < _input.Length && (char.IsDigit(_input[_pos]) || _input[_pos] == '.'))
            _pos++;
        _tokens.Add(new Token(TokenType.NumberLiteral, _input[start.._pos], start));
    }

    private void ReadIdentifierOrKeyword()
    {
        int start = _pos;
        while (_pos < _input.Length && (char.IsLetterOrDigit(_input[_pos]) || _input[_pos] == '_'))
            _pos++;

        var word = _input[start.._pos];

        // Check for ORDER BY / GROUP BY (two-word keywords)
        if (word.Equals("ORDER", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("GROUP", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (_pos < _input.Length)
            {
                int byStart = _pos;
                while (_pos < _input.Length && char.IsLetter(_input[_pos])) _pos++;
                var nextWord = _input[byStart.._pos];
                if (nextWord.Equals("BY", StringComparison.OrdinalIgnoreCase))
                {
                    var compoundType = word.Equals("ORDER", StringComparison.OrdinalIgnoreCase)
                        ? TokenType.OrderBy
                        : TokenType.Group; // GROUP BY - we'll use Group token, parser expects "BY" consumed
                    _tokens.Add(new Token(compoundType, $"{word} BY", start));
                    return;
                }
                // Not "BY" - rewind
                _pos = byStart;
            }
        }

        if (Keywords.TryGetValue(word, out var tokenType))
            _tokens.Add(new Token(tokenType, word, start));
        else
            _tokens.Add(new Token(TokenType.Identifier, word, start));
    }
}
