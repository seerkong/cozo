using System.Globalization;
using System.Text;
using Cozo.DotNet.Datalog;

namespace Cozo.DotNet.Datalog.Cozo;

public sealed record CozoStoredRelationMapping(
    string Predicate,
    string Relation,
    IReadOnlyList<string> Fields);

public sealed record CozoDatalogCompileOptions(
    IReadOnlyDictionary<string, CozoStoredRelationMapping>? StoredRelations = null,
    IReadOnlyDictionary<string, object?>? Parameters = null);

public sealed record CozoScriptCompileResult(
    string Script,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<DatalogDiagnostic> Diagnostics)
{
    public bool Success => Diagnostics.All(d => d.Severity != DatalogDiagnosticSeverity.Error);
}

public sealed class CozoDatalogCompiler
{
    private readonly List<DatalogDiagnostic> _diagnostics = [];
    private IReadOnlyDictionary<string, CozoStoredRelationMapping> _storedRelations = new Dictionary<string, CozoStoredRelationMapping>();
    private IReadOnlyDictionary<string, object?> _parameters = new Dictionary<string, object?>();

    public CozoScriptCompileResult Compile(DatalogProgramIr program, CozoDatalogCompileOptions? options = null)
    {
        _diagnostics.Clear();
        _storedRelations = options?.StoredRelations ?? new Dictionary<string, CozoStoredRelationMapping>();
        _parameters = options?.Parameters ?? new Dictionary<string, object?>();

        var builder = new StringBuilder();
        foreach (var rule in program.Rules)
        {
            builder.Append(CompileRule(rule));
            builder.AppendLine();
        }

        if (program.Query is not null)
        {
            builder.Append(CompileQuery(program.Query));
            builder.AppendLine();
        }

        var diagnostics = _diagnostics.ToList();
        var script = diagnostics.Any(d => d.Severity == DatalogDiagnosticSeverity.Error)
            ? string.Empty
            : builder.ToString().TrimEnd();
        return new CozoScriptCompileResult(script, _parameters, diagnostics);
    }

    private string CompileRule(DatalogRuleIr rule)
    {
        if (rule.Head.Terms.Any(t => t is not DatalogVariableTermIr))
        {
            _diagnostics.Add(DatalogDiagnostic.Error("CZ4001", "Rule head terms must be variables.", rule.Head.Span));
        }

        var headVars = string.Join(", ", rule.Head.Terms.Select(CompileTerm));
        return $"{EscapeIdentifier(rule.Head.Predicate)}[{headVars}] := {CompileBody(rule.Body)}";
    }

    private string CompileQuery(DatalogQueryIr query)
    {
        var projection = string.Join(", ", query.Projection.Select(EscapeIdentifier));
        return $"?[{projection}] := {CompileBody(query.Body)}";
    }

    private string CompileBody(IReadOnlyList<DatalogLiteralIr> body) =>
        string.Join(", ", body.Select(CompileLiteral));

    private string CompileLiteral(DatalogLiteralIr literal) =>
        literal switch
        {
            DatalogAtomLiteralIr atom => CompileAtomLiteral(atom),
            DatalogComparisonLiteralIr comparison => $"{CompileTerm(comparison.Left)} {CompileOperator(comparison.Operator)} {CompileTerm(comparison.Right)}",
            _ => throw new InvalidOperationException($"Unknown literal type {literal.GetType().Name}.")
        };

    private string CompileAtomLiteral(DatalogAtomLiteralIr literal)
    {
        var atom = literal.Atom;
        var compiled = _storedRelations.TryGetValue(atom.Predicate, out var mapping)
            ? CompileStoredAtom(atom, mapping)
            : CompileInlineAtom(atom);
        return literal.Negated ? $"not {compiled}" : compiled;
    }

    private string CompileStoredAtom(DatalogAtomIr atom, CozoStoredRelationMapping mapping)
    {
        if (mapping.Fields.Count != atom.Terms.Count)
        {
            _diagnostics.Add(DatalogDiagnostic.Error(
                "CZ4002",
                $"Stored relation mapping for '{atom.Predicate}' expects {mapping.Fields.Count} terms, got {atom.Terms.Count}.",
                atom.Span));
        }

        var pairs = mapping.Fields.Zip(atom.Terms, (field, term) => $"{EscapeIdentifier(field)}: {CompileTerm(term)}");
        return $"*{EscapeIdentifier(mapping.Relation)}{{{string.Join(", ", pairs)}}}";
    }

    private static string CompileInlineAtom(DatalogAtomIr atom)
    {
        var terms = string.Join(", ", atom.Terms.Select(CompileTerm));
        return $"{EscapeIdentifier(atom.Predicate)}[{terms}]";
    }

    private static string CompileTerm(DatalogTermIr term) =>
        term switch
        {
            DatalogVariableTermIr variable => EscapeIdentifier(variable.Name),
            DatalogParameterTermIr parameter => $"${EscapeIdentifier(parameter.Name)}",
            DatalogStringTermIr value => Quote(value.Value),
            DatalogNumberTermIr value => value.Value.ToString(CultureInfo.InvariantCulture),
            DatalogBooleanTermIr value => value.Value ? "true" : "false",
            DatalogNullTermIr => "null",
            _ => throw new InvalidOperationException($"Unknown term type {term.GetType().Name}.")
        };

    private static string CompileOperator(DatalogComparisonOperator op) =>
        op switch
        {
            DatalogComparisonOperator.Equal => "==",
            DatalogComparisonOperator.NotEqual => "!=",
            DatalogComparisonOperator.LessThan => "<",
            DatalogComparisonOperator.LessThanOrEqual => "<=",
            DatalogComparisonOperator.GreaterThan => ">",
            DatalogComparisonOperator.GreaterThanOrEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
        };

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c
            });
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string EscapeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));
        }

        return identifier.Replace('-', '_');
    }
}
