using System.Text.Json;
using Cozo.DotNet.Datalog.Cozo;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet.Om.Query;

namespace Cozo.DotNet.Om.CodeKnowledge;

public static class CozoOmCodeKnowledgeExtensions
{
    public static Task InitCodeKnowledgeAsync(this CozoOm om, CancellationToken cancellationToken = default) =>
        om.InitCodeKnowledgeAsync(reindex: false, cancellationToken);

    public static Task InitCodeKnowledgeAsync(this CozoOm om, bool reindex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        return CodeKnowledgeSchema.InitAsync(om, reindex, cancellationToken);
    }

    public static async Task<CodeKnowledgeIndexResult> IndexCodeKnowledgeAsync(
        this CozoOm om,
        CodeKnowledgeBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(batch);
        await using var tx = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);

        foreach (var item in batch.Repositories ?? [])
        {
            await tx.RunAsync(
                """
                ?[repo_id, root_path, name, commit] <- [[$repo_id, $root_path, $name, $commit]]
                :put ck_repo {repo_id => root_path, name, commit}
                """,
                Params(("repo_id", item.RepoId), ("root_path", item.RootPath), ("name", item.Name ?? ""), ("commit", item.Commit ?? "")),
                cancellationToken: cancellationToken);
        }

        foreach (var item in batch.Files ?? [])
        {
            await tx.RunAsync(
                """
                ?[file_id, repo_id, path, language, hash, updated_at] <- [[$file_id, $repo_id, $path, $language, $hash, $updated_at]]
                :put ck_file {file_id => repo_id, path, language, hash, updated_at}
                """,
                Params(("file_id", item.FileId), ("repo_id", item.RepoId), ("path", item.Path), ("language", item.Language), ("hash", item.Hash), ("updated_at", item.UpdatedAt)),
                cancellationToken: cancellationToken);
        }

        foreach (var item in batch.Symbols ?? [])
        {
            await tx.RunAsync(
                """
                ?[symbol_id, file_id, name, kind, start_line, end_line, signature, parent_id, lang, visibility, exported, sym_key, doc_id, resolver] <-
                  [[$symbol_id, $file_id, $name, $kind, $start_line, $end_line, $signature, $parent_id, $lang, $visibility, $exported, $sym_key, $doc_id, $resolver]]
                :put ck_symbol {symbol_id => file_id, name, kind, start_line, end_line, signature, parent_id, lang, visibility, exported, sym_key, doc_id, resolver}
                """,
                Params(
                    ("symbol_id", item.SymbolId), ("file_id", item.FileId), ("name", item.Name), ("kind", item.Kind),
                    ("start_line", item.StartLine), ("end_line", item.EndLine), ("signature", item.Signature),
                    ("parent_id", item.ParentId), ("lang", item.Lang), ("visibility", item.Visibility),
                    ("exported", item.Exported), ("sym_key", item.SymKey), ("doc_id", item.DocId), ("resolver", item.Resolver)),
                cancellationToken: cancellationToken);
        }

        foreach (var item in batch.Edges ?? [])
        {
            await PutEdgeAsync(tx, item, cancellationToken);
        }

        // Legacy compatibility: ck_relation was removed in schema v2. Relations entries are
        // converted to ck_edge rows at the regex confidence tier (confidence=0.3, resolver="regex"),
        // and their v1 lowercase kinds are normalized to the v2 CodeEdgeKinds vocabulary.
#pragma warning disable CS0618
        var legacyRelations = batch.Relations ?? [];
#pragma warning restore CS0618
        foreach (var item in legacyRelations)
        {
            await PutEdgeAsync(
                tx,
                new CodeEdgeFact(item.FromId, item.ToId, CodeEdgeKindMap.NormalizeV1(item.Kind), item.FileId ?? "", item.Line, Confidence: 0.3, Resolver: "regex", Evidence: item.Evidence),
                cancellationToken);
        }

