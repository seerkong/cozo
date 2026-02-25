namespace Cozo.DotNet.Datalog;

public sealed record DatalogProgramIr(
    IReadOnlyList<DatalogRuleIr> Rules,
    DatalogQueryIr? Query);

public sealed record DatalogRuleIr(
    DatalogAtomIr Head,
    IReadOnlyList<DatalogLiteralIr> Body,
    SourceSpan Span);

public sealed record DatalogQueryIr(
    IReadOnlyList<DatalogLiteralIr> Body,
    IReadOnlyList<string> Projection,
    SourceSpan Span);

public abstract record DatalogLiteralIr(SourceSpan Span);

public sealed record DatalogAtomLiteralIr(DatalogAtomIr Atom, bool Negated, SourceSpan Span) : DatalogLiteralIr(Span);

public sealed record DatalogComparisonLiteralIr(DatalogTermIr Left, DatalogComparisonOperator Operator, DatalogTermIr Right, SourceSpan Span) : DatalogLiteralIr(Span);

public sealed record DatalogAtomIr(
    string Predicate,
    IReadOnlyList<DatalogTermIr> Terms,
    SourceSpan Span);

public abstract record DatalogTermIr(SourceSpan Span);

public sealed record DatalogVariableTermIr(string Name, SourceSpan Span) : DatalogTermIr(Span);

public sealed record DatalogParameterTermIr(string Name, SourceSpan Span) : DatalogTermIr(Span);

public sealed record DatalogStringTermIr(string Value, SourceSpan Span) : DatalogTermIr(Span);

public sealed record DatalogNumberTermIr(decimal Value, SourceSpan Span) : DatalogTermIr(Span);

public sealed record DatalogBooleanTermIr(bool Value, SourceSpan Span) : DatalogTermIr(Span);

public sealed record DatalogNullTermIr(SourceSpan Span) : DatalogTermIr(Span);
