using System.Runtime.InteropServices;
using System.Text;

namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// Primary parsing backend: in-process tree-sitter through P/Invoke (design.md §2).
/// Construction is gated by TryCreate, which probes the native libraries and asserts
/// each grammar's ABI version against the runtime's supported window; on any failure
/// it returns null with diagnostics so ParserBackendSelector can degrade (cli → none).
/// Symbol/edge extraction is delegated to the per-language extractors (P2):
/// CSharpExtractor for csharp, TypeScriptExtractor for typescript/javascript,
/// and JavaExtractor for java.
/// </summary>
public sealed class TreeSitterNativeBackend : ISemanticParserBackend
{
    private readonly IReadOnlyDictionary<string, TreeSitterGrammar> _grammars;

    private TreeSitterNativeBackend(IReadOnlyDictionary<string, TreeSitterGrammar> grammars)
    {
        _grammars = grammars;
    }

    public string Name => "native";

    /// <summary>Loaded grammars with their ABI versions, e.g. csharp abi=15.</summary>
    internal IReadOnlyList<(string LanguageId, uint AbiVersion)> LoadedGrammars =>
        _grammars.Values
            .DistinctBy(grammar => grammar.GrammarName)
            .Select(grammar => (grammar.GrammarName, grammar.AbiVersion))
            .ToArray();

    public bool SupportsLanguage(string languageId) =>
        _grammars.ContainsKey(Normalize(languageId));

    /// <summary>
    /// Try to initialize the native backend. Returns null when the native libraries are
    /// missing or every grammar fails the ABI assertion; diagnostics explain why.
    /// probeDirectories overrides the default search path (AppContext.BaseDirectory and
    /// runtimes/&lt;rid&gt;/native) — used by tests to simulate missing libraries.
    /// </summary>
    public static TreeSitterNativeBackend? TryCreate(
        out IReadOnlyList<SemanticDiagnostic> diagnostics,
        IReadOnlyList<string>? probeDirectories = null)
    {
        var collected = new List<SemanticDiagnostic>();
        diagnostics = collected;

        if (!TreeSitterNative.TryProbeLibrary(TreeSitterNative.RuntimeLibrary, probeDirectories, out _))
        {
            collected.Add(new SemanticDiagnostic(
                "TSNAT001",
                $"Native library '{TreeSitterNative.GetPlatformLibraryFileName(TreeSitterNative.RuntimeLibrary)}' not found "
                + $"(searched: {string.Join(", ", TreeSitterNative.GetCandidatePaths(TreeSitterNative.RuntimeLibrary, probeDirectories))}). "
                + "Native tree-sitter backend unavailable; falling back.",
                "warning"));
            return null;
        }

        if (probeDirectories is not null)
        {
            // Make DllImport resolution consistent with the successful probe.
            TreeSitterNative.AdditionalSearchDirectories = probeDirectories;
        }

        var grammars = new Dictionary<string, TreeSitterGrammar>(StringComparer.OrdinalIgnoreCase);
        InitializeGrammar("csharp", TreeSitterNative.CSharpLibrary, ["csharp"], probeDirectories, grammars, collected);
        InitializeGrammar("typescript", TreeSitterNative.TypeScriptLibrary, ["typescript", "javascript"], probeDirectories, grammars, collected);
        InitializeGrammar("java", TreeSitterNative.JavaLibrary, ["java"], probeDirectories, grammars, collected);

        if (grammars.Count == 0)
        {
            collected.Add(new SemanticDiagnostic(
                "TSNAT003",
                "Native tree-sitter runtime loaded but no grammar passed initialization; native backend unavailable.",
                "warning"));
            return null;
        }

        return new TreeSitterNativeBackend(grammars);
    }

    /// <summary>
    /// Assert a grammar ABI version against the runtime's supported window
    /// [TreeSitterNative.MinCompatibleLanguageAbi, TreeSitterNative.MaxSupportedLanguageAbi].
    /// Returns null when compatible, otherwise an error diagnostic carrying the version numbers.
    /// </summary>
    internal static SemanticDiagnostic? ValidateAbiVersion(string languageId, uint abiVersion)
    {
        if (abiVersion >= TreeSitterNative.MinCompatibleLanguageAbi && abiVersion <= TreeSitterNative.MaxSupportedLanguageAbi)
        {
            return null;
        }

        return new SemanticDiagnostic(
            "TSNAT002",
            $"Grammar '{languageId}' has ABI version {abiVersion}, outside the runtime-supported range "
            + $"[{TreeSitterNative.MinCompatibleLanguageAbi}, {TreeSitterNative.MaxSupportedLanguageAbi}]. "
            + "Re-generate the grammar against a compatible tree-sitter version (see scripts/build-native-parsers.sh pins).",
            "error");
    }

