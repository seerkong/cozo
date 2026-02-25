namespace Cozo.DotNet.Datalog;

public sealed class PortableDatalogParser
{
    private readonly string _source;
    private readonly Lexer _lexer;
    private readonly List<DatalogDiagnostic> _diagnostics = [];
    private Token _current;

    private PortableDatalogParser(string source)
    {
        _source = source;
        _lexer = new Lexer(source);
        _current = _lexer.Next();
    }

    public static DatalogParseResult Parse(string source)
    {
        var parser = new PortableDatalogParser(source);
        return parser.ParseProgram();
    }

    private DatalogParseResult ParseProgram()
    {
        var rules = new List<DatalogRule>();
        DatalogQuery? query = null;

        while (_current.Kind != TokenKind.End)
        {
            if (_current.Kind == TokenKind.Query)
            {
                if (query is not null)
                {
                    AddError("DL2001", "Only one query is supported in a Portable Datalog program.", _current.Span);
                }

                query = ParseQuery();
                continue;
            }

            rules.Add(ParseRule());
        }

        _diagnostics.AddRange(_lexer.Diagnostics);
        var program = _diagnostics.Any(d => d.Severity == DatalogDiagnosticSeverity.Error)
            ? null
            : new DatalogProgram(rules, query);
        return new DatalogParseResult(program, _diagnostics);
    }

    private DatalogRule ParseRule()
    {
        var start = _current.Span.Start;
        var head = ParseAtom();
        IReadOnlyList<DatalogLiteral> body;
        if (Match(TokenKind.ColonDash))
        {
            body = ParseLiteralList();
        }
        else
        {
            body = [];
        }

        Expect(TokenKind.Dot, "Rule must end with '.'.");
        return new DatalogRule(head, body, SpanFrom(start));
    }

    private DatalogQuery ParseQuery()
    {
        var start = _current.Span.Start;
        Expect(TokenKind.Query, "Query must start with '?-'.");
        var body = ParseLiteralList();
        Expect(TokenKind.Dot, "Query must end with '.'.");
        return new DatalogQuery(body, SpanFrom(start));
    }

    private IReadOnlyList<DatalogLiteral> ParseLiteralList()
    {
        var literals = new List<DatalogLiteral>();
        literals.Add(ParseLiteral());
        while (Match(TokenKind.Comma))
        {
            literals.Add(ParseLiteral());
        }

        return literals;
    }

    private DatalogLiteral ParseLiteral()
    {
        var start = _current.Span.Start;
        var negated = Match(TokenKind.Not);
        if (_current.Kind is TokenKind.Identifier)
        {
            var atom = ParseAtom();
            return new DatalogAtomLiteral(atom, negated, SpanFrom(start));
        }

        if (negated)
        {
            AddError("DL2002", "Negation can only be applied to relation atoms.", SpanFrom(start));
        }

        var left = ParseTerm();
        if (!TryParseComparison(out var op))
        {
            AddError("DL2003", "Expected comparison operator.", _current.Span);
            return new DatalogComparisonLiteral(left, DatalogComparisonOperator.Equal, new DatalogNullTerm(_current.Span), SpanFrom(start));
        }

        var right = ParseTerm();
        return new DatalogComparisonLiteral(left, op, right, SpanFrom(start));
    }

    private DatalogAtom ParseAtom()
    {
        var start = _current.Span.Start;
        var predicate = ExpectIdentifier("Expected predicate name.");
        Expect(TokenKind.LParen, "Expected '(' after predicate name.");
        var terms = new List<DatalogTerm>();
        if (_current.Kind != TokenKind.RParen)
        {
            terms.Add(ParseTerm());
            while (Match(TokenKind.Comma))
            {
                terms.Add(ParseTerm());
            }
        }

        Expect(TokenKind.RParen, "Expected ')' after atom terms.");
        return new DatalogAtom(predicate, terms, SpanFrom(start));
    }

    private DatalogTerm ParseTerm()
    {
        var token = _current;
        switch (token.Kind)
        {
            case TokenKind.Variable:
                Advance();
                return new DatalogVariableTerm(token.Text, token.Span);
            case TokenKind.Parameter:
                Advance();
                return new DatalogParameterTerm((string)token.Value!, token.Span);
            case TokenKind.String:
                Advance();
                return new DatalogStringTerm((string)token.Value!, token.Span);
            case TokenKind.Number:
                Advance();
                return new DatalogNumberTerm((decimal)token.Value!, token.Span);
            case TokenKind.True:
                Advance();
                return new DatalogBooleanTerm(true, token.Span);
            case TokenKind.False:
                Advance();
                return new DatalogBooleanTerm(false, token.Span);
            case TokenKind.Null:
                Advance();
                return new DatalogNullTerm(token.Span);
            case TokenKind.Identifier:
                Advance();
                return new DatalogStringTerm(token.Text, token.Span);
            default:
                AddError("DL2004", "Expected term.", token.Span);
                Advance();
                return new DatalogNullTerm(token.Span);
        }
    }

    private bool TryParseComparison(out DatalogComparisonOperator op)
    {
        op = _current.Kind switch
        {
            TokenKind.Equal => DatalogComparisonOperator.Equal,
            TokenKind.NotEqual => DatalogComparisonOperator.NotEqual,
            TokenKind.Less => DatalogComparisonOperator.LessThan,
            TokenKind.LessEqual => DatalogComparisonOperator.LessThanOrEqual,
            TokenKind.Greater => DatalogComparisonOperator.GreaterThan,
            TokenKind.GreaterEqual => DatalogComparisonOperator.GreaterThanOrEqual,
            _ => DatalogComparisonOperator.Equal
        };
        if (_current.Kind is TokenKind.Equal or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
        {
            Advance();
            return true;
        }

        return false;
    }

    private string ExpectIdentifier(string message)
    {
        if (_current.Kind == TokenKind.Identifier)
        {
            var text = _current.Text;
            Advance();
            return text;
        }

        AddError("DL2005", message, _current.Span);
        Advance();
        return "__error__";
    }

    private void Expect(TokenKind kind, string message)
    {
        if (_current.Kind == kind)
        {
            Advance();
            return;
        }

        AddError("DL2006", message, _current.Span);
    }

    private bool Match(TokenKind kind)
    {
        if (_current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private void Advance()
    {
        _current = _lexer.Next();
    }

    private void AddError(string code, string message, SourceSpan span)
    {
        _diagnostics.Add(DatalogDiagnostic.Error(code, message, span));
    }

    private SourceSpan SpanFrom(int start)
    {
        var end = Math.Min(_source.Length, Math.Max(start, _current.Span.End));
        return new SourceSpan(start, end - start);
    }
}
