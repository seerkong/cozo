namespace Cozo.DotNet.LlmWiki.Core;

public sealed record LlmWikiStoreOptions(
    string Engine = "mem",
    string Path = "");

public enum RepositoryDocSymbolLinkMode
{
    Off,
    Local,
    Global,
}

/// <summary>
/// Parser backend selection for repository indexing (T3.1).
/// Auto prefers tree-sitter (native → cli chain) per file with regex fallback;
/// RegexOnly forces the legacy regex extraction tier (baseline / troubleshooting switch).
/// </summary>
public enum RepositoryParserMode
{
    Auto,
    RegexOnly,
}

public sealed record RepositoryIndexRequest(
    string RepositoryPath,
    string? RepositoryId = null,
    string? RepositoryName = null,
    IReadOnlyList<string>? IncludeExtensions = null,
    IReadOnlyList<string>? ExcludeDirectories = null,
    int MaxFileBytes = 512_000,
    bool UseGitIgnore = true,
    RepositoryDocSymbolLinkMode DocSymbolLinkMode = RepositoryDocSymbolLinkMode.Local,
    int MaxDocLinksPerDoc = 20,
    int MaxInferredRelations = 200_000,
    RepositoryParserMode ParserMode = RepositoryParserMode.Auto,
    // call-resolution track (design.md §3, add-only): ACCESSES edges are on by default but
    // bounded per file; EmitAccessEdges=false switches them off entirely.
    bool EmitAccessEdges = true,
    int MaxAccessEdgesPerFile = 200,
    // roslyn-csharp-resolution track (design.md §2, add-only): Roslyn lightweight-compilation
    // semantic enhancement for C# — overrides tree-sitter CALLS edges per call point and
    // enriches symbol doc_ids. Off ⇒ output is exactly the tree-sitter baseline.
    bool EnableRoslynEnhancement = true,
    // add-llm-wiki-community-detection track (design.md §4, add-only): recompute the
    // ck_community/ck_member partition at the tail of IndexAsync after the facts are written.
    // Off ⇒ no community rows are produced; a detection failure never fails the index
    // (community_failed diagnostic instead).
    bool ComputeCommunities = true,
    // add-llm-wiki-process-extraction track (design.md §3, add-only): detect syntax-level
    // entry-point candidates (http_route / mcp_tool) during batch building and run
    // ExtractProcessesAsync at the tail of IndexAsync (after community detection). Off ⇒ no
    // ck_entry_point/ck_process/ck_process_step rows are produced; an extraction failure never
    // fails the index (process_failed diagnostic instead).
    bool ExtractProcesses = true,
    // add-llm-wiki-incremental-indexing track (design.md §4, add-only): "auto" (default) takes
    // the file-level incremental path when a prior index of the same repository exists (ck_file
    // hash baseline); "full" forces the legacy full rewrite. With no baseline auto falls back to
    // full — behavior identical to the pre-incremental indexer.
    string IncrementalMode = "auto",
    // CodeKnowledge schema data is recomputable. Explicit reindex permits its rebuild-style migration.
    bool Reindex = false);

public sealed record RepositoryIndexSummary(
    string RepositoryId,
    string RepositoryPath,
    string Commit,
    int Files,
    int Symbols,
    int Relations,
    int DocBlocks,
    IReadOnlyList<string> SkippedFiles)
{
    /// <summary>Call/new sites seen by the resolution pipeline (call-resolution track, design.md §5; add-only).</summary>
    public int CallSites { get; init; }

    /// <summary>Call/new sites that produced a CALLS edge (any confidence tier).</summary>
    public int ResolvedCalls { get; init; }

    /// <summary>Call/new sites with no in-repo candidate — counted, never edged (design decision: BCL and dynamic targets).</summary>
    public int UnresolvedCalls { get; init; }

    /// <summary>Roslyn semantic-hit call edges merged into the graph at confidence 1.0 (roslyn-csharp-resolution track, add-only).</summary>
    public int RoslynResolvedCalls { get; init; }

    /// <summary>Roslyn candidate-only call edges merged at confidence 0.6 (evidence carries the CandidateReason).</summary>
    public int RoslynCandidateCalls { get; init; }

    /// <summary>Roslyn call sites whose target lives outside the repository (BCL/external assemblies) — counted, never edged.</summary>
    public int RoslynExternalCalls { get; init; }

    /// <summary>Roslyn edges whose caller/callee DocId could not be mapped to a baseline symbol — baseline edge kept.</summary>
    public int RoslynUnmappedCalls { get; init; }

    /// <summary>Communities persisted by the post-index community detection (community-detection track, add-only; 0 when the switch is off or detection failed).</summary>
    public int Communities { get; init; }

    /// <summary>Symbols dropped as noise (community below MinCommunitySize) by the post-index community detection (add-only diagnostic count).</summary>
    public int NoiseSymbols { get; init; }

    /// <summary>Entry points persisted to ck_entry_point by the post-index process extraction (process-extraction track, add-only; 0 when the switch is off or extraction failed).</summary>
    public int EntryPoints { get; init; }

    /// <summary>Processes persisted to ck_process by the post-index process extraction (add-only; 0 when the switch is off or extraction failed).</summary>
    public int Processes { get; init; }

    /// <summary>Entry points traversed but dropped because the reachable chain was shorter than MinSteps (add-only diagnostic count).</summary>
    public int DroppedProcesses { get; init; }

    /// <summary>True when this index run took the file-level incremental path (incremental-indexing track, add-only). False on full runs and auto fallbacks.</summary>
    public bool IncrementalUsed { get; init; }

    /// <summary>Files whose content changed or that are new since the previous index (incremental runs only; 0 otherwise).</summary>
    public int ChangedFiles { get; init; }

    /// <summary>Files present in the previous index but gone now — their facts were removed (incremental runs only; 0 otherwise).</summary>
    public int RemovedFiles { get; init; }

    /// <summary>Files whose hash was unchanged so their per-file facts were reused (incremental runs only; 0 otherwise).</summary>
    public int ReusedFiles { get; init; }
}

public sealed record SourceFileSnapshot(
    string FileId,
    string RelativePath,
    string AbsolutePath,
    string Language,
    string Hash,
    long SizeBytes,
    DateTimeOffset UpdatedAt);

public sealed record ExtractedSymbol(
    string SymbolId,
    string FileId,
    string Name,
    string Kind,
    int StartLine,
    int EndLine,
    string Signature);

public sealed record ExtractedRelation(
    string FromId,
    string ToId,
    string Kind,
    string? FileId = null,
    int Line = 0,
    string Evidence = "");

public sealed record ExtractedDocBlock(
    string DocId,
    string FileId,
    string Anchor,
    string Text,
    string Hash,
    DateTimeOffset UpdatedAt);

public sealed record WikiBuildRequest(
    string? OutputDirectory = null,
    bool WriteFiles = false);

public sealed record WikiBuildResult(
    string IndexMarkdown,
    IReadOnlyList<WikiPageDocument> Pages,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> MissingDocs);

public sealed record WikiPageDocument(
    string PageId,
    string Title,
    string Markdown,
    IReadOnlyList<string> SourceFileIds,
    IReadOnlyList<string> SymbolIds,
    IReadOnlyList<string> DocIds);

public sealed record LlmWikiToolResult(
    bool Success,
    string Message,
    object? Data = null);