    public ParsedFileResult Parse(string path, string source, string languageId)
    {
        var normalized = Normalize(languageId);
        if (!_grammars.TryGetValue(normalized, out var grammar))
        {
            return new ParsedFileResult(
                false, path, normalized, Name, HasErrors: false, [], [],
                [new SemanticDiagnostic("TSNAT404", $"No native grammar for language '{languageId}'.", "warning")]);
        }

        using var parser = TsParserHandle.Create();
        if (parser.IsInvalid || !TreeSitterNative.ts_parser_set_language(parser.DangerousGetHandle(), grammar.Language))
        {
            return new ParsedFileResult(
                false, path, normalized, Name, HasErrors: false, [], [],
                [new SemanticDiagnostic("TSNAT005", $"ts_parser_set_language failed for '{normalized}' (grammar ABI {grammar.AbiVersion}).", "error")]);
        }

        var bytes = Encoding.UTF8.GetBytes(source);
        var treePtr = TreeSitterNative.ts_parser_parse_string(parser.DangerousGetHandle(), IntPtr.Zero, bytes, (uint)bytes.Length);
        if (treePtr == IntPtr.Zero)
        {
            return new ParsedFileResult(
                false, path, normalized, Name, HasErrors: false, [], [],
                [new SemanticDiagnostic("TSNAT006", $"tree-sitter returned no tree for '{path}'.", "error")]);
        }

        using var tree = TsTreeHandle.Wrap(treePtr);
        var root = tree.RootNode();
        var hasErrors = TreeSitterNative.ts_node_has_error(root);
        var diagnostics = new List<SemanticDiagnostic>();
        if (hasErrors)
        {
            diagnostics.Add(new SemanticDiagnostic("TSNAT007", $"Source contains syntax errors ({normalized}); extraction may be partial.", "info"));
        }

        // Per-language extractors (P2). The typescript grammar serves both "typescript" and
        // "javascript" language ids (plain JS parses as a TS subset; see track findings).
        IReadOnlyList<ParsedSymbol> symbols = [];
        IReadOnlyList<ParsedEdge> edges = [];
        IReadOnlyList<ParsedCallSite> callSites = [];
        if (grammar.GrammarName == "csharp")
        {
            var query = GetOrCompileExtractionQuery(grammar, CSharpExtractor.QuerySource, diagnostics);
            if (query is not null)
            {
                (symbols, edges, callSites) = CSharpExtractor.Extract(path, bytes.Length, query.ExecuteMatchesOnRoot(root, bytes));
            }
        }
        else if (grammar.GrammarName == "typescript")
        {
            var query = GetOrCompileExtractionQuery(grammar, TypeScriptExtractor.QuerySource, diagnostics);
            if (query is not null)
            {
                (symbols, edges, callSites) = TypeScriptExtractor.Extract(path, query.ExecuteMatchesOnRoot(root, bytes));
            }
        }
        else if (grammar.GrammarName == "java")
        {
            var query = GetOrCompileExtractionQuery(grammar, JavaExtractor.QuerySource, diagnostics);
            if (query is not null)
            {
                (symbols, edges, callSites) = JavaExtractor.Extract(path, bytes.Length, query.ExecuteMatchesOnRoot(root, bytes));
            }
        }

        ParsedJavaSourceSyntax? javaSourceSyntax = null;
        if (grammar.GrammarName == "java")
        {
            var query = GetOrCompileExtractionQuery(grammar, JavaSourceSyntaxExtractor.QuerySource, diagnostics);
            if (query is not null)
            {
                javaSourceSyntax = JavaSourceSyntaxExtractor.Extract(query.ExecuteMatchesOnRoot(root, bytes));
            }
        }

        return new ParsedFileResult(true, path, normalized, Name, hasErrors, symbols, edges, diagnostics)
        {
            CallSites = callSites,
            JavaSourceSyntax = javaSourceSyntax,
        };
    }

    private readonly object _extractionQueryGate = new();
    private readonly Dictionary<string, (TreeSitterQuery? Query, SemanticDiagnostic? Diagnostic)> _extractionQueries = new(StringComparer.Ordinal);

