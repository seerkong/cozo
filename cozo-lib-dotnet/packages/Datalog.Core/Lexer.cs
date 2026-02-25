using System.Globalization;
using System.Text;

namespace Cozo.DotNet.Datalog;

internal enum TokenKind
{
    End,
    Identifier,
    Variable,
    Parameter,
    String,
    Number,
    True,
    False,
    Null,
    Not,
    Query,
    ColonDash,
    LParen,
    RParen,
    Comma,
    Dot,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual
}

internal readonly record struct Token(TokenKind Kind, string Text, SourceSpan Span, object? Value = null);

internal sealed class Lexer
{
    private readonly string _source;
    private readonly List<DatalogDiagnostic> _diagnostics = [];
    private int _position;

    public Lexer(string source)
    {
        _source = source;
    }

    public IReadOnlyList<DatalogDiagnostic> Diagnostics => _diagnostics;

    public Token Next()
    {
        SkipWhitespaceAndComments();
        if (_position >= _source.Length)
        {
            return new Token(TokenKind.End, string.Empty, new SourceSpan(_position, 0));
        }

        var start = _position;
        var current = _source[_position];
        switch (current)
        {
            case '(':
                _position++;
                return Token(TokenKind.LParen, start);
            case ')':
                _position++;
                return Token(TokenKind.RParen, start);
            case ',':
                _position++;
                return Token(TokenKind.Comma, start);
            case '.':
                _position++;
                return Token(TokenKind.Dot, start);
            case '?':
                if (Match("-"))
                {
                    return Token(TokenKind.Query, start);
                }

                return Unknown(start, "Unexpected '?'. Did you mean '?-'?");
            case ':':
                if (Match("-"))
                {
                    return Token(TokenKind.ColonDash, start);
                }

                return Unknown(start, "Unexpected ':'. Did you mean ':-'?");
            case '!':
                if (Match("="))
                {
                    return Token(TokenKind.NotEqual, start);
                }

                return Unknown(start, "Unexpected '!'. Did you mean '!='?");
            case '=':
                _position++;
                return Token(TokenKind.Equal, start);
            case '<':
                _position++;
                if (Peek() == '=')
                {
                    _position++;
                    return new Token(TokenKind.LessEqual, _source[start.._position], new SourceSpan(start, _position - start));
                }

                return Token(TokenKind.Less, start);
            case '>':
                _position++;
                if (Peek() == '=')
                {
                    _position++;
                    return new Token(TokenKind.GreaterEqual, _source[start.._position], new SourceSpan(start, _position - start));
                }

                return Token(TokenKind.Greater, start);
            case '\'':
            case '"':
                return ReadString(current);
            case '$':
                return ReadParameter();
        }

        if (char.IsDigit(current) || current == '-' && char.IsDigit(Peek(1)))
        {
            return ReadNumber();
        }

        if (IsIdentifierStart(current))
        {
            return ReadIdentifier();
        }

        _position++;
        return Unknown(start, $"Unexpected character '{current}'.");
    }

    private Token ReadIdentifier()
    {
        var start = _position;
        _position++;
        while (_position < _source.Length && IsIdentifierPart(_source[_position]))
        {
            _position++;
        }

        var text = _source[start.._position];
        return text switch
        {
            "true" => new Token(TokenKind.True, text, new SourceSpan(start, _position - start), true),
            "false" => new Token(TokenKind.False, text, new SourceSpan(start, _position - start), false),
            "null" => new Token(TokenKind.Null, text, new SourceSpan(start, _position - start)),
            "not" => new Token(TokenKind.Not, text, new SourceSpan(start, _position - start)),
            _ when char.IsUpper(text[0]) || text[0] == '_' => new Token(TokenKind.Variable, text, new SourceSpan(start, _position - start)),
            _ => new Token(TokenKind.Identifier, text, new SourceSpan(start, _position - start))
        };
    }

    private Token ReadParameter()
    {
        var start = _position;
        _position++;
        if (_position >= _source.Length || !IsIdentifierStart(_source[_position]))
        {
            return Unknown(start, "Parameter must be followed by an identifier.");
        }

        var nameStart = _position;
        _position++;
        while (_position < _source.Length && IsIdentifierPart(_source[_position]))
        {
            _position++;
        }

        var name = _source[nameStart.._position];
        return new Token(TokenKind.Parameter, _source[start.._position], new SourceSpan(start, _position - start), name);
    }

    private Token ReadNumber()
    {
        var start = _position;
        if (_source[_position] == '-')
        {
            _position++;
        }

        while (_position < _source.Length && char.IsDigit(_source[_position]))
        {
            _position++;
        }

        if (_position < _source.Length && _source[_position] == '.')
        {
            _position++;
            while (_position < _source.Length && char.IsDigit(_source[_position]))
            {
                _position++;
            }
        }

        var text = _source[start.._position];
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            _diagnostics.Add(DatalogDiagnostic.Error("DL1004", $"Invalid number literal '{text}'.", new SourceSpan(start, _position - start)));
        }

        return new Token(TokenKind.Number, text, new SourceSpan(start, _position - start), number);
    }

    private Token ReadString(char quote)
    {
        var start = _position;
        _position++;
        var builder = new StringBuilder();
        while (_position < _source.Length)
        {
            var current = _source[_position++];
            if (current == quote)
            {
                return new Token(TokenKind.String, _source[start.._position], new SourceSpan(start, _position - start), builder.ToString());
            }

            if (current == '\\' && _position < _source.Length)
            {
                var escaped = _source[_position++];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    _ => escaped
                });
                continue;
            }

            builder.Append(current);
        }

        _diagnostics.Add(DatalogDiagnostic.Error("DL1005", "Unterminated string literal.", new SourceSpan(start, _position - start)));
        return new Token(TokenKind.String, _source[start.._position], new SourceSpan(start, _position - start), builder.ToString());
    }

    private void SkipWhitespaceAndComments()
    {
        while (_position < _source.Length)
        {
            var current = _source[_position];
            if (char.IsWhiteSpace(current))
            {
                _position++;
                continue;
            }

            if (current == '%' || current == '#')
            {
                while (_position < _source.Length && _source[_position] is not '\n' and not '\r')
                {
                    _position++;
                }

                continue;
            }

            if (current == '/' && Peek(1) == '/')
            {
                _position += 2;
                while (_position < _source.Length && _source[_position] is not '\n' and not '\r')
                {
                    _position++;
                }

                continue;
            }

            break;
        }
    }

    private bool Match(string expected)
    {
        if (_position + expected.Length >= _source.Length)
        {
            return false;
        }

        if (_source.Substring(_position + 1, expected.Length) != expected)
        {
            return false;
        }

        _position += expected.Length + 1;
        return true;
    }

    private char Peek(int offset = 0)
    {
        var index = _position + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    private Token Token(TokenKind kind, int start) => new(kind, _source[start.._position], new SourceSpan(start, _position - start));

    private Token Unknown(int start, string message)
    {
        if (_position <= start)
        {
            _position = start + 1;
        }

        _diagnostics.Add(DatalogDiagnostic.Error("DL1001", message, new SourceSpan(start, Math.Max(1, _position - start))));
        return Next();
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
}
