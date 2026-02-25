namespace Cozo.DotNet.Datalog;

public enum DatalogDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public readonly record struct SourceSpan(int Start, int Length)
{
    public int End => Start + Length;
}

public sealed record DatalogDiagnostic(
    DatalogDiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceSpan? Span = null)
{
    public static DatalogDiagnostic Error(string code, string message, SourceSpan? span = null) =>
        new(DatalogDiagnosticSeverity.Error, code, message, span);
}

public sealed record DatalogParseResult(
    DatalogProgram? Program,
    IReadOnlyList<DatalogDiagnostic> Diagnostics)
{
    public bool Success => Program is not null && Diagnostics.All(d => d.Severity != DatalogDiagnosticSeverity.Error);
}

public sealed record DatalogValidationResult(
    DatalogProgramIr? Program,
    IReadOnlyList<DatalogDiagnostic> Diagnostics)
{
    public bool Success => Program is not null && Diagnostics.All(d => d.Severity != DatalogDiagnosticSeverity.Error);
}