    private TreeSitterQuery? GetOrCompileExtractionQuery(TreeSitterGrammar grammar, string querySource, List<SemanticDiagnostic> diagnostics)
    {
        (TreeSitterQuery? Query, SemanticDiagnostic? Diagnostic) entry;
        lock (_extractionQueryGate)
        {
            var key = $"{grammar.GrammarName}\n{querySource}";
            if (!_extractionQueries.TryGetValue(key, out entry))
            {
                var query = TreeSitterQuery.TryCompile(grammar, querySource, out var diagnostic);
                entry = (query, diagnostic);
                _extractionQueries[key] = entry;
            }
        }

        if (entry.Query is null && entry.Diagnostic is not null)
        {
            diagnostics.Add(entry.Diagnostic);
        }

        return entry.Query;
    }

    /// <summary>Compile a tree-sitter query for a supported language. Returns null with a diagnostic on syntax/availability errors.</summary>
    internal TreeSitterQuery? TryCompileQuery(string languageId, string querySource, out SemanticDiagnostic? diagnostic)
    {
        var normalized = Normalize(languageId);
        if (!_grammars.TryGetValue(normalized, out var grammar))
        {
            diagnostic = new SemanticDiagnostic("TSNAT404", $"No native grammar for language '{languageId}'.", "warning");
            return null;
        }

        return TreeSitterQuery.TryCompile(grammar, querySource, out diagnostic);
    }

    private static void InitializeGrammar(
        string grammarName,
        string libraryName,
        string[] languageIds,
        IReadOnlyList<string>? probeDirectories,
        Dictionary<string, TreeSitterGrammar> grammars,
        List<SemanticDiagnostic> diagnostics)
    {
        if (!TreeSitterNative.TryProbeLibrary(libraryName, probeDirectories, out _))
        {
            diagnostics.Add(new SemanticDiagnostic(
                "TSNAT001",
                $"Native library '{TreeSitterNative.GetPlatformLibraryFileName(libraryName)}' not found; language(s) {string.Join("/", languageIds)} unavailable on the native backend.",
                "warning"));
            return;
        }

        IntPtr language;
        try
        {
            language = grammarName switch
            {
                "csharp" => TreeSitterNative.tree_sitter_c_sharp(),
                "typescript" => TreeSitterNative.tree_sitter_typescript(),
                "java" => TreeSitterNative.tree_sitter_java(),
                _ => IntPtr.Zero,
            };
        }
        catch (DllNotFoundException ex)
        {
            diagnostics.Add(new SemanticDiagnostic("TSNAT001", $"Failed to load grammar library '{libraryName}': {ex.Message}", "warning"));
            return;
        }
        catch (EntryPointNotFoundException ex)
        {
            diagnostics.Add(new SemanticDiagnostic("TSNAT004", $"Grammar entry point missing in '{libraryName}': {ex.Message}", "error"));
            return;
        }

        if (language == IntPtr.Zero)
        {
            diagnostics.Add(new SemanticDiagnostic("TSNAT004", $"Grammar '{grammarName}' returned a null language pointer.", "error"));
            return;
        }

        var abi = TreeSitterNative.ts_language_abi_version(language);
        if (ValidateAbiVersion(grammarName, abi) is { } abiDiagnostic)
        {
            diagnostics.Add(abiDiagnostic);
            return;
        }

        var grammar = new TreeSitterGrammar(grammarName, language, abi);
        foreach (var id in languageIds)
        {
            grammars[id] = grammar;
        }
    }

    private static string Normalize(string languageId) =>
        (languageId ?? "").Trim().ToLowerInvariant();
}

internal sealed record TreeSitterGrammar(string GrammarName, IntPtr Language, uint AbiVersion);

/// <summary>One query capture: capture name, node kind, 1-based line range, matched text and byte range (add-only fields).</summary>
internal sealed record TreeSitterQueryCapture(
    string CaptureName,
    string NodeKind,
    int StartLine,
    int EndLine,
    string Text,
    int StartByte = 0,
    int EndByte = 0);

/// <summary>Captures of one query match, grouped so extractors can pair e.g. a declaration node with its name node.</summary>
internal sealed record TreeSitterQueryMatchResult(
    int PatternIndex,
    IReadOnlyList<TreeSitterQueryCapture> Captures);

/// <summary>
/// Compiled tree-sitter query plus capture iteration — the query infrastructure the
/// per-language extractors (P2) build on. Execute parses the given source with the
/// query's grammar and returns all captures in match order.
/// </summary>
internal sealed class TreeSitterQuery : IDisposable
{
    private readonly TreeSitterGrammar _grammar;
    private readonly TsQueryHandle _query;

    private TreeSitterQuery(TreeSitterGrammar grammar, TsQueryHandle query)
    {
        _grammar = grammar;
        _query = query;
    }

