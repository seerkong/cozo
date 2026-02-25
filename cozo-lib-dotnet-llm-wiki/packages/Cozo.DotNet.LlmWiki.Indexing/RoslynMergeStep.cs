using Cozo.DotNet.LlmWiki.RoslynEnhancer;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Indexing;

/// <summary>
/// Roslyn semantic merge (roslyn-csharp-resolution track, design.md §2). Runs after CallResolver:
///
/// - Symbol matching: RoslynSymbolInfo.SymKey == baseline CodeSymbolFact.SymKey within the same
///   file with overlapping line ranges → the symbol's DocId is filled in-place (record `with`);
///   the symbol resolver stays "treesitter" (decision 1: doc_id enrichment does not change the
///   resolver — only edges produced here carry resolver=roslyn).
/// - Edge merge, key = (callerSymbolId, file, line): a Roslyn edge whose caller AND callee DocIds
///   map to baseline symbols removes every baseline treesitter CALLS edge with that key and
///   inserts the Roslyn edge (confidence 1.0/0.6, resolver=roslyn, evidence preserved). Call
///   points where the baseline had no edge (e.g. implicit `new()`, unresolved sites) are inserted
///   through the same path. Mapping failure (synthesized ctors, non-treesitter files, top-level
///   entry points) keeps the baseline edge and counts as unmapped.
/// - External (BCL / out-of-repo) call sites never reach this step as edges — the count is
///   passed through from the analysis result.
/// - Failure safety: any Analyze exception records a roslyn_failed warning diagnostic and
///   returns zero counters — the tree-sitter baseline index proceeds untouched.
///
/// Internal by design (minimal public surface): consumed only by RepositoryIndexer; results
/// surface through CodeSymbolFact.DocId, CodeEdgeFact rows, and the summary counters.
/// </summary>
internal static class RoslynMergeStep
{
    private const string RoslynResolver = "roslyn";
    private const string TreeSitterResolver = "treesitter";

    /// <summary>Aggregate counters for the index summary (design.md §2, add-only).</summary>
    internal sealed record RoslynMergeStats(int ResolvedCalls, int CandidateCalls, int ExternalCalls, int UnmappedCalls)
    {
        public static readonly RoslynMergeStats Empty = new(0, 0, 0, 0);
    }

    /// <summary>
    /// Analyzes <paramref name="csFiles"/> with the Roslyn enhancer and merges the result into the
    /// baseline facts in place. <paramref name="analyze"/> is an injection seam for the failure
    /// path test; production always uses <see cref="RoslynEnhancer.RoslynEnhancer.Analyze"/>.
    /// </summary>
    internal static RoslynMergeStats Enhance(
        IReadOnlyList<(string Path, string Source)> csFiles,
        IReadOnlyDictionary<string, string> fileIdByPath,
        List<CodeSymbolFact> symbols,
        List<CodeEdgeFact> edges,
        List<CodeDiagnosticFact> diagnostics,
        string diagnosticTargetId,
        Func<IReadOnlyList<(string Path, string Source)>, RoslynAnalysisResult>? analyze = null,
        List<CodeExternalCallFact>? externalCalls = null)
    {
        RoslynAnalysisResult analysis;
        try
        {
            analysis = (analyze ?? RoslynEnhancer.RoslynEnhancer.Analyze)(csFiles);
        }
        catch (Exception ex)
        {
            // Compilation failure must never fail the index — the tree-sitter baseline stands.
            diagnostics.Add(new CodeDiagnosticFact(
                $"diag:roslyn-failed:{diagnosticTargetId}",
                diagnosticTargetId,
                "roslyn_failed",
                $"Roslyn enhancement failed; tree-sitter baseline kept: {ex.Message}",
                "warning"));
            return RoslynMergeStats.Empty;
        }

        // ---- symbol matching + doc_id enrichment (design.md §2) --------------------------------
        var baselineByKey = new Dictionary<(string FileId, string SymKey), List<int>>();
        for (var i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            if (symbol.Resolver != TreeSitterResolver || symbol.SymKey.Length == 0)
            {
                continue;
            }

            var key = (symbol.FileId, symbol.SymKey);
            if (!baselineByKey.TryGetValue(key, out var list))
            {
                baselineByKey[key] = list = [];
            }

            list.Add(i);
        }

        var symbolIdByDocId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var roslynSymbol in analysis.Symbols)
        {
            if (!fileIdByPath.TryGetValue(roslynSymbol.File, out var fileId) ||
                !baselineByKey.TryGetValue((fileId, roslynSymbol.SymKey), out var candidates))
            {
                continue;
            }

            foreach (var index in candidates)
            {
                var baseline = symbols[index];
                if (baseline.StartLine > roslynSymbol.EndLine || roslynSymbol.StartLine > baseline.EndLine)
                {
                    continue; // line ranges must overlap (sym_key collisions, e.g. partial types).
                }

                if (baseline.DocId.Length == 0)
                {
                    symbols[index] = baseline with { DocId = roslynSymbol.DocId };
                }

                symbolIdByDocId.TryAdd(roslynSymbol.DocId, baseline.SymbolId);
                break;
            }
        }

