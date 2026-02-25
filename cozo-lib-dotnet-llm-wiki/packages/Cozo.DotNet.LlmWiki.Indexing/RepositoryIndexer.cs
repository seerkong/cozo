using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.SemanticParsing;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Indexing;

public sealed class RepositoryIndexer
{
    // Regex extraction tier (design.md §6): heuristic edges carry the lowest confidence.
    private const double RegexConfidence = 0.3;
    private const string RegexResolver = "regex";

    // tree-sitter primary path (track add-llm-wiki-treesitter-ingestion, design.md §4).
    private const string TreeSitterResolver = "treesitter";

    // Backend probing (native dylib load + CLI probe) is process-wide work; share one selector.
    private static readonly Lazy<ParserBackendSelector> SharedSelector =
        new(() => ParserBackendSelector.CreateDefault(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ParserBackendSelector? _backendSelector;

    public RepositoryIndexer(ParserBackendSelector? backendSelector = null)
    {
        _backendSelector = backendSelector;
    }

    private ParserBackendSelector Selector => _backendSelector ?? SharedSelector.Value;

    /// <summary>
    /// Community-detection seam (add-llm-wiki-community-detection track): tests inject a
    /// throwing detector to exercise the community_failed failure path. Null (production)
    /// means <see cref="CozoOmCodeGraphExtensions.ComputeCommunitiesAsync"/> with default options.
    /// Internal by design — the public knob is <see cref="RepositoryIndexRequest.ComputeCommunities"/>.
    /// </summary>
    internal Func<CozoOm, CancellationToken, Task<CommunityDetectionResult>>? CommunityDetector { get; init; }

    /// <summary>
    /// Process-extraction seam (add-llm-wiki-process-extraction track): tests inject a throwing
    /// extractor to exercise the process_failed failure path. Null (production) means
    /// <see cref="CozoOmProcessExtensions.ExtractProcessesAsync"/> with default options.
    /// Internal by design — the public knob is <see cref="RepositoryIndexRequest.ExtractProcesses"/>.
    /// </summary>
    internal Func<CozoOm, CancellationToken, Task<ProcessExtractionResult>>? ProcessExtractor { get; init; }

    /// <summary>
    /// Git-capsule seam (add-llm-wiki-detect-changes track, design.md §2): tests inject a fake
    /// provider; null (production) means <see cref="GitCliDiffProvider.Default"/>. IndexAsync
    /// records the HEAD commit as ck_meta.indexed_commit so status/detect_changes can compare it
    /// against the live HEAD (stale hint). Non-git directories simply skip the write.
    /// </summary>
    internal IGitDiffProvider? GitDiffProvider { get; init; }

    /// <summary>
    /// Search-index seam (add-llm-wiki-hybrid-search track): tests inject a throwing indexer to
    /// exercise the search_index_failed failure path. Null (production) means
    /// <see cref="Cozo.DotNet.LlmWiki.VectorSearch.CozoVectorSearchService.EnsureSearchTextAsync"/>
    /// followed by <see cref="Cozo.DotNet.LlmWiki.VectorSearch.CozoVectorSearchService.EnsureFtsIndexAsync"/>.
    /// </summary>
    internal Func<CozoOm, CancellationToken, Task>? SearchTextIndexer { get; init; }

    private static readonly Regex CsharpSymbol = new(
        @"^\s*(?:public|private|protected|internal|static|sealed|abstract|partial|async|readonly|\s)*\s*(class|interface|record|struct|enum|delegate|namespace)\s+([A-Za-z_][A-Za-z0-9_\.]*)|^\s*(?:public|private|protected|internal|static|virtual|override|async|sealed|partial|\s)+[\w<>\[\]\?\.]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex JsTsSymbol = new(
        @"^\s*(?:export\s+)?(?:async\s+)?function\s+([A-Za-z_][A-Za-z0-9_]*)|^\s*(?:export\s+)?(?:class|interface|type|enum)\s+([A-Za-z_][A-Za-z0-9_]*)|^\s*(?:export\s+)?const\s+([A-Za-z_][A-Za-z0-9_]*)\s*=",
        RegexOptions.Compiled);

    private static readonly Regex ImportLine = new(
        @"^\s*(?:using\s+([A-Za-z0-9_\.]+)\s*;|import\s+.*?from\s+['""]([^'""]+)['""]|import\s+['""]([^'""]+)['""])",
        RegexOptions.Compiled);

    private static readonly Regex IdentifierToken = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    private static readonly HashSet<string> AmbiguousSymbolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "ID", "Run", "Get", "Set", "Add", "Use", "New", "Old", "Map", "Key", "Value", "Data", "Item", "Node", "Edge", "File", "Path", "Name", "Type", "Kind", "Test"
    };

    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".md", ".mdx", ".json", ".xml", ".csproj", ".sln", ".slnx"
    };