    internal static TreeSitterQuery? TryCompile(TreeSitterGrammar grammar, string querySource, out SemanticDiagnostic? diagnostic)
    {
        var bytes = Encoding.UTF8.GetBytes(querySource);
        var queryPtr = TreeSitterNative.ts_query_new(grammar.Language, bytes, (uint)bytes.Length, out var errorOffset, out var errorType);
        if (queryPtr == IntPtr.Zero)
        {
            diagnostic = new SemanticDiagnostic(
                "TSNAT008",
                $"Query compilation failed for '{grammar.GrammarName}' at byte offset {errorOffset} (error type {errorType}).",
                "error");
            return null;
        }

        diagnostic = null;
        return new TreeSitterQuery(grammar, TsQueryHandle.Wrap(queryPtr));
    }

    public IReadOnlyList<TreeSitterQueryCapture> Execute(string source)
    {
        using var parser = TsParserHandle.Create();
        if (parser.IsInvalid || !TreeSitterNative.ts_parser_set_language(parser.DangerousGetHandle(), _grammar.Language))
        {
            return [];
        }

        var bytes = Encoding.UTF8.GetBytes(source);
        var treePtr = TreeSitterNative.ts_parser_parse_string(parser.DangerousGetHandle(), IntPtr.Zero, bytes, (uint)bytes.Length);
        if (treePtr == IntPtr.Zero)
        {
            return [];
        }

        using var tree = TsTreeHandle.Wrap(treePtr);
        return ExecuteOnRoot(tree.RootNode(), bytes);
    }

    internal IReadOnlyList<TreeSitterQueryCapture> ExecuteOnRoot(TsNode root, byte[] sourceBytes) =>
        ExecuteMatchesOnRoot(root, sourceBytes).SelectMany(match => match.Captures).ToArray();

    /// <summary>Like Execute, but preserves per-match capture grouping (needed to pair declaration/name/parameter captures).</summary>
    public IReadOnlyList<TreeSitterQueryMatchResult> ExecuteMatches(string source)
    {
        using var parser = TsParserHandle.Create();
        if (parser.IsInvalid || !TreeSitterNative.ts_parser_set_language(parser.DangerousGetHandle(), _grammar.Language))
        {
            return [];
        }

        var bytes = Encoding.UTF8.GetBytes(source);
        var treePtr = TreeSitterNative.ts_parser_parse_string(parser.DangerousGetHandle(), IntPtr.Zero, bytes, (uint)bytes.Length);
        if (treePtr == IntPtr.Zero)
        {
            return [];
        }

        using var tree = TsTreeHandle.Wrap(treePtr);
        return ExecuteMatchesOnRoot(tree.RootNode(), bytes);
    }

    internal IReadOnlyList<TreeSitterQueryMatchResult> ExecuteMatchesOnRoot(TsNode root, byte[] sourceBytes)
    {
        var matches = new List<TreeSitterQueryMatchResult>();
        using var cursor = TsQueryCursorHandle.Create();
        TreeSitterNative.ts_query_cursor_exec(cursor.Value, _query.Value, root);
        while (TreeSitterNative.ts_query_cursor_next_match(cursor.Value, out var match))
        {
            var captures = new List<TreeSitterQueryCapture>(match.CaptureCount);
            for (var i = 0; i < match.CaptureCount; i++)
            {
                var capture = Marshal.PtrToStructure<TsQueryCapture>(match.Captures + i * Marshal.SizeOf<TsQueryCapture>());
                captures.Add(ToCapture(capture, sourceBytes));
            }

            matches.Add(new TreeSitterQueryMatchResult(match.PatternIndex, captures));
        }

        return matches;
    }

    private TreeSitterQueryCapture ToCapture(TsQueryCapture capture, byte[] sourceBytes)
    {
        var namePtr = TreeSitterNative.ts_query_capture_name_for_id(_query.Value, capture.Index, out var nameLength);
        var captureName = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(namePtr, (int)nameLength);
        var nodeKind = Marshal.PtrToStringAnsi(TreeSitterNative.ts_node_type(capture.Node)) ?? "";
        var startByte = (int)TreeSitterNative.ts_node_start_byte(capture.Node);
        var endByte = (int)TreeSitterNative.ts_node_end_byte(capture.Node);
        var text = startByte >= 0 && endByte <= sourceBytes.Length && endByte >= startByte
            ? Encoding.UTF8.GetString(sourceBytes, startByte, endByte - startByte)
            : "";
        var startLine = (int)TreeSitterNative.ts_node_start_point(capture.Node).Row + 1;
        var endLine = (int)TreeSitterNative.ts_node_end_point(capture.Node).Row + 1;
        return new TreeSitterQueryCapture(captureName, nodeKind, startLine, endLine, text, startByte, endByte);
    }

    public void Dispose() => _query.Dispose();
}
