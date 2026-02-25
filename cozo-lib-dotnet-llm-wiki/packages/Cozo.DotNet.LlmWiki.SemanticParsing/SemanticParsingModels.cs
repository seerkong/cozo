namespace Cozo.DotNet.LlmWiki.SemanticParsing;

public sealed record ParserStatus(
    bool Available,
    string Parser,
    string Version,
    IReadOnlyList<SemanticDiagnostic> Diagnostics,
    // Add-only fields (T1.1): native backend availability. Defaults preserve the CLI-era shape.
    bool NativeAvailable = false,
    string NativeDetail = "");

public sealed record SemanticParseRequest(
    string FilePath,
    string? Language = null,
    int MaxNodes = 256);

public sealed record SemanticParseResult(
    bool Success,
    string FilePath,
    string Parser,
    string Language,
    IReadOnlyList<SemanticNodeSummary> Nodes,
    IReadOnlyList<SemanticDiagnostic> Diagnostics,
    string RawOutput = "");

public sealed record SemanticNodeSummary(
    string Kind,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string Text = "");

public sealed record SemanticDiagnostic(
    string Code,
    string Message,
    string Severity = "info");