    private static readonly HashSet<string> DefaultExcludeDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "bin", "obj", "node_modules", "dist", "build", ".next", ".cache", ".codex", ".claude"
    };

    public async Task<RepositoryIndexSummary> IndexAsync(
        CozoOm om,
        RepositoryIndexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(request);

        var batch = await BuildBatchAsync(request, cancellationToken);
        await om.InitCodeKnowledgeAsync(cancellationToken);

        // add-llm-wiki-incremental-indexing track (design.md §1/§2/§4): auto mode goes
        // incremental when a ck_file hash baseline exists for this repository; otherwise (or on
        // IncrementalMode="full") the legacy full write runs unchanged. Parsing/resolution stayed
        // full either way (BuildBatchAsync above) — the incremental path only narrows the fact
        // delete/write set to the affected files plus their edge-endpoint files.
        IncrementalDiff.DiffResult? diff = null;
        if (!string.Equals(request.IncrementalMode, "full", StringComparison.OrdinalIgnoreCase))
        {
            var baseline = await IncrementalDiff.LoadBaselineAsync(om, batch.RepositoryId, cancellationToken);
            if (baseline.Count > 0)
            {
                diff = IncrementalDiff.Compute(baseline, batch.Batch.Files ?? []);
            }
        }

        if (diff is null)
        {
            await om.IndexCodeKnowledgeAsync(batch.Batch, cancellationToken);
        }
        else
        {
            await ApplyIncrementalAsync(om, batch.Batch, diff, cancellationToken);
        }

        // add-llm-wiki-detect-changes track (design.md §2): record the indexed HEAD commit so the
        // status/detect_changes surface can raise a stale hint when HEAD moves on. A non-git
        // directory (TryGetHeadCommit == null) writes nothing — no error, no diagnostic.
        var headCommit = (GitDiffProvider ?? GitCliDiffProvider.Default)
            .TryGetHeadCommit(Path.GetFullPath(request.RepositoryPath));
        if (!string.IsNullOrEmpty(headCommit))
        {
            await om.Runtime.Store.RunAsync(
                """
                ?[key, value] <- [["indexed_commit", $commit]]
                :put ck_meta {key => value}
                """,
                new Dictionary<string, object?> { ["commit"] = headCommit },
                cancellationToken: cancellationToken);
        }

        // add-llm-wiki-community-detection track (design.md §4): recompute the community
        // partition after the facts are written. A failure degrades to a community_failed
        // diagnostic and never fails the index.
        var (communities, noiseSymbols) = request.ComputeCommunities
            ? await ComputeCommunitiesStepAsync(om, batch.RepositoryId, cancellationToken)
            : (0, 0);

        // add-llm-wiki-process-extraction track (design.md §3): extract execution flows after
        // community detection (cross_community marking reads ck_member). A failure degrades to
        // a process_failed diagnostic and never fails the index.
        var (entryPoints, processes, droppedProcesses) = request.ExtractProcesses
            ? await ExtractProcessesStepAsync(om, batch.RepositoryId, cancellationToken)
            : (0, 0, 0);

        // add-llm-wiki-hybrid-search track (design.md §5): rebuild the ck_search_text FTS
        // projection after all facts are written. A failure degrades to a search_index_failed
        // diagnostic and never fails the index.
        await BuildSearchIndexStepAsync(om, batch.RepositoryId, cancellationToken);

        // Whole-repo totals from the (always fully built) batch: identical to the previous
        // result-based counts on the full path, and the meaningful totals on the incremental
        // path where only a subset of facts was physically written.
        return new RepositoryIndexSummary(
            batch.RepositoryId,
            Path.GetFullPath(request.RepositoryPath),
            batch.Commit,
            batch.Batch.Files?.Count ?? 0,
            batch.Batch.Symbols?.Count ?? 0,
            batch.Batch.Edges?.Count ?? 0,
            batch.Batch.DocBlocks?.Count ?? 0,
            batch.SkippedFiles)
        {
            IncrementalUsed = diff is not null,
            ChangedFiles = diff is null ? 0 : diff.Added.Count + diff.Changed.Count,
            RemovedFiles = diff?.RemovedFileIds.Count ?? 0,
            ReusedFiles = diff?.Reused.Count ?? 0,
            CallSites = batch.CallSites,
            ResolvedCalls = batch.ResolvedCalls,
            UnresolvedCalls = batch.UnresolvedCalls,
            RoslynResolvedCalls = batch.RoslynResolvedCalls,
            RoslynCandidateCalls = batch.RoslynCandidateCalls,
            RoslynExternalCalls = batch.RoslynExternalCalls,
            RoslynUnmappedCalls = batch.RoslynUnmappedCalls,
            Communities = communities,
            NoiseSymbols = noiseSymbols,
            EntryPoints = entryPoints,
            Processes = processes,
            DroppedProcesses = droppedProcesses,
        };
    }

    /// <summary>
    /// Incremental write step (incremental-indexing track, design.md §2): removes and rewrites
    /// facts at file granularity. The rewrite set is the changed/added files expanded by every
    /// unchanged file that owns an edge touching an affected file's symbols — on the old side
    /// (stale edges into now-gone symbol ids must be deleted with their owner) and on the new
    /// side (fresh edges into re-created symbol ids must be written even though their owner's
    /// content did not change). One expansion round suffices: expanded files are content-unchanged,
    /// so re-putting their facts is idempotent and never invalidates a third file.
    /// </summary>
    private static async Task ApplyIncrementalAsync(
        CozoOm om,
        CodeKnowledgeBatch batch,
        IncrementalDiff.DiffResult diff,
        CancellationToken cancellationToken)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in diff.Added)
        {
            affected.Add(file.FileId);
        }

        foreach (var file in diff.Changed)
        {
            affected.Add(file.FileId);
        }

        var removed = new HashSet<string>(diff.RemovedFileIds, StringComparer.Ordinal);
        var writeSet = new HashSet<string>(affected, StringComparer.Ordinal);

        // Old-side expansion: files owning ck_edge rows whose endpoint is a symbol of an
        // affected/removed file (queried before any deletion happens).
        var oldTouched = affected.Concat(removed).ToArray();
        if (oldTouched.Length > 0)
        {
            var rows = await om.Runtime.Store.RunAsync(
                """
                fids[fid] <- $file_ids
                sym[s] := *ck_symbol{ symbol_id: s, file_id: f }, fids[f]
                ?[file_id] := *ck_edge{ to_id, file_id }, sym[to_id]
                ?[file_id] := *ck_edge{ from_id, file_id }, sym[from_id]
                ?[file_id] := *ck_edge{ to_id, file_id }, fids[to_id]
                """,
                new Dictionary<string, object?> { ["file_ids"] = oldTouched.Select(id => new List<object?> { id }).ToList() },
                cancellationToken: cancellationToken);
            foreach (var row in rows.Rows)
            {
                var fileId = row.Count > 0 && row[0].ValueKind == System.Text.Json.JsonValueKind.String ? row[0].GetString() : null;
                if (!string.IsNullOrEmpty(fileId) && !removed.Contains(fileId))
                {
                    writeSet.Add(fileId);
                }
            }
        }

        // New-side expansion: batch edges owned by an unchanged file but touching a re-created
        // symbol (or the file node) of an affected file.
        var affectedSymbolIds = (batch.Symbols ?? [])
            .Where(symbol => affected.Contains(symbol.FileId))
            .Select(symbol => symbol.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var edge in batch.Edges ?? [])
        {
            if (edge.FileId.Length > 0 && !writeSet.Contains(edge.FileId) &&
                (affectedSymbolIds.Contains(edge.FromId) || affectedSymbolIds.Contains(edge.ToId) ||
                 affected.Contains(edge.FromId) || affected.Contains(edge.ToId)))
            {
                writeSet.Add(edge.FileId);
            }
        }

        await om.RemoveFileFactsAsync(writeSet.Concat(removed).ToArray(), removed.ToArray(), cancellationToken);

        var writeSymbolIds = (batch.Symbols ?? [])
            .Where(symbol => writeSet.Contains(symbol.FileId))
            .Select(symbol => symbol.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = new CodeKnowledgeBatch(
            Repositories: batch.Repositories,
            Files: (batch.Files ?? []).Where(file => writeSet.Contains(file.FileId)).ToArray(),
            Symbols: (batch.Symbols ?? []).Where(symbol => writeSet.Contains(symbol.FileId)).ToArray(),
            Edges: (batch.Edges ?? [])
                .Where(edge => writeSet.Contains(edge.FileId) || writeSet.Contains(edge.FromId) || writeSymbolIds.Contains(edge.FromId))
                .ToArray(),
            DocBlocks: (batch.DocBlocks ?? []).Where(doc => writeSet.Contains(doc.FileId)).ToArray(),
            Diagnostics: (batch.Diagnostics ?? [])
                .Where(diagnostic => writeSet.Contains(diagnostic.TargetId) || writeSymbolIds.Contains(diagnostic.TargetId))
                .ToArray(),
            EntryPoints: (batch.EntryPoints ?? []).Where(entry => writeSymbolIds.Contains(entry.SymbolId)).ToArray(),
            ExternalCalls: (batch.ExternalCalls ?? []).Where(call => writeSymbolIds.Contains(call.CallerId)).ToArray());
        await om.IndexCodeKnowledgeAsync(filtered, cancellationToken);
    }

    /// <summary>
    /// Process-extraction tail step (process-extraction track, design.md §3): detects graph-level
    /// entry points, merges the syntax-level candidates already written to ck_entry_point by the
    /// batch, and persists ck_process/ck_process_step. Any failure is caught and recorded as a
    /// process_failed warning diagnostic — the index result stands unchanged and the process
    /// counters report zero.
    /// </summary>
    private async Task<(int EntryPoints, int Processes, int DroppedProcesses)> ExtractProcessesStepAsync(
        CozoOm om,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = ProcessExtractor is { } extractor
                ? await extractor(om, cancellationToken)
                : await om.ExtractProcessesAsync(cancellationToken: cancellationToken);
            return (result.EntryPoints.Count, result.Processes.Count, result.DroppedProcesses);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await om.Runtime.Store.RunAsync(
                """
                ?[diagnostic_id, target_id, kind, message, severity] <-
                  [[$diagnostic_id, $target_id, $kind, $message, $severity]]
                :put ck_diagnostic {diagnostic_id => target_id, kind, message, severity}
                """,
                new Dictionary<string, object?>
                {
                    ["diagnostic_id"] = $"diag:process-failed:{repositoryId}",
                    ["target_id"] = repositoryId,
                    ["kind"] = "process_failed",
                    ["message"] = ex.Message,
                    ["severity"] = "warning",
                },
                cancellationToken: cancellationToken);
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// Community-detection tail step (community-detection track, design.md §4): recomputes
    /// ck_community/ck_member over the freshly written graph. Any failure is caught and
    /// recorded as a community_failed warning diagnostic — the index result stands unchanged
    /// and the community counters report zero.
    /// </summary>
    private async Task<(int Communities, int NoiseSymbols)> ComputeCommunitiesStepAsync(
        CozoOm om,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = CommunityDetector is { } detector
                ? await detector(om, cancellationToken)
                : await om.ComputeCommunitiesAsync(cancellationToken: cancellationToken);
            return (result.Communities.Count, result.NoiseSymbols);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await om.Runtime.Store.RunAsync(
                """
                ?[diagnostic_id, target_id, kind, message, severity] <-
                  [[$diagnostic_id, $target_id, $kind, $message, $severity]]
                :put ck_diagnostic {diagnostic_id => target_id, kind, message, severity}
                """,
                new Dictionary<string, object?>
                {
                    ["diagnostic_id"] = $"diag:community-failed:{repositoryId}",
                    ["target_id"] = repositoryId,
                    ["kind"] = "community_failed",
                    ["message"] = ex.Message,
                    ["severity"] = "warning",
                },
                cancellationToken: cancellationToken);
            return (0, 0);
        }
    }

    /// <summary>
    /// Search-index tail step (hybrid-search track, design.md §5): rebuilds the ck_search_text
    /// FTS projection from the freshly written ck_symbol/ck_doc_block facts and ensures the BM25
    /// index (the second ::fts create is a swallowed create conflict, so re-indexing stays
    /// idempotent). Any failure is caught and recorded as a search_index_failed warning
    /// diagnostic — the index result stands unchanged.
    /// </summary>
    private async Task BuildSearchIndexStepAsync(
        CozoOm om,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (SearchTextIndexer is { } indexer)
            {
                await indexer(om, cancellationToken);
            }
            else
            {
                await VectorSearch.CozoVectorSearchService.EnsureSearchTextAsync(om, cancellationToken);
                await VectorSearch.CozoVectorSearchService.EnsureFtsIndexAsync(om, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await om.Runtime.Store.RunAsync(
                """
                ?[diagnostic_id, target_id, kind, message, severity] <-
                  [[$diagnostic_id, $target_id, $kind, $message, $severity]]
                :put ck_diagnostic {diagnostic_id => target_id, kind, message, severity}
                """,
                new Dictionary<string, object?>
                {
                    ["diagnostic_id"] = $"diag:search-index-failed:{repositoryId}",
                    ["target_id"] = repositoryId,
                    ["kind"] = "search_index_failed",
                    ["message"] = ex.Message,
                    ["severity"] = "warning",
                },
                cancellationToken: cancellationToken);
        }
    }

    public async Task<RepositoryBatchResult> BuildBatchAsync(
        RepositoryIndexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        }

        var repoId = SanitizeId(request.RepositoryId) ?? $"repo:{HashText(root)}";
        var repoName = request.RepositoryName ?? new DirectoryInfo(root).Name;
        var commit = TryGetGitCommit(root);
        var include = request.IncludeExtensions is { Count: > 0 }
            ? new HashSet<string>(request.IncludeExtensions.Select(NormalizeExtension), StringComparer.OrdinalIgnoreCase)
            : DefaultExtensions;
        var excluded = new HashSet<string>(DefaultExcludeDirectories, StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.ExcludeDirectories ?? [])
        {
            excluded.Add(item);
        }

        var files = new List<CodeFileFact>();
        var symbols = new List<CodeSymbolFact>();
        var edges = new List<CodeEdgeFact>();
        var docs = new List<CodeDocBlockFact>();
        var diagnostics = new List<CodeDiagnosticFact>();
        var skipped = new List<string>();
        var parsedFiles = new List<CallResolver.FileInput>();
        // roslyn-csharp-resolution track (design.md §2): every indexed C# source feeds one
        // lightweight compilation, regardless of which per-file extraction tier produced the facts.
        var csharpSources = new List<(string Path, string Source)>();
        var csharpFileIdByPath = new Dictionary<string, string>(StringComparer.Ordinal);

        var ignoreMatcher = request.UseGitIgnore ? GitIgnoreMatcher.Load(root) : GitIgnoreMatcher.Empty;
        foreach (var file in EnumerateFiles(root, excluded, ignoreMatcher))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (ignoreMatcher.IsIgnored(relative, isDirectory: false))
            {
                skipped.Add(relative);
                continue;
            }

            var ext = Path.GetExtension(file);
            if (!include.Contains(ext))
            {
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > request.MaxFileBytes)
            {
                skipped.Add(relative);
                continue;
            }

            var text = await File.ReadAllTextAsync(file, cancellationToken);
            var language = DetectLanguage(ext);
            var fileId = $"file:{repoId}:{relative}";
            var hash = HashText(text);
            files.Add(new CodeFileFact(fileId, repoId, relative, language, hash, info.LastWriteTimeUtc.ToString("O")));

            if (language is "csharp")
            {
                csharpSources.Add((relative, text));
                csharpFileIdByPath.TryAdd(relative, fileId);
            }

            if (language is "markdown")
            {
                ExtractImports(fileId, relative, text, edges);
                ExtractMarkdown(fileId, relative, text, hash, info.LastWriteTimeUtc, docs);
            }
            else if (request.ParserMode == RepositoryParserMode.Auto &&
                     TryExtractWithTreeSitter(fileId, relative, language, text, symbols, edges, diagnostics, parsedFiles))
            {
                // tree-sitter succeeded: its IMPORTS edges supersede the regex import scan for this
                // file (same `import:{hash(target)}` id space, higher confidence — merge by omission).
            }
            else
            {
                ExtractImports(fileId, relative, text, edges);
                ExtractSymbols(fileId, relative, language, text, symbols, edges);
            }
        }

        // call-resolution track (design.md §3–§5): whole-batch pass so cross-file registries see
        // every tree-sitter fact; CALLS/ACCESSES/METHOD_OVERRIDES edges are appended in place.
        var callStats = CallResolver.Resolve(
            parsedFiles,
            symbols,
            edges,
            diagnostics,
            request.EmitAccessEdges,
            request.MaxAccessEdgesPerFile);

        // roslyn-csharp-resolution track (design.md §2): semantic enhancement after the tree-sitter
        // call resolution — per-call-point override, doc_id enrichment, add-only counters. The
        // switch (default on) restores the exact baseline when off; failures degrade to a
        // roslyn_failed diagnostic inside the merge step and never fail the index.
        // depa-ontology track (design §4.2): out-of-repo call summaries aggregated by the merge
        // step land in ck_external_call. tree-sitter-only unresolved call sites carry a bare name
        // (no FQN), so only the Roslyn path feeds this channel (recorded as the summary's fidelity
        // boundary in the track findings).
        var externalCallFacts = new List<CodeExternalCallFact>();
        var roslynStats = request.EnableRoslynEnhancement && csharpSources.Count > 0
            ? RoslynMergeStep.Enhance(csharpSources, csharpFileIdByPath, symbols, edges, diagnostics, repoId, externalCalls: externalCallFacts)
            : RoslynMergeStep.RoslynMergeStats.Empty;

        // process-extraction track (design.md §1/§3): syntax-level entry-point candidates
        // (http_route / mcp_tool) are detectable only here, where the parsed call sites still
        // exist. Gated on the same switch as the extraction so ExtractProcesses=false leaves
        // ck_entry_point completely empty (behavior delta: pipeline-switch).
        IReadOnlyList<CodeEntryPointFact> entryPoints = request.ExtractProcesses
            ? EntryPointCandidates.Detect(parsedFiles, symbols)
            : [];

        LinkDocsToSymbols(symbols, docs, files, request, edges);
        var batch = new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact(repoId, root, repoName, commit)],
            Files: files,
            Symbols: symbols,
            Edges: edges,
            DocBlocks: docs,
            Diagnostics: diagnostics,
            EntryPoints: entryPoints,
            ExternalCalls: externalCallFacts);
        return new RepositoryBatchResult(repoId, commit, batch, skipped)
        {
            CallSites = callStats.CallSites,
            ResolvedCalls = callStats.ResolvedCalls,
            UnresolvedCalls = callStats.UnresolvedCalls,
            RoslynResolvedCalls = roslynStats.ResolvedCalls,
            RoslynCandidateCalls = roslynStats.CandidateCalls,
            RoslynExternalCalls = roslynStats.ExternalCalls,
            RoslynUnmappedCalls = roslynStats.UnmappedCalls,
        };
    }

    private static IEnumerable<string> EnumerateFiles(string root, HashSet<string> excluded, GitIgnoreMatcher ignoreMatcher)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var childDir in Directory.EnumerateDirectories(dir))
            {
                var relative = Path.GetRelativePath(root, childDir).Replace(Path.DirectorySeparatorChar, '/');
                if (!excluded.Contains(Path.GetFileName(childDir)) &&
                    !ignoreMatcher.IsIgnored(relative, isDirectory: true))
                {
                    stack.Push(childDir);
                }
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// tree-sitter primary path (design.md §4). Returns true when the backend produced usable
    /// facts (they are appended); false means the caller must run the regex fallback. A parse
    /// exception, a failed parse, or an error-ridden parse with zero symbols degrades this file
    /// only — the overall index never fails because of a single file.
    /// </summary>
    private bool TryExtractWithTreeSitter(
        string fileId,
        string relativePath,
        string language,
        string text,
        List<CodeSymbolFact> symbols,
        List<CodeEdgeFact> edges,
        List<CodeDiagnosticFact> diagnostics,
        List<CallResolver.FileInput> parsedFiles)
    {
        ISemanticParserBackend? backend;
        try
        {
            backend = Selector.SelectFor(language);
        }
        catch (Exception ex)
        {
            diagnostics.Add(FallbackDiagnostic(fileId, $"backend selection failed: {ex.Message}"));
            return false;
        }

        if (backend is null)
        {
            return false;
        }

        ParsedFileResult parsed;
        try
        {
            parsed = backend.Parse(relativePath, text, language);
        }
        catch (Exception ex)
        {
            diagnostics.Add(FallbackDiagnostic(fileId, $"{backend.Name} parse threw: {ex.Message}"));
            return false;
        }

        // Zero extracted facts (e.g. cli backend: parse-only, no symbol extraction) or a failed /
        // fully-broken parse: fall back to regex so behavior matches the pre-tree-sitter path.
        if (!parsed.Success || (parsed.Symbols.Count == 0 && parsed.Edges.Count == 0))
        {
            if (!parsed.Success || parsed.HasErrors)
            {
                diagnostics.Add(FallbackDiagnostic(
                    fileId,
                    $"{backend.Name} parse degraded to regex (success={parsed.Success}, hasErrors={parsed.HasErrors}): "
                    + string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))));
            }

            return false;
        }

        AppendTreeSitterFacts(fileId, relativePath, language, parsed, symbols, edges);
        // Feed the whole-batch call-resolution pass (call-resolution track): it needs every
        // parsed file's call sites plus the raw text for the text-level local binding table.
        parsedFiles.Add(new CallResolver.FileInput(fileId, relativePath, language, text, parsed));
        return true;
    }

    private static CodeDiagnosticFact FallbackDiagnostic(string fileId, string message) =>
        new($"diag:parser-fallback:{fileId}", fileId, "parser_fallback", message, "warning");

    /// <summary>
    /// Converts ParsedSymbol tree / ParsedEdge list into v2 facts. Symbol ids keep the existing
    /// `symbol:{fileId}:{startLine}:{name}` convention so the 12-tool surface stays id-compatible
    /// with the regex tier; nesting is carried via ParentId instead of synthetic file edges.
    /// </summary>
    private static void AppendTreeSitterFacts(
        string fileId,
        string relativePath,
        string language,
        ParsedFileResult parsed,
        List<CodeSymbolFact> symbols,
        List<CodeEdgeFact> edges)
    {
        var idByQualified = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineByQualified = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        void Walk(ParsedSymbol symbol, string parentId, string parentQualified)
        {
            var qualified = QualifiedNameOf(symbol, parentQualified);
            var symbolId = $"symbol:{fileId}:{symbol.StartLine}:{symbol.Name}";
            if (!usedIds.Add(symbolId))
            {
                var suffix = 2;
                string candidate;
                do
                {
                    candidate = $"{symbolId}:{suffix++}";
                }
                while (!usedIds.Add(candidate));
                symbolId = candidate;
            }

            idByQualified.TryAdd(qualified, symbolId);
            lineByQualified.TryAdd(qualified, symbol.StartLine);
            symbols.Add(new CodeSymbolFact(
                symbolId,
                fileId,
                symbol.Name,
                symbol.Kind,
                symbol.StartLine,
                symbol.EndLine,
                symbol.Signature,
                ParentId: parentId,
                Lang: language,
                Visibility: symbol.Visibility,
                Exported: symbol.Exported,
                SymKey: symbol.SymKey,
                Resolver: TreeSitterResolver));
            foreach (var child in symbol.Children)
            {
                Walk(child, symbolId, qualified);
            }
        }

        foreach (var rootSymbol in parsed.Symbols)
        {
            Walk(rootSymbol, "", "");
        }

        foreach (var edge in parsed.Edges)
        {
            var fromId = edge.FromQualified == relativePath || edge.FromQualified == parsed.FilePath
                ? fileId
                : idByQualified.GetValueOrDefault(edge.FromQualified);
            if (string.IsNullOrEmpty(fromId))
            {
                continue;
            }

            string toId;
            if (edge.Kind == CodeEdgeKinds.Imports)
            {
                // Same id scheme as the regex import scan so both tiers merge on the same node.
                toId = $"import:{HashText(edge.ToName)}";
            }
            else if (idByQualified.TryGetValue(edge.ToName, out var resolved))
            {
                toId = resolved;
            }
            else
            {
                // EXTENDS/IMPLEMENTS targets are often unqualified in-source names; resolve within
                // this file when unambiguous, otherwise keep a stable unresolved reference node.
                var matches = idByQualified
                    .Where(pair => pair.Key.EndsWith("." + edge.ToName, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                toId = matches.Length == 1 ? matches[0].Value : $"typeref:{language}:{edge.ToName}";
            }

            var line = lineByQualified.GetValueOrDefault(edge.FromQualified, 0);
            edges.Add(new CodeEdgeFact(
                fromId,
                toId,
                edge.Kind,
                fileId,
                line,
                edge.Confidence,
                TreeSitterResolver,
                string.IsNullOrEmpty(edge.Evidence) ? relativePath : $"{relativePath}: {edge.Evidence}"));
        }
    }

    private static string QualifiedNameOf(ParsedSymbol symbol, string parentQualified)
    {
        // Prefer the extractor's sym_key ("lang:Qualified#arity") so edge FromQualified lookups
        // match exactly; fall back to parent-chain joining when the backend omitted sym_key.
        var symKey = symbol.SymKey;
        var colon = symKey.IndexOf(':');
        var hash = symKey.LastIndexOf('#');
        if (colon >= 0 && hash > colon)
        {
            return symKey[(colon + 1)..hash];
        }

        return parentQualified.Length == 0 ? symbol.Name : $"{parentQualified}.{symbol.Name}";
    }

    private static void ExtractSymbols(
        string fileId,
        string relativePath,
        string language,
        string text,
        List<CodeSymbolFact> symbols,
        List<CodeEdgeFact> edges)
    {
        var lines = SplitLines(text);
        string? previousSymbol = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            Match match;
            string? kind = null;
            string? name = null;
            if (language == "csharp" && (match = CsharpSymbol.Match(line)).Success)
            {
                kind = match.Groups[1].Success ? match.Groups[1].Value : "method";
                name = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            }
            else if ((language is "typescript" or "javascript") && (match = JsTsSymbol.Match(line)).Success)
            {
                kind = line.Contains(" class ", StringComparison.Ordinal) ? "class" : "function";
                name = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value;
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind))
            {
                continue;
            }

            var symbolId = $"symbol:{fileId}:{i + 1}:{name}";
            symbols.Add(new CodeSymbolFact(symbolId, fileId, name!, kind!, i + 1, i + 1, line.Trim(), Lang: language, Resolver: RegexResolver));
            edges.Add(new CodeEdgeFact(fileId, symbolId, CodeEdgeKinds.Contains, fileId, i + 1, RegexConfidence, RegexResolver, relativePath));
            if (previousSymbol is not null)
            {
                // "near" is a regex-tier proximity heuristic with no v2 CodeEdgeKinds equivalent;
                // it intentionally stays a lowercase open-string kind.
                edges.Add(new CodeEdgeFact(previousSymbol, symbolId, "near", fileId, i + 1, RegexConfidence, RegexResolver, "sequential symbol proximity"));
            }

            previousSymbol = symbolId;
        }
    }

    private static void ExtractImports(string fileId, string relativePath, string text, List<CodeEdgeFact> edges)
    {
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            var match = ImportLine.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var target = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value;
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var importId = $"import:{HashText(target!)}";
            edges.Add(new CodeEdgeFact(fileId, importId, CodeEdgeKinds.Imports, fileId, i + 1, RegexConfidence, RegexResolver, $"{relativePath}:{i + 1}: {target}"));
        }
    }

    private static void ExtractMarkdown(
        string fileId,
        string relativePath,
        string text,
        string fileHash,
        DateTime updatedAt,
        List<CodeDocBlockFact> docs)
    {
        var lines = SplitLines(text);
        var currentTitle = Path.GetFileName(relativePath);
        var block = new StringBuilder();
        var blockStart = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#", StringComparison.Ordinal))
            {
                FlushDoc();
                currentTitle = lines[i].TrimStart('#', ' ').Trim();
                blockStart = i + 1;
            }

            block.AppendLine(lines[i]);
        }

        FlushDoc();

        void FlushDoc()
        {
            var textBlock = block.ToString().Trim();
            if (textBlock.Length == 0)
            {
                return;
            }

            var anchor = Slug(currentTitle);
            docs.Add(new CodeDocBlockFact(
                $"doc:{fileId}:{blockStart}:{anchor}",
                fileId,
                anchor,
                textBlock,
                HashText($"{fileHash}:{blockStart}:{textBlock}"),
                updatedAt.ToUniversalTime().ToString("O")));
            block.Clear();
        }
    }

    private static void LinkDocsToSymbols(
        IReadOnlyList<CodeSymbolFact> symbols,
        IReadOnlyList<CodeDocBlockFact> docs,
        IReadOnlyList<CodeFileFact> files,
        RepositoryIndexRequest request,
        List<CodeEdgeFact> edges)
    {
        if (request.DocSymbolLinkMode == RepositoryDocSymbolLinkMode.Off ||
            request.MaxDocLinksPerDoc <= 0 ||
            request.MaxInferredRelations <= 0)
        {
            return;
        }

        var remainingBudget = request.MaxInferredRelations;
        var filePaths = files.ToDictionary(file => file.FileId, file => file.Path, StringComparer.Ordinal);
        var symbolsByFile = symbols
            .Where(symbol => IsLinkableSymbolName(symbol.Name))
            .GroupBy(symbol => symbol.FileId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var allSymbols = symbolsByFile.Values.SelectMany(group => group).ToArray();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var doc in docs)
        {
            if (remainingBudget <= 0)
            {
                return;
            }

            var docPath = filePaths.GetValueOrDefault(doc.FileId, "");
            var candidates = request.DocSymbolLinkMode == RepositoryDocSymbolLinkMode.Global
                ? allSymbols.Select(symbol => (Symbol: symbol, Score: 1))
                : LocalSymbolCandidates(docPath, filePaths, symbolsByFile);

            var linksForDoc = 0;
            foreach (var candidate in candidates
                         .Where(candidate => ContainsIdentifier(doc.Text, candidate.Symbol.Name))
                         .OrderByDescending(candidate => candidate.Score)
                         .ThenByDescending(candidate => candidate.Symbol.Name.Length)
                         .ThenBy(candidate => candidate.Symbol.SymbolId, StringComparer.Ordinal)
                         .Take(request.MaxDocLinksPerDoc))
            {
                var key = $"{candidate.Symbol.SymbolId}\n{doc.DocId}";
                if (!emitted.Add(key))
                {
                    continue;
                }

                edges.Add(new CodeEdgeFact(
                    candidate.Symbol.SymbolId,
                    doc.DocId,
                    CodeEdgeKinds.DocLinks,
                    doc.FileId,
                    0,
                    RegexConfidence,
                    RegexResolver,
                    request.DocSymbolLinkMode == RepositoryDocSymbolLinkMode.Global
                        ? $"markdown mentions {candidate.Symbol.Name}"
                        : $"local markdown mentions {candidate.Symbol.Name}"));
                linksForDoc++;
                remainingBudget--;
                if (remainingBudget <= 0 || linksForDoc >= request.MaxDocLinksPerDoc)
                {
                    break;
                }
            }
        }
    }

    private static IEnumerable<(CodeSymbolFact Symbol, int Score)> LocalSymbolCandidates(
        string docPath,
        IReadOnlyDictionary<string, string> filePaths,
        IReadOnlyDictionary<string, CodeSymbolFact[]> symbolsByFile)
    {
        var docDirectory = DirectoryName(docPath);
        var docTokens = PathTokens(docPath);

        foreach (var (fileId, symbols) in symbolsByFile)
        {
            var symbolPath = filePaths.GetValueOrDefault(fileId, "");
            var score = LocalityScore(docPath, docDirectory, docTokens, symbolPath);
            if (score <= 0)
            {
                continue;
            }

            foreach (var symbol in symbols)
            {
                yield return (symbol, score);
            }
        }
    }

    private static int LocalityScore(string docPath, string docDirectory, HashSet<string> docTokens, string symbolPath)
    {
        if (docPath.Length == 0 || symbolPath.Length == 0)
        {
            return 0;
        }

        var symbolDirectory = DirectoryName(symbolPath);
        if (string.Equals(docDirectory, symbolDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (IsReadmeLike(docPath) && IsSameOrDescendant(symbolDirectory, docDirectory))
        {
            return 80;
        }

        if (docDirectory.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            var symbolTokens = PathTokens(symbolPath);
            var overlap = docTokens.Intersect(symbolTokens, StringComparer.OrdinalIgnoreCase).Count();
            if (overlap > 0)
            {
                return 40 + Math.Min(overlap, 10);
            }
        }

        return 0;
    }

    private static bool IsLinkableSymbolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        return trimmed.Length >= 4 &&
               !AmbiguousSymbolNames.Contains(trimmed) &&
               IdentifierToken.IsMatch(trimmed);
    }

    private static bool ContainsIdentifier(string text, string identifier)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        var pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static HashSet<string> PathTokens(string path) =>
        IdentifierToken.Matches(path)
            .Select(match => match.Value)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string DirectoryName(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? "" : normalized[..index];
    }

    private static bool IsSameOrDescendant(string path, string ancestor) =>
        ancestor.Length == 0 ||
        string.Equals(path, ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadmeLike(string path)
    {
        var fileName = Path.GetFileName(path.Replace('\\', '/'));
        return fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("AGENTS", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string DetectLanguage(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".fs" => "fsharp",
            ".vb" => "vb",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
            ".md" or ".mdx" => "markdown",
            ".json" => "json",
            ".xml" or ".csproj" => "xml",
            ".sln" or ".slnx" => "solution",
            _ => "text",
        };

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}";

    private static string? SanitizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Replace(' ', '-');

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TryGetGitCommit(string root)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return "";
            }

            process.WaitForExit(2000);
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : "";
        }
        catch
        {
            return "";
        }
    }
}