        // ---- edge merge, key = (callerSymbolId, file, line) ------------------------------------
        var resolvedCalls = 0;
        var candidateCalls = 0;
        var unmappedCalls = 0;
        var overriddenKeys = new HashSet<(string CallerId, string FileId, int Line)>();
        var inserts = new List<CodeEdgeFact>();
        var emitted = new HashSet<(string From, string To, string FileId, int Line)>();

        foreach (var edge in analysis.Edges)
        {
            if (!fileIdByPath.TryGetValue(edge.File, out var fileId) ||
                !symbolIdByDocId.TryGetValue(edge.CallerDocId, out var callerId) ||
                !symbolIdByDocId.TryGetValue(edge.CalleeDocId, out var calleeId))
            {
                // Caller/callee not mappable to a baseline symbol (synthesized ctor, regex-tier
                // file, top-level entry point) → keep whatever the baseline produced, count it.
                unmappedCalls++;
                continue;
            }

            overriddenKeys.Add((callerId, fileId, edge.Line));
            if (emitted.Add((callerId, calleeId, fileId, edge.Line)))
            {
                inserts.Add(new CodeEdgeFact(
                    callerId,
                    calleeId,
                    CodeEdgeKinds.Calls,
                    fileId,
                    edge.Line,
                    edge.Confidence,
                    RoslynResolver,
                    $"{edge.File}: {edge.Evidence}"));
                if (edge.Confidence >= 0.99)
                {
                    resolvedCalls++;
                }
                else
                {
                    candidateCalls++;
                }
            }
        }

        // Same call point only keeps the highest-confidence source (Roslyn wins): remove the
        // baseline treesitter CALLS edges whose key an inserted Roslyn edge covers.
        edges.RemoveAll(edge => edge.Kind == CodeEdgeKinds.Calls
            && edge.Resolver == TreeSitterResolver
            && overriddenKeys.Contains((edge.FromId, edge.FileId, edge.Line)));
        edges.AddRange(inserts);

        // ---- ck_external_call aggregation (depa-ontology track, design §4.2) --------------------
        // Sites are grouped per (caller symbol, external target FQN); only the first call site
        // (analysis order = source order) is kept as evidence, the rest survive in Count. Callers
        // that never mapped to a baseline symbol are skipped (no anchor to attach the row to).
        // Category stays "" here — the built-in whitelist pre-classification happens at the
        // observation-layer write path (IndexCodeKnowledgeAsync), keeping the indexer whitelist-free.
        if (externalCalls is not null)
        {
            var byKey = new Dictionary<(string CallerId, string TargetKey), (int Count, string FileId, int Line)>();
            foreach (var site in analysis.ExternalCallSites)
            {
                if (!symbolIdByDocId.TryGetValue(site.CallerDocId, out var callerId) ||
                    !fileIdByPath.TryGetValue(site.File, out var siteFileId))
                {
                    continue;
                }

                var key = (callerId, site.TargetKey);
                byKey[key] = byKey.TryGetValue(key, out var acc)
                    ? (acc.Count + 1, acc.FileId, acc.Line)
                    : (1, siteFileId, site.Line);
            }

            foreach (var pair in byKey.OrderBy(p => p.Key.CallerId, StringComparer.Ordinal).ThenBy(p => p.Key.TargetKey, StringComparer.Ordinal))
            {
                externalCalls.Add(new CodeExternalCallFact(
                    pair.Key.CallerId,
                    pair.Key.TargetKey,
                    pair.Value.Count,
                    Category: "",
                    FirstFileId: pair.Value.FileId,
                    FirstLine: pair.Value.Line,
                    Resolver: RoslynResolver));
            }
        }

        return new RoslynMergeStats(resolvedCalls, candidateCalls, analysis.ExternalCalls, unmappedCalls);
    }
}