        foreach (var item in batch.DocBlocks ?? [])
        {
            await tx.RunAsync(
                """
                ?[doc_id, file_id, anchor, text, hash, updated_at] <- [[$doc_id, $file_id, $anchor, $text, $hash, $updated_at]]
                :put ck_doc_block {doc_id => file_id, anchor, text, hash, updated_at}
                """,
                Params(("doc_id", item.DocId), ("file_id", item.FileId), ("anchor", item.Anchor), ("text", item.Text), ("hash", item.Hash), ("updated_at", item.UpdatedAt)),
                cancellationToken: cancellationToken);
        }

        foreach (var item in batch.Concepts ?? [])
        {
            await tx.RunAsync(
                """
                ?[concept_id, name, description] <- [[$concept_id, $name, $description]]
                :put ck_concept {concept_id => name, description}
                """,
                Params(("concept_id", item.ConceptId), ("name", item.Name), ("description", item.Description)),
                cancellationToken: cancellationToken);
        }

        foreach (var item in batch.Diagnostics ?? [])
        {
            await tx.RunAsync(
                """
                ?[diagnostic_id, target_id, kind, message, severity] <- [[$diagnostic_id, $target_id, $kind, $message, $severity]]
                :put ck_diagnostic {diagnostic_id => target_id, kind, message, severity}
                """,
                Params(("diagnostic_id", item.DiagnosticId), ("target_id", item.TargetId), ("kind", item.Kind), ("message", item.Message), ("severity", item.Severity)),
                cancellationToken: cancellationToken);
        }

        foreach (var item in batch.Owners ?? [])
        {
            await tx.RunAsync(
                """
                ?[target_id, owner, kind] <- [[$target_id, $owner, $kind]]
                :put ck_owner {target_id, owner => kind}
                """,
                Params(("target_id", item.TargetId), ("owner", item.Owner), ("kind", item.Kind)),
                cancellationToken: cancellationToken);
        }

        // Syntax-level entry-point candidates (process-extraction track, design.md §3): :put so
        // ExtractProcessesAsync's later :replace merge can preserve these non-main/public_api rows.
        foreach (var item in batch.EntryPoints ?? [])
        {
            await tx.RunAsync(
                """
                ?[symbol_id, kind, metadata] <- [[$symbol_id, $kind, $metadata]]
                :put ck_entry_point {symbol_id, kind => metadata}
                """,
                Params(("symbol_id", item.SymbolId), ("kind", item.Kind), ("metadata", item.Metadata)),
                cancellationToken: cancellationToken);
        }

        // add-llm-wiki-depa-ontology track (design §4.2): aggregated out-of-repo call summaries.
        // Facts arriving with an empty Category are pre-classified against the built-in effect
        // whitelist here (index-time fast path); depa_scan re-matches target_key with the merged
        // built-in + user whitelist at scan time and never rewrites these observation rows.
        foreach (var item in batch.ExternalCalls ?? [])
        {
            var category = item.Category.Length > 0
                ? item.Category
                : EffectApiBuiltins.Match(EffectApiBuiltins.Rules, item.TargetKey)?.Category ?? "";
            await tx.RunAsync(
                """
                ?[caller_id, target_key, count, category, first_file_id, first_line, resolver] <-
                  [[$caller_id, $target_key, $count, $category, $first_file_id, $first_line, $resolver]]
                :put ck_external_call {caller_id, target_key => count, category, first_file_id, first_line, resolver}
                """,
                Params(
                    ("caller_id", item.CallerId), ("target_key", item.TargetKey), ("count", item.Count),
                    ("category", category), ("first_file_id", item.FirstFileId), ("first_line", item.FirstLine),
                    ("resolver", item.Resolver)),
                cancellationToken: cancellationToken);
        }

        await tx.RunAsync(
            """
            ?[key, value] <- [["indexed_at", $indexed_at]]
            :put ck_meta {key => value}
            """,
            Params(("indexed_at", om.Runtime.Options.TimeProvider.GetUtcNow().ToString("O"))),
            cancellationToken: cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new CodeKnowledgeIndexResult(
            batch.Repositories?.Count ?? 0,
            batch.Files?.Count ?? 0,
            batch.Symbols?.Count ?? 0,
            legacyRelations.Count,
            batch.Edges?.Count ?? 0,
            batch.DocBlocks?.Count ?? 0,
            batch.Concepts?.Count ?? 0,
            batch.Diagnostics?.Count ?? 0,
            batch.Owners?.Count ?? 0,
            batch.EntryPoints?.Count ?? 0,
            batch.ExternalCalls?.Count ?? 0);
    }

