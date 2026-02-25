namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// Parsing backend contract (design.md §1). Implementations: TreeSitterNativeBackend (primary),
/// TreeSitterCliBackend (fallback). Selection/degradation is handled by ParserBackendSelector.
/// Parse is synchronous by design: tree-sitter parsing is CPU-bound.
/// </summary>
public interface ISemanticParserBackend
{
    /// <summary>Stable backend identifier: "native" or "cli".</summary>
    string Name { get; }

    /// <summary>Whether this backend can parse the given language id (e.g. "csharp", "typescript").</summary>
    bool SupportsLanguage(string languageId);

    /// <summary>Parse source text of a file. Never throws for parse-level failures; reports via diagnostics.</summary>
    ParsedFileResult Parse(string path, string source, string languageId);
}

/// <summary>Result of parsing one file: symbol tree + structural edges + diagnostics.</summary>
public sealed record ParsedFileResult(
    bool Success,
    string FilePath,
    string LanguageId,
    string BackendName,
    bool HasErrors,
    IReadOnlyList<ParsedSymbol> Symbols,
    IReadOnlyList<ParsedEdge> Edges,
    IReadOnlyList<SemanticDiagnostic> Diagnostics)
{
    /// <summary>
    /// Call sites extracted from the file body (call-resolution track, design.md §1).
    /// Add-only field: empty when the producing backend does not extract call sites
    /// (cli backend, regex fallback). Resolution to symbols is a later indexing stage.
    /// </summary>
    public IReadOnlyList<ParsedCallSite> CallSites { get; init; } = [];
}

/// <summary>
/// One unresolved call/construction/member-access site (design.md §1).
/// CallerQualified is the qualified name of the innermost enclosing declared symbol,
/// or "&lt;file&gt;" for top-level code. TargetName has type arguments stripped.
/// ReceiverText is the raw source text of the receiver expression ("repo", "this",
/// a type name, or a chained expression), null for receiver-less calls and news.
/// Kind is "call" | "new" | "access"; AccessMode is "read" | "write" for kind=access, null otherwise.
/// Arity is the argument count (0 for accesses). Line is 1-based.
/// </summary>
public sealed record ParsedCallSite(
    string CallerQualified,
    string TargetName,
    string? ReceiverText,
    int Arity,
    string Kind,
    string? AccessMode,
    int Line);

/// <summary>One extracted symbol. Tree-shaped: nesting is expressed through Children.</summary>
public sealed record ParsedSymbol(
    string Name,
    string Kind,
    int StartLine,
    int EndLine,
    string Signature = "",
    string Visibility = "",
    bool Exported = false,
    IReadOnlyList<ParsedSymbol>? Children = null)
{
    public IReadOnlyList<ParsedSymbol> Children { get; init; } = Children ?? [];

    /// <summary>
    /// v2 sym_key (G2 semantics: lang + fully-qualified name + arity), e.g. "csharp:Demo.Svc.Repo.Fetch#1".
    /// Arity is the parameter count for methods/constructors and 0 for everything else.
    /// Add-only field: empty when the producing backend does not compute it.
    /// </summary>
    public string SymKey { get; init; } = "";
}

/// <summary>One structural edge (EXTENDS/IMPLEMENTS/IMPORTS/...). From is the qualified source symbol name; To is the (possibly unresolved) target name.</summary>
public sealed record ParsedEdge(
    string FromQualified,
    string ToName,
    string Kind,
    double Confidence = 0.9,
    string Evidence = "");
