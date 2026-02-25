namespace Cozo.DotNet.Datalog;

public sealed class PortableDatalogValidator
{
    private readonly List<DatalogDiagnostic> _diagnostics = [];

    public DatalogValidationResult Validate(DatalogProgram program)
    {
        var rules = new List<DatalogRuleIr>();
        foreach (var rule in program.Rules)
        {
            ValidateRule(rule);
            rules.Add(ConvertRule(rule));
        }

        DatalogQueryIr? query = null;
        if (program.Query is not null)
        {
            ValidateQuery(program.Query);
            query = ConvertQuery(program.Query);
        }

        var ir = _diagnostics.Any(d => d.Severity == DatalogDiagnosticSeverity.Error)
            ? null
            : new DatalogProgramIr(rules, query);
        return new DatalogValidationResult(ir, _diagnostics);
    }

    public DatalogValidationResult ParseAndValidate(string source)
    {
        var parse = PortableDatalogParser.Parse(source);
        if (!parse.Success || parse.Program is null)
        {
            return new DatalogValidationResult(null, parse.Diagnostics);
        }

        return Validate(parse.Program);
    }

    private void ValidateRule(DatalogRule rule)
    {
        var bound = BoundVariables(rule.Body);
        foreach (var variable in Variables(rule.Head.Terms))
        {
            if (!bound.Contains(variable.Name))
            {
                _diagnostics.Add(DatalogDiagnostic.Error("DL3001", $"Head variable '{variable.Name}' is not bound by a positive body atom.", variable.Span));
            }
        }

        ValidateLiteralSafety(rule.Body, bound);
    }

    private void ValidateQuery(DatalogQuery query)
    {
        var bound = BoundVariables(query.Body);
        ValidateLiteralSafety(query.Body, bound);
    }

    private void ValidateLiteralSafety(IReadOnlyList<DatalogLiteral> body, HashSet<string> bound)
    {
        foreach (var literal in body)
        {
            switch (literal)
            {
                case DatalogAtomLiteral { Negated: true } atomLiteral:
                    foreach (var variable in Variables(atomLiteral.Atom.Terms))
                    {
                        if (!bound.Contains(variable.Name))
                        {
                            _diagnostics.Add(DatalogDiagnostic.Error("DL3002", $"Negated atom variable '{variable.Name}' is not bound by a positive body atom.", variable.Span));
                        }
                    }

                    break;
                case DatalogComparisonLiteral comparison:
                    foreach (var variable in Variables([comparison.Left, comparison.Right]))
                    {
                        if (!bound.Contains(variable.Name))
                        {
                            _diagnostics.Add(DatalogDiagnostic.Error("DL3003", $"Comparison variable '{variable.Name}' is not bound by a positive body atom.", variable.Span));
                        }
                    }

                    break;
            }
        }
    }

    private static HashSet<string> BoundVariables(IReadOnlyList<DatalogLiteral> body)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var literal in body.OfType<DatalogAtomLiteral>().Where(l => !l.Negated))
        {
            foreach (var variable in Variables(literal.Atom.Terms))
            {
                bound.Add(variable.Name);
            }
        }

        return bound;
    }

    private static IEnumerable<DatalogVariableTerm> Variables(IEnumerable<DatalogTerm> terms) =>
        terms.OfType<DatalogVariableTerm>();

    private static DatalogRuleIr ConvertRule(DatalogRule rule) =>
        new(ConvertAtom(rule.Head), rule.Body.Select(ConvertLiteral).ToList(), rule.Span);

    private static DatalogQueryIr ConvertQuery(DatalogQuery query)
    {
        var projection = query.Body
            .OfType<DatalogAtomLiteral>()
            .SelectMany(l => l.Atom.Terms)
            .OfType<DatalogVariableTerm>()
            .Select(v => v.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new DatalogQueryIr(query.Body.Select(ConvertLiteral).ToList(), projection, query.Span);
    }

    private static DatalogLiteralIr ConvertLiteral(DatalogLiteral literal) =>
        literal switch
        {
            DatalogAtomLiteral atom => new DatalogAtomLiteralIr(ConvertAtom(atom.Atom), atom.Negated, atom.Span),
            DatalogComparisonLiteral comparison => new DatalogComparisonLiteralIr(ConvertTerm(comparison.Left), comparison.Operator, ConvertTerm(comparison.Right), comparison.Span),
            _ => throw new InvalidOperationException($"Unknown literal type {literal.GetType().Name}.")
        };

    private static DatalogAtomIr ConvertAtom(DatalogAtom atom) =>
        new(atom.Predicate, atom.Terms.Select(ConvertTerm).ToList(), atom.Span);

    private static DatalogTermIr ConvertTerm(DatalogTerm term) =>
        term switch
        {
            DatalogVariableTerm variable => new DatalogVariableTermIr(variable.Name, variable.Span),
            DatalogParameterTerm parameter => new DatalogParameterTermIr(parameter.Name, parameter.Span),
            DatalogStringTerm value => new DatalogStringTermIr(value.Value, value.Span),
            DatalogNumberTerm value => new DatalogNumberTermIr(value.Value, value.Span),
            DatalogBooleanTerm value => new DatalogBooleanTermIr(value.Value, value.Span),
            DatalogNullTerm value => new DatalogNullTermIr(value.Span),
            _ => throw new InvalidOperationException($"Unknown term type {term.GetType().Name}.")
        };
}