public sealed record RepositoryBatchResult(
    string RepositoryId,
    string Commit,
    CodeKnowledgeBatch Batch,
    IReadOnlyList<string> SkippedFiles)
{
    /// <summary>Call/new sites seen by the resolution pipeline (call-resolution track, design.md §5; add-only).</summary>
    public int CallSites { get; init; }

    /// <summary>Call/new sites that produced a CALLS edge (any confidence tier).</summary>
    public int ResolvedCalls { get; init; }

    /// <summary>Call/new sites with no in-repo candidate — counted, never edged.</summary>
    public int UnresolvedCalls { get; init; }

    /// <summary>Roslyn semantic-hit edges merged at 1.0 (roslyn-csharp-resolution track, add-only).</summary>
    public int RoslynResolvedCalls { get; init; }

    /// <summary>Roslyn candidate-only edges merged at 0.6.</summary>
    public int RoslynCandidateCalls { get; init; }

    /// <summary>Roslyn call sites targeting BCL/external assemblies — counted, never edged.</summary>
    public int RoslynExternalCalls { get; init; }

    /// <summary>Roslyn edges whose caller/callee could not map to a baseline symbol — baseline kept.</summary>
    public int RoslynUnmappedCalls { get; init; }
}

internal sealed class GitIgnoreMatcher
{
    public static readonly GitIgnoreMatcher Empty = new([]);