    /// <summary>
    /// File-level fact removal (add-llm-wiki-incremental-indexing track, design.md §2). Deletes
    /// every observation-layer fact owned by the given files in one transaction: ck_symbol rows,
    /// ck_edge rows (owned via file_id or via a from_id symbol of the file), ck_doc_block rows,
    /// ck_entry_point / ck_external_call rows keyed by the files' symbols, and ck_diagnostic rows
    /// targeting the files or their symbols. ck_file rows are deleted only for
    /// <paramref name="removedFileIds"/> (changed files get their row re-put by the caller).
    /// Derived layers (community/process/search) are not touched — the index tail recomputes them.
    /// Internal by design: the public knob is RepositoryIndexRequest.IncrementalMode.
    /// </summary>
    internal static async Task RemoveFileFactsAsync(
        this CozoOm om,
        IReadOnlyCollection<string> fileIds,
        IReadOnlyCollection<string> removedFileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentNullException.ThrowIfNull(removedFileIds);
        if (fileIds.Count == 0 && removedFileIds.Count == 0)
        {
            return;
        }

        await using var tx = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        if (fileIds.Count > 0)
        {
            var fileParams = Params(("file_ids", fileIds.Select(id => new List<object?> { id }).ToList()));

            // Order matters: every sym[...] rule reads ck_symbol, so the tables that join through
            // it (edges/entry points/external calls/diagnostics) are cleared before the symbols.
            await tx.RunAsync(
                """
                fids[fid] <- $file_ids
                sym[s] := *ck_symbol{ symbol_id: s, file_id: f }, fids[f]
                ?[from_id, to_id, kind, file_id, line] := *ck_edge{ from_id, to_id, kind, file_id, line }, fids[file_id]
                ?[from_id, to_id, kind, file_id, line] := *ck_edge{ from_id, to_id, kind, file_id, line }, fids[from_id]
                ?[from_id, to_id, kind, file_id, line] := *ck_edge{ from_id, to_id, kind, file_id, line }, sym[from_id]
                :rm ck_edge {from_id, to_id, kind, file_id, line}
                """,
                fileParams,
                cancellationToken: cancellationToken);

            await tx.RunAsync(
                """
                fids[fid] <- $file_ids
                sym[s] := *ck_symbol{ symbol_id: s, file_id: f }, fids[f]
                ?[symbol_id, kind] := *ck_entry_point{ symbol_id, kind }, sym[symbol_id]
                :rm ck_entry_point {symbol_id, kind}
                """,
                fileParams,
                cancellationToken: cancellationToken);

            await tx.RunAsync(
                """
                fids[fid] <- $file_ids
                sym[s] := *ck_symbol{ symbol_id: s, file_id: f }, fids[f]
                ?[caller_id, target_key] := *ck_external_call{ caller_id, target_key }, sym[caller_id]
                :rm ck_external_call {caller_id, target_key}
                """,
                fileParams,
                cancellationToken: cancellationToken);

            await tx.RunAsync(
                """
                fids[fid] <- $file_ids
                sym[s] := *ck_symbol{ symbol_id: s, file_id: f }, fids[f]
                ?[diagnostic_id] := *ck_diagnostic{ diagnostic_id, target_id }, fids[target_id]
                ?[diagnostic_id] := *ck_diagnostic{ diagnostic_id, target_id }, sym[target_id]
                :rm ck_diagnostic {diagnostic_id}
                """,
                fileParams,
                cancellationToken: cancellationToken);

            await tx.RunAsync(
                """
                fids[fid] <- $file_ids
                ?[doc_id] := *ck_doc_block{ doc_id, file_id }, fids[file_id]
                :rm ck_doc_block {doc_id}
                """,
                fileParams,
                cancellationToken: cancellationToken);

            await tx.RunAsync(
                """
                fids[fid] <- $file_ids
                ?[symbol_id] := *ck_symbol{ symbol_id, file_id }, fids[file_id]
                :rm ck_symbol {symbol_id}
                """,
                fileParams,
                cancellationToken: cancellationToken);
        }

        if (removedFileIds.Count > 0)
        {
            await tx.RunAsync(
                """
                rfids[fid] <- $removed_file_ids
                ?[file_id] := *ck_file{ file_id }, rfids[file_id]
                :rm ck_file {file_id}
                """,
                Params(("removed_file_ids", removedFileIds.Select(id => new List<object?> { id }).ToList())),
                cancellationToken: cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static Task PutEdgeAsync(ICozoOmTransaction tx, CodeEdgeFact edge, CancellationToken cancellationToken) =>
        tx.RunAsync(
            """
            ?[from_id, to_id, kind, file_id, line, confidence, resolver, evidence] <-
              [[$from_id, $to_id, $kind, $file_id, $line, $confidence, $resolver, $evidence]]
            :put ck_edge {from_id, to_id, kind, file_id, line => confidence, resolver, evidence}
            """,
            Params(
                ("from_id", edge.FromId), ("to_id", edge.ToId), ("kind", edge.Kind),
                ("file_id", edge.FileId), ("line", edge.Line),
                ("confidence", edge.Confidence), ("resolver", edge.Resolver), ("evidence", edge.Evidence)),
            cancellationToken: cancellationToken);

    public static OmQueryRegistry CreateCodeKnowledgeRegistry() =>
        new OmQueryRegistry()
            .Register(new NamedQueryDefinition(
                "code.symbolContext",
                "v1",
                "symbol(SymbolId, Name, Kind, FileId, Path, StartLine, EndLine, Signature) :- ck_symbol(SymbolId, FileId, Name, Kind, StartLine, EndLine, Signature), ck_file(FileId, RepoId, Path, Language, Hash, UpdatedAt).\n?- symbol($symbolId, Name, Kind, FileId, Path, StartLine, EndLine, Signature).",
                Mapping(),
                OmQueryResultShape.Table,
                [new NamedQueryParameter("symbolId")],
                new OmQuerySafetyPolicy(Limit: 50),
                "Find symbol context."))
            .Register(new NamedQueryDefinition(
                "code.impactOfChange",
                "v1",
                "impact(X, Y, Kind) :- ck_edge(X, Y, Kind, FileId, Line, Evidence).\nimpact(X, Z, Kind2) :- impact(X, Y, Kind1), ck_edge(Y, Z, Kind2, FileId2, Line2, Evidence2).\n?- impact(Source, Target, Kind), Source = $symbolId.",
                Mapping(),
                OmQueryResultShape.Graph,
                [new NamedQueryParameter("symbolId")],
                new OmQuerySafetyPolicy(Limit: 200),
                "Transitive impact of a changed symbol."))
            .Register(new NamedQueryDefinition(
                "code.docsForCode",
                "v1",
                "docs(Target, DocId, FileId, Anchor, Text) :- ck_edge(Target, DocId, \"DOC_LINKS\", FileId2, Line, Evidence), ck_doc_block(DocId, FileId, Anchor, Text, Hash, UpdatedAt).\n?- docs($targetId, DocId, FileId, Anchor, Text).",
                Mapping(),
                OmQueryResultShape.Table,
                [new NamedQueryParameter("targetId")],
                new OmQuerySafetyPolicy(Limit: 100),
                "Find docs linked to a code target."))
            .Register(new NamedQueryDefinition(
                "code.traceConcept",
                "v1",
                "mentions(ConceptId, Target, Evidence) :- ck_edge(Target, ConceptId, \"MENTIONS\", FileId, Line, Evidence).\n?- mentions($conceptId, Target, Evidence).",
                Mapping(),
                OmQueryResultShape.Table,
                [new NamedQueryParameter("conceptId")],
                new OmQuerySafetyPolicy(Limit: 200),
                "Trace concept mentions."))
            .Register(new NamedQueryDefinition(
                "code.explainRelation",
                "v1",
                "rel(FromId, ToId, Kind, Confidence, Evidence) :- ck_edge_scored(FromId, ToId, Kind, FileId, Line, Confidence, Evidence).\n?- rel($fromId, $toId, Kind, Confidence, Evidence).",
                Mapping(),
                OmQueryResultShape.Table,
                [new NamedQueryParameter("fromId"), new NamedQueryParameter("toId")],
                new OmQuerySafetyPolicy(Limit: 50),
                "Explain direct relation evidence."));

    public static async Task<SymbolContextResult> FindSymbolContextAsync(this CozoOm om, string symbolId, CancellationToken cancellationToken = default)
    {
        var engine = new OmQueryEngine(om, CreateCodeKnowledgeRegistry());
        var query = await engine.ExecuteNamedAsync(new NamedQueryInput("code.symbolContext", Params(("symbolId", symbolId))), cancellationToken);
        var symbol = query.Table.Rows.Select(SymbolFromRow).FirstOrDefault();
        var relations = await DirectRelationsAsync(om, symbolId, cancellationToken);
        var docs = await om.DocsForCodeAsync(symbolId, cancellationToken);

        // deepen-llm-wiki-context-impact track (design §3, add-only enrichment): execution flows,
        // community membership and per-kind edge counts. Process/community lookups are best-effort —
        // missing or empty tables (or a failing query) leave the fields empty, never throw.
        IReadOnlyList<CodeProcessSummary> processes = [];
        try
        {
            processes = (await om.FindProcessesForSymbolAsync(symbolId, cancellationToken)).Take(10).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        var communityId = "";
        var communityLabel = "";
        try
        {
            var community = await om.FindSymbolCommunityAsync(symbolId, cancellationToken);
            if (community is not null)
            {
                communityId = community.CommunityId;
                communityLabel = community.Label;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return new SymbolContextResult(symbol, relations.Incoming, relations.Outgoing, docs.Docs)
        {
            Processes = processes,
            CommunityId = communityId,
            CommunityLabel = communityLabel,
            IncomingByKind = CountByKind(relations.Incoming),
            OutgoingByKind = CountByKind(relations.Outgoing),
        };
    }

    private static IReadOnlyDictionary<string, int> CountByKind(IReadOnlyList<CodeRelationSummary> relations)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            counts[relation.Kind] = counts.GetValueOrDefault(relation.Kind) + 1;
        }

        return counts;
    }

    public static Task<ImpactOfChangeResult> ImpactOfChangeAsync(this CozoOm om, string symbolId, CancellationToken cancellationToken = default) =>
        om.ImpactOfChangeAsync(symbolId, minConfidence: 0.0, cancellationToken);

    /// <summary>
    /// Transitive impact of a changed symbol over ck_edge. Only edges with
    /// confidence &gt;= <paramref name="minConfidence"/> are traversed, so low-confidence
    /// (e.g. regex tier 0.3) continuations can be excluded from the blast radius.
    /// </summary>
    public static async Task<ImpactOfChangeResult> ImpactOfChangeAsync(
        this CozoOm om,
        string symbolId,
        double minConfidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        var rows = await om.Runtime.Store.RunAsync(
            """
            reach[to] := *ck_edge{ from_id: $symbol_id, to_id: to, confidence }, confidence >= $min_confidence
            reach[to] := reach[mid], *ck_edge{ from_id: mid, to_id: to, confidence }, confidence >= $min_confidence
            ?[from_id, to_id, kind, confidence, evidence] :=
              *ck_edge{ from_id, to_id, kind, confidence, evidence },
              from_id = $symbol_id, confidence >= $min_confidence
            ?[from_id, to_id, kind, confidence, evidence] :=
              reach[from_id],
              *ck_edge{ from_id, to_id, kind, confidence, evidence },
              confidence >= $min_confidence
            :sort from_id, to_id, kind
            :limit 200
            """,
            Params(("symbol_id", symbolId), ("min_confidence", minConfidence)),
            cancellationToken: cancellationToken);
        var edges = rows.Rows.Select(row => new CodeRelationSummary(
            JsonString(row[0]) ?? "",
            JsonString(row[1]) ?? "",
            JsonString(row[2]) ?? "",
            JsonString(row[4]) ?? "",
            DoubleOf(row[3]))).Where(e => e.ToId.Length > 0).ToArray();
        return new ImpactOfChangeResult(symbolId, edges, edges.Select(e => e.ToId).Distinct(StringComparer.Ordinal).ToArray());
    }

    public static async Task<DocsForCodeResult> DocsForCodeAsync(this CozoOm om, string targetId, CancellationToken cancellationToken = default)
    {
        var engine = new OmQueryEngine(om, CreateCodeKnowledgeRegistry());
        var query = await engine.ExecuteNamedAsync(new NamedQueryInput("code.docsForCode", Params(("targetId", targetId))), cancellationToken);
        var docs = query.Table.Rows.Select(row => new CodeDocSummary(
            StringAt(row, "DocId") ?? "",
            StringAt(row, "FileId") ?? "",
            StringAt(row, "Anchor") ?? "",
            StringAt(row, "Text") ?? "")).Where(d => d.DocId.Length > 0).ToArray();
        return new DocsForCodeResult(targetId, docs, docs.Length == 0);
    }

    public static async Task<TraceConceptResult> TraceConceptAsync(this CozoOm om, string conceptId, CancellationToken cancellationToken = default)
    {
        var engine = new OmQueryEngine(om, CreateCodeKnowledgeRegistry());
        var query = await engine.ExecuteNamedAsync(new NamedQueryInput("code.traceConcept", Params(("conceptId", conceptId))), cancellationToken);
        var mentions = query.Table.Rows.Select(row => new CodeRelationSummary(
            StringAt(row, "Target") ?? "",
            conceptId,
            CodeEdgeKinds.Mentions,
            StringAt(row, "Evidence") ?? "")).ToArray();
        return new TraceConceptResult(conceptId, mentions, []);
    }

    public static async Task<ExplainRelationResult> ExplainRelationAsync(this CozoOm om, string fromId, string toId, CancellationToken cancellationToken = default)
    {
        var engine = new OmQueryEngine(om, CreateCodeKnowledgeRegistry());
        var query = await engine.ExecuteNamedAsync(new NamedQueryInput("code.explainRelation", Params(("fromId", fromId), ("toId", toId))), cancellationToken);
        var relations = query.Table.Rows.Select(row => new CodeRelationSummary(
            fromId,
            toId,
            StringAt(row, "Kind") ?? "",
            StringAt(row, "Evidence") ?? "",
            DoubleAt(row, "Confidence"))).ToArray();
        return new ExplainRelationResult(fromId, toId, relations);
    }

    public static async Task<WikiPlan> BuildWikiPlanAsync(this CozoOm om, CancellationToken cancellationToken = default)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[file_id, path, symbol_id, name] :=
              *ck_file{ file_id, repo_id: _repo_id, path, language: _language, hash: _hash, updated_at: _updated_at },
              *ck_symbol{ symbol_id, file_id, name, kind: _kind, start_line: _start, end_line: _end, signature: _sig }
            :sort path, name
            """,
            cancellationToken: cancellationToken);
        var pages = rows.Rows
            .GroupBy(row => JsonString(row[0]) ?? "")
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                var first = g.First();
                var path = JsonString(first[1]) ?? g.Key;
                var symbols = g.Select(row => JsonString(row[2]) ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                return new WikiPlanPage($"page:{g.Key}", path, [g.Key], symbols, []);
            })
            .ToArray();

        var missingRows = await om.Runtime.Store.RunAsync(
            """
            documented[id] := *ck_edge{ from_id: id, to_id: _doc, kind: "DOC_LINKS", file_id: _file, line: _line, evidence: _evidence }
            ?[symbol_id] :=
              *ck_symbol{ symbol_id, file_id: _file_id, name: _name, kind: _kind, start_line: _start, end_line: _end, signature: _sig },
              not documented[symbol_id]
            :sort symbol_id
            """,
            cancellationToken: cancellationToken);
        var missing = missingRows.Rows.Select(row => JsonString(row[0]) ?? "").Where(x => x.Length > 0).ToArray();
        return new WikiPlan(pages, missing, []);
    }

    private static IReadOnlyDictionary<string, CozoStoredRelationMapping> Mapping() =>
        new Dictionary<string, CozoStoredRelationMapping>(StringComparer.Ordinal)
        {
            ["ck_repo"] = new("ck_repo", "ck_repo", ["repo_id", "root_path", "name", "commit"]),
            ["ck_file"] = new("ck_file", "ck_file", ["file_id", "repo_id", "path", "language", "hash", "updated_at"]),
            ["ck_symbol"] = new("ck_symbol", "ck_symbol", ["symbol_id", "file_id", "name", "kind", "start_line", "end_line", "signature"]),
            ["ck_edge"] = new("ck_edge", "ck_edge", ["from_id", "to_id", "kind", "file_id", "line", "evidence"]),
            // Confidence-aware alias over the same stored relation for queries that need the score.
            ["ck_edge_scored"] = new("ck_edge_scored", "ck_edge", ["from_id", "to_id", "kind", "file_id", "line", "confidence", "evidence"]),
            ["ck_doc_block"] = new("ck_doc_block", "ck_doc_block", ["doc_id", "file_id", "anchor", "text", "hash", "updated_at"]),
            ["ck_concept"] = new("ck_concept", "ck_concept", ["concept_id", "name", "description"]),
        };

    private static async Task<(IReadOnlyList<CodeRelationSummary> Incoming, IReadOnlyList<CodeRelationSummary> Outgoing)> DirectRelationsAsync(
        CozoOm om,
        string symbolId,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[from_id, to_id, kind, evidence, confidence] :=
              *ck_edge{ from_id, to_id, kind, file_id: _file_id, line: _line, confidence, evidence },
              from_id = $symbol_id
            ?[from_id, to_id, kind, evidence, confidence] :=
              *ck_edge{ from_id, to_id, kind, file_id: _file_id, line: _line, confidence, evidence },
              to_id = $symbol_id
            :sort kind, from_id, to_id
            """,
            Params(("symbol_id", symbolId)),
            cancellationToken: cancellationToken);
        var all = rows.Rows.Select(row => new CodeRelationSummary(
            JsonString(row[0]) ?? "",
            JsonString(row[1]) ?? "",
            JsonString(row[2]) ?? "",
            JsonString(row[3]) ?? "",
            DoubleOf(row[4]))).ToArray();
        return (
            all.Where(r => r.ToId == symbolId).ToArray(),
            all.Where(r => r.FromId == symbolId).ToArray());
    }

    private static CodeSymbolSummary SymbolFromRow(IReadOnlyDictionary<string, JsonElement> row) =>
        new(
            StringAt(row, "SymbolId") ?? "",
            StringAt(row, "Name") ?? "",
            StringAt(row, "Kind") ?? "",
            StringAt(row, "FileId") ?? "",
            StringAt(row, "Path") ?? "",
            IntAt(row, "StartLine"),
            IntAt(row, "EndLine"),
            StringAt(row, "Signature") ?? "");

    private static Dictionary<string, object?> Params(params (string Key, object? Value)[] entries)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries) dict[key] = value;
        return dict;
    }

    private static string? StringAt(IReadOnlyDictionary<string, JsonElement> row, string key) =>
        row.TryGetValue(key, out var value) ? JsonString(value) : null;

    private static int IntAt(IReadOnlyDictionary<string, JsonElement> row, string key) =>
        row.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static double DoubleAt(IReadOnlyDictionary<string, JsonElement> row, string key) =>
        row.TryGetValue(key, out var value) ? DoubleOf(value) : 0.0;

    private static double DoubleOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0.0;

    private static string? JsonString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
}
