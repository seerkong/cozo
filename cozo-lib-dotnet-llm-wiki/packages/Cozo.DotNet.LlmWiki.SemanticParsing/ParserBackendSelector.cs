namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// Orders parsing backends by preference: native → cli → none (design.md §1).
/// This package only reports "no backend" (SelectFor returns null); the regex
/// fallback decision belongs to the Indexing layer.
/// </summary>
public sealed class ParserBackendSelector
{
    private readonly IReadOnlyList<ISemanticParserBackend> _backends;
    private readonly TreeSitterNativeBackend? _native;
    private readonly bool _cliAvailable;
    private readonly string _cliVersion;

    private ParserBackendSelector(
        IReadOnlyList<ISemanticParserBackend> backends,
        TreeSitterNativeBackend? native,
        bool cliAvailable,
        string cliVersion,
        IReadOnlyList<SemanticDiagnostic> diagnostics)
    {
        _backends = backends;
        _native = native;
        _cliAvailable = cliAvailable;
        _cliVersion = cliVersion;
        Diagnostics = diagnostics;
    }

    public bool NativeAvailable => _native is not null;

    public bool CliAvailable => _cliAvailable;

    /// <summary>Why unavailable backends are unavailable (missing dylib, ABI mismatch, missing CLI…).</summary>
    public IReadOnlyList<SemanticDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Build the default chain. nativeProbeDirectories overrides where native dylibs are
    /// searched (tests use this to simulate a missing-library environment).
    /// </summary>
    public static ParserBackendSelector CreateDefault(
        IReadOnlyList<string>? nativeProbeDirectories = null,
        TreeSitterCliParser? cliParser = null)
    {
        var diagnostics = new List<SemanticDiagnostic>();
        var backends = new List<ISemanticParserBackend>();

        var native = TreeSitterNativeBackend.TryCreate(out var nativeDiagnostics, nativeProbeDirectories);
        diagnostics.AddRange(nativeDiagnostics);
        if (native is not null)
        {
            backends.Add(native);
        }

        var parser = cliParser ?? new TreeSitterCliParser();
        var cliStatus = parser.GetStatusAsync().GetAwaiter().GetResult();
        diagnostics.AddRange(cliStatus.Diagnostics);
        if (cliStatus.Available)
        {
            backends.Add(new TreeSitterCliBackend(parser));
        }

        return new ParserBackendSelector(backends, native, cliStatus.Available, cliStatus.Version, diagnostics);
    }

    /// <summary>First available backend supporting the language, or null when none does.</summary>
    public ISemanticParserBackend? SelectFor(string languageId) =>
        _backends.FirstOrDefault(backend => backend.SupportsLanguage(languageId));

    /// <summary>
    /// Availability report in the parser_status shape. Existing fields keep their CLI-era
    /// semantics; native availability is exposed through the add-only fields.
    /// </summary>
    public ParserStatus DescribeStatus()
    {
        var nativeDetail = _native is null
            ? ""
            : string.Join("; ", _native.LoadedGrammars.Select(g => $"{g.LanguageId} abi={g.AbiVersion}"));
        return new ParserStatus(
            NativeAvailable || CliAvailable,
            "tree-sitter",
            NativeAvailable ? "native" : _cliVersion,
            Diagnostics,
            NativeAvailable,
            nativeDetail);
    }
}