    private readonly IReadOnlyList<Rule> _rules;

    private GitIgnoreMatcher(IReadOnlyList<Rule> rules)
    {
        _rules = rules;
    }

    public static GitIgnoreMatcher Load(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        if (!File.Exists(path))
        {
            return Empty;
        }

        var rules = new List<Rule>();
        foreach (var line in File.ReadLines(path))
        {
            if (Rule.TryParse(line, out var rule))
            {
                rules.Add(rule);
            }
        }

        return rules.Count == 0 ? Empty : new GitIgnoreMatcher(rules);
    }

    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        if (_rules.Count == 0)
        {
            return false;
        }

        var path = NormalizePath(relativePath);
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.Matches(path, isDirectory))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static string NormalizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar.ToString(), "/", StringComparison.Ordinal)
            .Replace(Path.AltDirectorySeparatorChar.ToString(), "/", StringComparison.Ordinal)
            .TrimStart('/');

    private sealed class Rule
    {
        private readonly Regex _regex;

        private Rule(
            bool negated,
            bool directoryOnly,
            bool rooted,
            bool hasSlash,
            string pattern,
            Regex regex)
        {
            Negated = negated;
            DirectoryOnly = directoryOnly;
            Rooted = rooted;
            HasSlash = hasSlash;
            Pattern = pattern;
            _regex = regex;
        }

        public bool Negated { get; }

        private bool DirectoryOnly { get; }

        private bool Rooted { get; }

        private bool HasSlash { get; }

        private string Pattern { get; }

        public static bool TryParse(string rawLine, out Rule rule)
        {
            rule = null!;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                return false;
            }

            var negated = line.StartsWith('!');
            if (negated)
            {
                line = line[1..].TrimStart();
            }

            if (line.Length == 0)
            {
                return false;
            }

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly)
            {
                line = line.TrimEnd('/');
            }

            var rooted = line.StartsWith('/');
            line = line.TrimStart('/');
            if (line.Length == 0)
            {
                return false;
            }

            var hasSlash = line.Contains('/');
            rule = new Rule(
                negated,
                directoryOnly,
                rooted,
                hasSlash,
                line,
                new Regex(ToRegexPattern(line, directoryOnly, rooted, hasSlash), RegexOptions.Compiled));
            return true;
        }

        public bool Matches(string relativePath, bool isDirectory)
        {
            if (DirectoryOnly && !isDirectory)
            {
                return false;
            }

            var path = relativePath.TrimStart('/');
            if (!HasSlash && !Rooted)
            {
                return DirectoryOnly
                    ? path.Split('/').Any(segment => _regex.IsMatch(segment))
                    : _regex.IsMatch(Path.GetFileName(path));
            }

            return _regex.IsMatch(path);
        }

        private static string ToRegexPattern(string pattern, bool directoryOnly, bool rooted, bool hasSlash)
        {
            if (!hasSlash && !rooted)
            {
                return $"^{GlobToRegex(pattern)}$";
            }

            var prefix = rooted ? "^" : "^(?:.*/)?";
            var suffix = directoryOnly ? "(?:/.*)?$" : "$";
            return prefix + GlobToRegex(pattern) + suffix;
        }

        private static string GlobToRegex(string pattern)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < pattern.Length; i++)
            {
                var ch = pattern[i];
                if (ch == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        builder.Append(".*");
                        i++;
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }
                }
                else if (ch == '?')
                {
                    builder.Append("[^/]");
                }
                else
                {
                    builder.Append(Regex.Escape(ch.ToString()));
                }
            }

            return builder.ToString();
        }
    }
}
