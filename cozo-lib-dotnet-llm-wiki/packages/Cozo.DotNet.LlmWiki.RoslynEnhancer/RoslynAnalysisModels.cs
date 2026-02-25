namespace Cozo.DotNet.LlmWiki.RoslynEnhancer;

/// <summary>
/// One semantically resolved call edge (design.md §1). Only in-repo callees are edged;
/// BCL / external-assembly targets are counted in <see cref="RoslynAnalysisResult.ExternalCalls"/>.
/// </summary>
/// <param name="CallerDocId">DocumentationCommentId of the nearest enclosing method / ctor /
/// property accessor / local function (top-level statements attribute to the synthesized entry point).</param>
/// <param name="CalleeDocId">DocumentationCommentId of the invoked symbol (synthesized default ctors included).</param>
/// <param name="CalleeIsInSource">Whether the callee has at least one in-source location.</param>
/// <param name="File">Path of the file containing the call site (as passed to Analyze).</param>
/// <param name="Line">1-based line of the call site.</param>
/// <param name="Confidence">1.0 semantic hit; 0.6 candidate-only.</param>
/// <param name="Evidence">"semantic" for hits; "candidate:&lt;CandidateReason&gt;" for candidates.</param>
public sealed record RoslynCallEdge(
    string CallerDocId,
    string CalleeDocId,
    bool CalleeIsInSource,
    string File,
    int Line,
    double Confidence,
    string Evidence);

/// <summary>
/// One in-source declared type or member symbol with its canonical DocId and the tree-sitter
/// sym_key equivalent (lang=csharp, namespace-qualified name, arity = method parameter count, else 0).
/// </summary>
/// <param name="DocId">DocumentationCommentId (e.g. "M:Ns.Type.Method(System.Int32)").</param>
/// <param name="SymKey">"csharp:&lt;Qualified&gt;#&lt;Arity&gt;" — matches SemanticParsing sym_key format.</param>
/// <param name="Qualified">Namespace-qualified dotted name (constructors use the type name, tree-sitter style).</param>
/// <param name="Arity">Parameter count for methods/constructors; 0 otherwise.</param>
/// <param name="File">Path of the declaring file (as passed to Analyze).</param>
/// <param name="StartLine">1-based first line of the declaration.</param>
/// <param name="EndLine">1-based last line of the declaration.</param>
public sealed record RoslynSymbolInfo(
    string DocId,
    string SymKey,
    string Qualified,
    int Arity,
    string File,
    int StartLine,
    int EndLine);

/// <summary>
/// One out-of-repo call site (track add-llm-wiki-depa-ontology, design §4.2, add-only): captured
/// where the external target used to be dropped after counting, so ck_external_call summaries can
/// be aggregated downstream. Never edged into ck_edge (the P0 discipline stands).
/// </summary>
/// <param name="CallerDocId">DocumentationCommentId of the enclosing caller (same attribution as <see cref="RoslynCallEdge"/>).</param>
/// <param name="TargetKey">Normalized external FQN: namespace-qualified dotted name, generics erased,
/// overloads merged at method-name granularity (e.g. "System.IO.File.WriteAllText"; constructors use the type name).</param>
/// <param name="File">Path of the file containing the call site (as passed to Analyze).</param>
/// <param name="Line">1-based line of the call site.</param>
public sealed record RoslynExternalCallSite(
    string CallerDocId,
    string TargetKey,
    string File,
    int Line);

/// <summary>Result of one whole-repo lightweight-compilation analysis pass.</summary>
/// <param name="Edges">Resolved call edges (in-repo callees only).</param>
/// <param name="Symbols">In-source declared type/member symbols with DocId + sym_key equivalence.</param>
/// <param name="ExternalCalls">Call sites whose target lives outside the analyzed sources (BCL / external assemblies) — counted, never edged.</param>
/// <param name="UnknownCalls">Call sites with neither a symbol nor candidates — skipped and counted.</param>
public sealed record RoslynAnalysisResult(
    IReadOnlyList<RoslynCallEdge> Edges,
    IReadOnlyList<RoslynSymbolInfo> Symbols,
    int ExternalCalls,
    int UnknownCalls)
{
    /// <summary>
    /// Per-site detail behind <see cref="ExternalCalls"/> (add-only, depa-ontology track):
    /// sites without an attributable caller DocId are counted but not listed.
    /// </summary>
    public IReadOnlyList<RoslynExternalCallSite> ExternalCallSites { get; init; } = [];
}
