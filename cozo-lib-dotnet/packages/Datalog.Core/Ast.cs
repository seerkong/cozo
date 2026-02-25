namespace Cozo.DotNet.Datalog;

public sealed record DatalogProgram(
    IReadOnlyList<DatalogRule> Rules,
    DatalogQuery? Query);

public sealed record DatalogRule(
    DatalogAtom Head,
    IReadOnlyList<DatalogLiteral> Body,
    SourceSpan Span);

public sealed record DatalogQuery(
    IReadOnlyList<DatalogLiteral> Body,
    SourceSpan Span);

public abstract record DatalogLiteral(SourceSpan Span);

public sealed record DatalogAtomLiteral(DatalogAtom Atom, bool Negated, SourceSpan Span) : DatalogLiteral(Span);

public sealed record DatalogComparisonLiteral(DatalogTerm Left, DatalogComparisonOperator Operator, DatalogTerm Right, SourceSpan Span) : DatalogLiteral(Span);

public sealed record DatalogAtom(
    string Predicate,
    IReadOnlyList<DatalogTerm> Terms,
    SourceSpan Span);

public abstract record DatalogTerm(SourceSpan Span);

public sealed record DatalogVariableTerm(string Name, SourceSpan Span) : DatalogTerm(Span);

public sealed record DatalogParameterTerm(string Name, SourceSpan Span) : DatalogTerm(Span);

public sealed record DatalogStringTerm(string Value, SourceSpan Span) : DatalogTerm(Span);

public sealed record DatalogNumberTerm(decimal Value, SourceSpan Span) : DatalogTerm(Span);

public sealed record DatalogBooleanTerm(bool Value, SourceSpan Span) : DatalogTerm(Span);

public sealed record DatalogNullTerm(SourceSpan Span) : DatalogTerm(Span);

public enum DatalogComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}
