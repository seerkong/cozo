using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.LlmWiki.SemanticParsing;
using Cozo.DotNet.LlmWiki.VectorSearch;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;
using Cozo.DotNet.Om.Depa;
using Cozo.DotNet.Om.Query;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed class LlmWikiToolRunner(CozoOm om)
{
    private readonly RepositoryIndexer _indexer = new();
    private readonly TreeSitterCliParser _parser = new();
    private readonly ParserBackendSelector _parserBackendSelector = ParserBackendSelector.CreateDefault();
    private readonly CozoVectorSearchService _vectorSearch = new();
    private readonly WikiCompiler _wiki = new();
    private readonly LlmWikiOverviewGraphBuilder _overviewGraph = new();
    private readonly BusinessOntologyInvestigationService _investigation = new(om, new BusinessOntologyStore(om));

    public static JsonArray ToolsJson() =>
    [
        Tool("index_repo", "Index a local repository into Cozo OM CodeKnowledge.", new JsonObject
        {
            ["repoPath"] = StringSchema("Repository path"),
            ["docSymbolLinkMode"] = StringSchema("Documentation link inference mode: off, local, or global. Default: local."),
            ["maxDocLinksPerDoc"] = StringSchema("Maximum inferred documentation links per doc block. Default: 20."),
            ["maxInferredRelations"] = StringSchema("Maximum inferred documentation relations per index run. Default: 200000.")
        }, ["repoPath"]),
        Tool("build_wiki", "Build Markdown wiki from indexed CodeKnowledge.", new JsonObject
        {
            ["outputDirectory"] = StringSchema("Optional output directory"),
            ["writeFiles"] = BoolSchema("Write wiki files (legacy pipeline only)"),
            ["pipeline"] = StringSchema("Wiki pipeline: legacy (template compiler) or codument-fractal (four-phase community-led pipeline with page-level incremental builds writing to .depa-wiki/docs-preview). Default: legacy."),
            ["useLlm"] = BoolSchema("codument-fractal only: use the configured LLM backend for grouping review and narrative sections. Default: true, degrading to the pure structure layer when no backend is configured/available."),
            ["force"] = BoolSchema("codument-fractal only: ignore the page-level incremental cache and rebuild every page. Default: false."),
            ["workDirectory"] = StringSchema("codument-fractal only: work directory the default preview root and the migration-ledger commit are derived from. Default: current directory.")
        }),
        Tool("symbol_context", "Find symbol context by symbol id: declaration, direct relations and linked docs, enriched with the execution flows (processes) the symbol participates in, its community membership and per-kind edge counts.", new JsonObject { ["symbolId"] = StringSchema("Symbol id") }, ["symbolId"]),
        Tool("impact_of_change", "Run transitive impact analysis from a symbol id: layered BFS over CALLS edges (each layer carries symbols/total/truncated), a LOW/MEDIUM/HIGH/CRITICAL risk rating and the affected execution flows, alongside the legacy flat edges/impactedIds fields.", new JsonObject
        {
            ["symbolId"] = StringSchema("Symbol id"),
            ["direction"] = StringSchema("Layered traversal direction: up = who transitively calls / depends on me (my blast radius); down = whom I transitively call / depend on (my dependency surface). Default: up."),
            ["maxDepth"] = StringSchema("Maximum number of BFS layers, clamped to 1..16. Default: 3."),
            ["minConfidence"] = StringSchema("Exclude edges below this confidence from the analysis. Default: 0.0.")
        }, ["symbolId"]),
        Tool("docs_for_code", "Find documentation blocks linked to a code target.", new JsonObject { ["targetId"] = StringSchema("Target id") }, ["targetId"]),
        Tool("explain_relation", "Explain direct relation evidence.", new JsonObject { ["fromId"] = StringSchema("From id"), ["toId"] = StringSchema("To id") }, ["fromId", "toId"]),
        Tool("parser_status", "Report Tree-sitter parser availability.", new JsonObject()),
        Tool("parse_file", "Parse a local source file via Tree-sitter CLI.", new JsonObject { ["filePath"] = StringSchema("File path"), ["language"] = StringSchema("Optional language id"), ["maxNodes"] = StringSchema("Maximum node summaries") }, ["filePath"]),
        Tool("index_embeddings", "Index CodeKnowledge text into Cozo vector relation.", new JsonObject { ["includeSymbols"] = BoolSchema("Index symbols"), ["includeDocs"] = BoolSchema("Index docs"), ["limit"] = StringSchema("Maximum source count") }),
        Tool("semantic_search", "Hybrid search over indexed CodeKnowledge: BM25 full-text and vector similarity fused with reciprocal rank fusion. Query text is matched literally (FTS operators are not interpreted). Degrades safely when one channel is unavailable.", new JsonObject
        {
            ["query"] = StringSchema("Search query"),
            ["limit"] = StringSchema("Maximum hits"),
            ["sourceKinds"] = StringArraySchema("Source categories or kinds to search: code, docs, symbol, doc."),
            ["mode"] = StringSchema("Search mode: hybrid (BM25 + vector RRF fusion), vector, or text. Default: hybrid."),
            ["rrfK"] = StringSchema("Reciprocal rank fusion K constant. Default: 60.")
        }, ["query"]),
        Tool("overview_graph", "Load a bounded repository CodeKnowledge overview graph.", new JsonObject { ["categories"] = StringArraySchema("Graph categories: code and/or docs."), ["maxNodes"] = StringSchema("Maximum graph nodes. Default: 300."), ["maxEdges"] = StringSchema("Maximum graph edges. Default: 600.") }),
        Tool("ontology_investigation_overview", "Read a bounded business-ontology investigation overview. This is read-only and never exposes raw database queries.", new JsonObject { ["ontologyId"] = StringSchema("Optional dotted ontology id whose active generation is included.") }),
        Tool("find_business_terms", "Find a business term across indexed semantic claims, code symbols, and an optional existing ontology. Results are stable, paginated, evidence-linked summaries.", new JsonObject { ["term"] = StringSchema("Business term to investigate."), ["ontologyId"] = StringSchema("Optional dotted ontology id."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 50.") }, ["term"]),
        Tool("list_use_case_slices", "List bounded business use-case slices rooted at indexed entry points for one existing ontology generation.", new JsonObject { ["ontologyId"] = StringSchema("Dotted ontology id."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 50.") }, ["ontologyId"]),
        Tool("get_use_case_slice", "Read one bounded business use-case slice by its stable slice id.", new JsonObject { ["ontologyId"] = StringSchema("Dotted ontology id."), ["sliceId"] = StringSchema("Stable use-case slice id.") }, ["ontologyId", "sliceId"]),
        Tool("find_semantic_patterns", "Find bounded indexed semantic patterns for one supported claim kind. It accepts no arbitrary database query text.", new JsonObject { ["kind"] = StringSchema("One of typed_reference, validation_constraint, persistence_constraint, state_field, state_value, state_assignment, transaction_scope, route_binding, business_guard."), ["term"] = StringSchema("Optional business-term filter."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 50.") }, ["kind"]),
        Tool("list_semantic_evidence", "Enumerate indexed semantic evidence in deterministic claim-id pages. This is read-only, bounded, and accepts neither SQL nor paths.", new JsonObject { ["term"] = StringSchema("Optional business-term filter."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 500.") }),
        Tool("get_semantic_evidence", "Read bounded source excerpts only by existing indexed semantic evidence ids; arbitrary paths are not accepted.", new JsonObject { ["evidenceIds"] = StringArraySchema("One to 24 indexed semantic claim ids.") }, ["evidenceIds"]),
        Tool("inspect_ontology_subject", "Inspect one existing ontology concept, relation, rule, lifecycle, or candidate with status, mappings, evidence ids, and reviews.", new JsonObject { ["ontologyId"] = StringSchema("Dotted ontology id."), ["subjectKind"] = StringSchema("concept, relation, rule, lifecycle, or candidate."), ["subjectId"] = StringSchema("Ontology subject id.") }, ["ontologyId", "subjectKind", "subjectId"]),
        Tool("discover_domain_charters", "Discover bounded domain-charter seeds from indexed entry points, claims, roles, and call paths. This reads only ck_* facts and never requires or writes an ontology generation.", new JsonObject { ["term"] = StringSchema("Literal domain term."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 50.") }, ["term"]),
        Tool("list_cross_layer_use_cases", "List bounded route-to-call-path-to-role use-case slices from one entry symbol or literal domain seed. This reads only ck_* facts and accepts neither ontology ids nor source paths.", new JsonObject { ["entrySymbolId"] = StringSchema("One indexed symbol: id."), ["domainSeed"] = StringSchema("Literal domain seed."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 50.") }),
        Tool("find_state_rule_clusters", "Group bounded indexed state fields, values, transitions, guards, validation, and persistence claims by subject and call path. This is read-only and evidence-linked.", new JsonObject { ["term"] = StringSchema("Literal domain term."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 50.") }, ["term"]),
        Tool("find_implementation_clusters", "Find bounded cross-role implementation anchors by literal domain seed or existing semantic evidence ids. The response carries a pending semantic-cluster-shaped anchor group only; it never writes analysis or onto_* facts.", new JsonObject { ["domainSeed"] = StringSchema("Literal domain seed."), ["evidenceIds"] = StringArraySchema("One to 24 existing indexed semantic claim ids."), ["cursor"] = StringSchema("Continuation cursor returned by this operation."), ["limit"] = StringSchema("Page size from 1 to 50.") }),
        Tool("get_domain_topology", "Return a bounded layered evidence graph for a literal domain term. Nodes carry degree, direct observation count, and a visualization-only weight; this never writes ontology facts or makes business-semantic conclusions.", new JsonObject { ["term"] = StringSchema("Literal domain term.") }, ["term"]),
        Tool("run_business_ontology_agent", "Run the bounded agentic business-ontology reconstruction loop through the fixed investigation tools and isolated analysis workspace. The operation accepts only ontologyId, generationId, workItem, and smaller optional budget caps; LLM provider configuration comes from the environment.", new JsonObject
        {
            ["ontologyId"] = StringSchema("Dotted ontology id."),
            ["generationId"] = StringSchema("Active ontology generation id."),
            ["workItem"] = StringSchema("Bounded business work item to investigate."),
            ["maxTurns"] = StringSchema("Optional smaller turn cap; default 12."),
            ["maxQueries"] = StringSchema("Optional smaller query cap; default 8."),
            ["maxRows"] = StringSchema("Optional smaller row cap; default 200."),
            ["maxSourceBytes"] = StringSchema("Optional smaller source-byte cap; default 32768."),
            ["maxInputTokens"] = StringSchema("Optional smaller input-token cap; default 48000."),
            ["maxOutputTokens"] = StringSchema("Optional smaller output-token cap; default 12000."),
            ["maxWallClockSeconds"] = StringSchema("Optional smaller wall-clock cap in seconds; default 180."),
        }, ["ontologyId", "generationId", "workItem"]),
        Tool("run_business_semantic_synthesis", "Run the independent v3 discover, controlled exploration/synthesis, critic, and pending-only semantic synthesis path. It accepts only optional bounded literal domain terms; LLM provider configuration comes from the environment.", new JsonObject
        {
            ["domainTerms"] = StringArraySchema("Optional one to six literal domain terms. Omit to use bounded automatic discovery."),
        }),
        Tool("publish_business_semantic_synthesis", "Run v3 semantic synthesis and atomically publish only its pending review artifacts to the server-configured artifact root. It never writes accepted ontology data and accepts no path or review decision.", new JsonObject
        {
            ["domainTerms"] = StringArraySchema("Optional one to six literal domain terms. Omit to use bounded automatic discovery."),
        }),
        Tool("export_business_ontology_candidates", "Publish one existing isolated analysis run as a validated, hypothesis-only candidate ontology bundle with diagnosis and advisory review artifacts. Output root, validator, evidence resolution, and publication paths are server-configured; this operation never accepts SQL, prompts, paths, provider configuration, records, or review decisions.", new JsonObject
        {
            ["ontologyId"] = StringSchema("Dotted ontology id."),
            ["generationId"] = StringSchema("Ontology generation id."),
            ["analysisRunId"] = StringSchema("Existing isolated analysis run id for this ontology generation."),
            ["version"] = StringSchema("Optional bounded semantic version; default 0.0.0-hypothesis."),
            ["bundleId"] = StringSchema("Optional safe bundle directory token; otherwise derived from the analysis run."),
        }, ["ontologyId", "generationId", "analysisRunId"]),
        Tool("query_named", "Execute a registered CodeKnowledge NamedQuery.", new JsonObject { ["name"] = StringSchema("NamedQuery name"), ["parametersJson"] = StringSchema("JSON object parameters") }, ["name"]),
        Tool("trace", "Trace the shortest call path (CALLS edges) between two symbols. Inputs are symbol ids (symbol: prefix) or exact symbol names; an ambiguous name returns found=false with a bounded candidate list (id/name/kind/fileId/line) instead of guessing. Hops carry clickable path:line locations and per-edge confidence.", new JsonObject
        {
            ["from"] = StringSchema("Source: symbol id (symbol:...) or exact symbol name"),
            ["to"] = StringSchema("Target: symbol id (symbol:...) or exact symbol name"),
            ["maxDepth"] = StringSchema("Maximum hops; a longer shortest path reports found=false. Default: 16."),
            ["minConfidence"] = StringSchema("Exclude CALLS edges below this confidence. Default: 0.7.")
        }, ["from", "to"]),
        Tool("check", "Detect dependency cycles (strongly connected components) over the IMPORTS and/or CALLS graph. Cycle members carry id/name/file and are deterministically ordered.", new JsonObject
        {
            ["cycles"] = StringSchema("Which graph to check for cycles: import, calls, or both. Default: import.")
        }),
        Tool("detect_changes", "Detect which indexed symbols a git change set touches: maps diff line ranges onto ck_symbol ranges, then aggregates bounded transitive impact (callers), affected execution flows and an overall risk rating. Degrades to diagnostics (never an error) outside a git repository.", new JsonObject
        {
            ["workDirectory"] = StringSchema("Git work directory to diff (the indexed repository root). Default: current directory."),
            ["scope"] = StringSchema("Which diff to inspect: unstaged (working tree vs index), staged (index vs HEAD), or compare (baseRef...HEAD merge-base diff). Default: unstaged."),
            ["baseRef"] = StringSchema("Base ref for scope=compare. When omitted, resolved through origin/HEAD, then main, then master; a fully failed chain yields a diagnostic."),
            ["maxImpactDepth"] = StringSchema("Maximum CALLS layers walked upward per changed symbol. Default: 2."),
            ["minConfidence"] = StringSchema("Exclude CALLS edges below this confidence from the impact aggregation. Default: 0.0.")
        }),
        Tool("depa_conformance", "Run the DEPA conformance report over the indexed CodeKnowledge: the red-light rules grouped by dimension (data/effect/processor/layering/fact_source/actor/overdesign/vendor), each rule PASS/GAP/BLOCKED with confidence-ordered violations carrying path:line evidence; BLOCKED names the missing input instead of faking PASS. Placeholder rules (rule-map.md status placeholder-BLOCKED, e.g. V-D2/V-P1/V-A*/V-G2/V-G3/V-V*) always report BLOCKED naming the missing observation class: static observation is insufficient.", new JsonObject
        {
            ["scanFirst"] = BoolSchema("Run the depa_scan pipeline (annotation sync + detection) before aggregating. false aggregates persisted violations only. Default: true."),
            ["dimension"] = StringSchema("Only report this dimension: data, effect, processor, layering, fact_source, actor, overdesign, or vendor."),
            ["workDirectory"] = StringSchema("Directory whose depa-map.json / depa-effects.json (root, then .codument/) are used by default. Default: current directory."),
            ["mapPath"] = StringSchema("Explicit depa-map.json path (overrides the workDirectory resolution)."),
            ["effectsPath"] = StringSchema("Explicit depa-effects.json path (overrides the workDirectory resolution).")
        }),
        Tool("fact_grade_map", "Export the DEPA fact-source grade map: every graded depa_fact_source node (grade 1-7, grade_id, expected owner, anchor path:line) plus the fact_written_by and projection_derived_from adjacency.", new JsonObject()),
        Tool("health_score", "Per-dimension DEPA health: gap/blocked counts, rule coverage and a display-only score for each dimension. Dimensions are judged independently and never merged into a single overall verdict.", new JsonObject
        {
            ["scanFirst"] = BoolSchema("Run the depa_scan pipeline before scoring. Default: true."),
            ["workDirectory"] = StringSchema("Directory whose depa-map.json / depa-effects.json (root, then .codument/) are used by default. Default: current directory."),
            ["mapPath"] = StringSchema("Explicit depa-map.json path (overrides the workDirectory resolution)."),
            ["effectsPath"] = StringSchema("Explicit depa-effects.json path (overrides the workDirectory resolution).")
        })
    ];

    public async Task<object> CallAsync(string name, JsonObject args, CancellationToken cancellationToken = default) =>
        name switch
        {
            "index_repo" => await _indexer.IndexAsync(om, IndexRequestFromArgs(args), cancellationToken),
            "build_wiki" => await BuildWikiToolAsync(args, cancellationToken),
            "symbol_context" => await om.FindSymbolContextAsync(Required(args, "symbolId"), cancellationToken),
            "impact_of_change" => await ImpactOfChangeToolAsync(args, cancellationToken),
            "docs_for_code" => await om.DocsForCodeAsync(Required(args, "targetId"), cancellationToken),
            "explain_relation" => await om.ExplainRelationAsync(Required(args, "fromId"), Required(args, "toId"), cancellationToken),
            "parser_status" => _parserBackendSelector.DescribeStatus(),
            "parse_file" => await _parser.ParseAsync(new SemanticParseRequest(
                Required(args, "filePath"),
                Optional(args, "language"),
                int.TryParse(Optional(args, "maxNodes"), out var maxNodes) ? maxNodes : 256), cancellationToken),
            "index_embeddings" => await _vectorSearch.IndexAsync(om, new VectorIndexRequest(
                Optional(args, "includeSymbols") != "false",
                Optional(args, "includeDocs") != "false",
                int.TryParse(Optional(args, "limit"), out var indexLimit) ? indexLimit : 1000), cancellationToken),
            "semantic_search" => await _vectorSearch.HybridSearchAsync(
                om,
                Required(args, "query"),
                int.TryParse(Optional(args, "limit"), out var searchLimit) ? searchLimit : 10,
                SourceKindsFromArgs(args),
                Optional(args, "mode") ?? "hybrid",
                int.TryParse(Optional(args, "rrfK"), out var rrfK) ? rrfK : 60,
                cancellationToken),
            "overview_graph" => await _overviewGraph.BuildAsync(om, new LlmWikiOverviewGraphRequest(
                int.TryParse(Optional(args, "maxNodes"), out var maxNodes) ? maxNodes : 300,
                int.TryParse(Optional(args, "maxEdges"), out var maxEdges) ? maxEdges : 600,
                CategoriesFromArgs(args)), cancellationToken),
            "ontology_investigation_overview" => await _investigation.GetOverviewAsync(Optional(args, "ontologyId"), cancellationToken),
            "find_business_terms" => await _investigation.FindBusinessTermsAsync(
                Required(args, "term"), Optional(args, "ontologyId"), Optional(args, "cursor"), OptionalPositiveLimit(args, "limit"), cancellationToken),
            "list_use_case_slices" => await _investigation.ListUseCaseSlicesAsync(
                Required(args, "ontologyId"), Optional(args, "cursor"), OptionalPositiveLimit(args, "limit"), cancellationToken),
            "get_use_case_slice" => await _investigation.GetUseCaseSliceAsync(
                Required(args, "ontologyId"), Required(args, "sliceId"), cancellationToken),
            "find_semantic_patterns" => await _investigation.FindSemanticPatternsAsync(
                Required(args, "kind"), Optional(args, "term"), Optional(args, "cursor"), OptionalPositiveLimit(args, "limit"), cancellationToken),
            "list_semantic_evidence" => await _investigation.ListSemanticEvidenceAsync(
                Optional(args, "term"), Optional(args, "cursor"), OptionalPositiveLimit(args, "limit"), cancellationToken),
            "get_semantic_evidence" => await _investigation.GetSemanticEvidenceAsync(
                StringListFromArgs(args, "evidenceIds") ?? throw new ArgumentException("Missing required argument: evidenceIds"), cancellationToken),
            "inspect_ontology_subject" => await _investigation.InspectOntologySubjectAsync(
                Required(args, "ontologyId"), Required(args, "subjectKind"), Required(args, "subjectId"), cancellationToken),
            "discover_domain_charters" => await DiscoverDomainChartersToolAsync(args, cancellationToken),
            "list_cross_layer_use_cases" => await ListCrossLayerUseCasesToolAsync(args, cancellationToken),
            "find_state_rule_clusters" => await FindStateRuleClustersToolAsync(args, cancellationToken),
            "find_implementation_clusters" => await FindImplementationClustersToolAsync(args, cancellationToken),
            "get_domain_topology" => await GetDomainTopologyToolAsync(args, cancellationToken),
            "run_business_ontology_agent" => await RunBusinessOntologyAgentAsync(args, cancellationToken),
            "run_business_semantic_synthesis" => await RunBusinessSemanticSynthesisAsync(args, cancellationToken),
            "publish_business_semantic_synthesis" => await PublishBusinessSemanticSynthesisAsync(args, cancellationToken),
            "export_business_ontology_candidates" => await ExportBusinessOntologyCandidatesAsync(args, cancellationToken),
            "query_named" => await QueryNamedAsync(args, cancellationToken),
            "trace" => await TraceAsync(args, cancellationToken),
            "check" => await CheckCyclesAsync(args, cancellationToken),
            "detect_changes" => await DetectChangesAsync(args, cancellationToken),
            "depa_conformance" => await om.GetConformanceReportAsync(DepaOptionsFromArgs(args, includeDimension: true), cancellationToken),
            "fact_grade_map" => await om.GetFactGradeMapAsync(cancellationToken),
            "health_score" => await om.GetHealthScoreAsync(DepaOptionsFromArgs(args, includeDimension: false), cancellationToken),
            _ => new { error = $"Unknown tool: {name}" },
        };

    private Task<BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter>> DiscoverDomainChartersToolAsync(
        JsonObject args,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["term", "cursor", "limit"]);
        return _investigation.DiscoverDomainChartersAsync(
            Required(args, "term"),
            Optional(args, "cursor"),
            OptionalPositiveLimit(args, "limit"),
            cancellationToken);
    }

    private Task<BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase>> ListCrossLayerUseCasesToolAsync(
        JsonObject args,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["entrySymbolId", "domainSeed", "cursor", "limit"]);
        return _investigation.ListCrossLayerUseCasesAsync(
            Optional(args, "entrySymbolId"),
            Optional(args, "domainSeed"),
            Optional(args, "cursor"),
            OptionalPositiveLimit(args, "limit"),
            cancellationToken);
    }

    private Task<BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster>> FindStateRuleClustersToolAsync(
        JsonObject args,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["term", "cursor", "limit"]);
        return _investigation.FindStateRuleClustersAsync(
            Required(args, "term"),
            Optional(args, "cursor"),
            OptionalPositiveLimit(args, "limit"),
            cancellationToken);
    }

    private Task<BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>> FindImplementationClustersToolAsync(
        JsonObject args,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["domainSeed", "evidenceIds", "cursor", "limit"]);
        return _investigation.FindImplementationClustersAsync(
            Optional(args, "domainSeed"),
            StringListFromArgs(args, "evidenceIds"),
            Optional(args, "cursor"),
            OptionalPositiveLimit(args, "limit"),
            cancellationToken);
    }

    private Task<BusinessOntologyDomainTopology> GetDomainTopologyToolAsync(
        JsonObject args,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["term"]);
        return _investigation.GetDomainTopologyAsync(Required(args, "term"), cancellationToken);
    }

    // --- DEPA conformance tools (track add-llm-wiki-depa-conformance-tools T2.1) ---

    /// <summary>
    /// Zero business logic at the tool layer: parameter plumbing only. depa-map.json /
    /// depa-effects.json default to the workDirectory root, then its .codument/ subdirectory
    /// (design §4.1/§4.3 locations); explicit mapPath/effectsPath override the resolution.
    /// All grouping/scoring lives in the Om.Depa report queries.
    /// </summary>
    private static DepaConformanceOptions DepaOptionsFromArgs(JsonObject args, bool includeDimension)
    {
        var workDirectory = Optional(args, "workDirectory") is { Length: > 0 } dir ? dir : Environment.CurrentDirectory;
        return new DepaConformanceOptions(
            ScanFirst: Optional(args, "scanFirst") != "false",
            Dimension: includeDimension ? Optional(args, "dimension") : null,
            MapPath: ResolveDepaConfigPath(workDirectory, Optional(args, "mapPath"), "depa-map.json"),
            EffectsPath: ResolveDepaConfigPath(workDirectory, Optional(args, "effectsPath"), "depa-effects.json"));
    }

    private static string? ResolveDepaConfigPath(string workDirectory, string? explicitPath, string fileName)
    {
        if (explicitPath is { Length: > 0 })
        {
            return explicitPath;
        }

        var direct = Path.Combine(workDirectory, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var nested = Path.Combine(workDirectory, ".codument", fileName);
        return File.Exists(nested) ? nested : null;
    }

    // --- build_wiki pipeline switch (track add-llm-wiki-llm-pipeline T2.2) ---

    /// <summary>
    /// build_wiki with the add-only pipeline parameter: the default legacy path is the exact
    /// pre-upgrade WikiCompiler call (same request, same WikiBuildResult); pipeline=codument-fractal
    /// routes to FractalWikiPipeline — useLlm resolves the configured backend from the
    /// environment (unavailability degrades to the structure layer, never an error) and force
    /// bypasses the page-level incremental cache. The wiki CLI subcommand reuses this fractal
    /// branch (fix-wiki-fractal-entry-and-context-ranking track T1.1), passing workDirectory.
    /// </summary>
    private async Task<object> BuildWikiToolAsync(JsonObject args, CancellationToken cancellationToken)
    {
        switch ((Optional(args, "pipeline") ?? "").Trim().ToLowerInvariant())
        {
            case "" or "legacy":
                return await _wiki.BuildAsync(
                    om,
                    new WikiBuildRequest(Optional(args, "outputDirectory"), WriteFiles: Optional(args, "writeFiles") == "true"),
                    cancellationToken);

            // "fractal" is the silent pre-rename compatibility alias (decisions #1): accepted so
            // existing calls keep working, but never advertised — the schema, the docs and the
            // result JSON all speak the canonical codument-fractal name only.
            case "codument-fractal" or "fractal":
            {
                var llm = Optional(args, "useLlm") == "false" ? null : LlmClientFactory.FromEnvironment();
                var result = await new FractalWikiPipeline().BuildAsync(om, new FractalWikiOptions(
                    OutputDirectory: Optional(args, "outputDirectory"),
                    WorkDirectory: Optional(args, "workDirectory"),
                    Llm: llm,
                    Force: Optional(args, "force") == "true"), cancellationToken);
                return new
                {
                    pipeline = "codument-fractal",
                    pages = result.Pages,
                    groupCount = result.GroupCount,
                    llmUsed = result.LlmUsed,
                    skipped = result.SkippedPages,
                    rebuilt = result.RebuiltPages,
                    diagnostics = result.Diagnostics,
                };
            }

            case var other:
                throw new ArgumentException($"Invalid pipeline: {other}. Expected legacy or codument-fractal.");
        }
    }

    // --- staleness (track add-llm-wiki-detect-changes T1.1) ---

    /// <summary>
    /// Git-capsule seam (design.md §1): the /api/status surface and the upcoming detect_changes
    /// tool read git through this provider; tests may inject a fake.
    /// </summary>
    internal IGitDiffProvider GitDiffProvider { get; init; } = GitCliDiffProvider.Default;

    /// <summary>
    /// Staleness triple for the status surface (behavior delta case stale-hint / non-git-safe):
    /// indexedCommit = ck_meta.indexed_commit (null before any git-repo index), headCommit =
    /// live HEAD of <paramref name="workDirectory"/> (null when git/repo is unavailable — never
    /// an error), stale = both known ? mismatch : null.
    /// </summary>
    public async Task<(string? IndexedCommit, string? HeadCommit, bool? Stale)> GetStalenessAsync(
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        string? indexedCommit = null;
        try
        {
            var rows = await om.Runtime.Store.RunAsync(
                """?[value] := *ck_meta{ key: "indexed_commit", value }""",
                cancellationToken: cancellationToken);
            if (rows.Rows.Count > 0 && rows.Rows[0][0].ValueKind == JsonValueKind.String)
            {
                var value = rows.Rows[0][0].GetString();
                indexedCommit = string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ck_meta does not exist yet (never indexed) — indexedCommit stays null.
        }

        var headCommit = GitDiffProvider.TryGetHeadCommit(workDirectory);
        bool? stale = indexedCommit is not null && headCommit is not null
            ? !string.Equals(indexedCommit, headCommit, StringComparison.Ordinal)
            : null;
        return (indexedCommit, headCommit, stale);
    }

    // --- detect_changes tool (track add-llm-wiki-detect-changes T2.1) ---

    /// <summary>Reported changed symbols cap (design §3: bounded outputs with truncation counters).</summary>
    private const int MaxChangedSymbols = 100;

    /// <summary>Deduplicated transitive impact cap across all changed symbols.</summary>
    private const int MaxImpactedSymbols = 200;

    /// <summary>Deduplicated affected execution-flow cap across all changed symbols.</summary>
    private const int MaxAffectedProcesses = 20;

    /// <summary>Default baseRef resolution chain for scope=compare without an explicit baseRef.</summary>
    private static readonly string[] CompareBaseRefChain = ["origin/HEAD", "main", "master"];

    /// <summary>
    /// detect_changes tool (design §3): git diff (per scope) → repo-root-relative path match onto
    /// ck_file → changed-line × ck_symbol line-range overlap → changedSymbols (bounded), then per
    /// changed symbol an upward DeepImpactAsync whose layers/processes/risk are aggregated
    /// deduplicated and bounded. Environmental failures (no git, non-git dir, unresolvable base)
    /// surface as diagnostics plus empty results — the tool never throws for them.
    /// </summary>
    private async Task<object> DetectChangesAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var workDirectory = Optional(args, "workDirectory") is { Length: > 0 } dir ? dir : Environment.CurrentDirectory;
        var scope = (Optional(args, "scope") ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "unstaged" => GitDiffScope.Unstaged,
            "staged" => GitDiffScope.Staged,
            "compare" => GitDiffScope.Compare,
            var other => throw new ArgumentException($"Invalid scope: {other}. Expected unstaged, staged, or compare."),
        };
        var maxImpactDepth = PositiveIntOrDefault(Optional(args, "maxImpactDepth"), 2);
        var minConfidence = DoubleOrDefault(Optional(args, "minConfidence"), 0.0);

        var diagnostics = new List<string>();
        var baseRef = Optional(args, "baseRef") is { Length: > 0 } explicitBase ? explicitBase.Trim() : null;
        GitDiffResult diff;
        if (scope == GitDiffScope.Compare && baseRef is null)
        {
            (diff, baseRef) = ResolveCompareBase(workDirectory, diagnostics);
        }
        else
        {
            diff = GitDiffProvider.GetDiff(workDirectory, scope, baseRef);
            diagnostics.AddRange(diff.Diagnostics);
        }

        // Path alignment: RepositoryIndexer stores ck_file.path repo-root-relative with forward
        // slashes — the same form `git diff` prints — so normalization is slash direction plus a
        // "./" strip, matched ordinally.
        var changedByPath = new Dictionary<string, GitChangedFile>(StringComparer.Ordinal);
        foreach (var file in diff.Files)
        {
            changedByPath[NormalizeDiffPath(file.Path)] = file;
        }

        var mappedFiles = new Dictionary<string, string>(StringComparer.Ordinal); // file_id → path
        if (changedByPath.Count > 0)
        {
            try
            {
                var fileRows = await om.Runtime.Store.RunAsync(
                    "?[file_id, path] := *ck_file{ file_id, path }, is_in(path, $paths)",
                    new Dictionary<string, object?> { ["paths"] = changedByPath.Keys.ToList() },
                    cancellationToken: cancellationToken);
                foreach (var row in fileRows.Rows)
                {
                    mappedFiles[AsString(row[0])] = AsString(row[1]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add("the CodeKnowledge index is empty or missing; run index_repo before detect_changes");
            }
        }

        // Changed symbols: a symbol is changed when its [start_line, end_line] overlaps any
        // new-side changed range of its file, or when the whole file was deleted.
        var changedSymbols = new List<(string Id, string Name, string Kind, string Path, int Line)>();
        if (mappedFiles.Count > 0)
        {
            var symbolRows = await om.Runtime.Store.RunAsync(
                """
                ?[symbol_id, name, kind, file_id, start_line, end_line] :=
                  *ck_symbol{ symbol_id, name, kind, file_id, start_line, end_line },
                  is_in(file_id, $ids)
                :sort symbol_id
                """,
                new Dictionary<string, object?> { ["ids"] = mappedFiles.Keys.ToList() },
                cancellationToken: cancellationToken);
            foreach (var row in symbolRows.Rows)
            {
                var path = mappedFiles.GetValueOrDefault(AsString(row[3]), "");
                if (!changedByPath.TryGetValue(path, out var file))
                {
                    continue;
                }

                var startLine = row[4].ValueKind == JsonValueKind.Number ? row[4].GetInt32() : 0;
                var endLine = row[5].ValueKind == JsonValueKind.Number ? row[5].GetInt32() : startLine;
                if (file.IsDeleted || file.Ranges.Any(range => range.Start <= endLine && range.End >= startLine))
                {
                    changedSymbols.Add((AsString(row[0]), AsString(row[1]), AsString(row[2]), path, startLine));
                }
            }
        }

        var reportedChanged = changedSymbols.Count > MaxChangedSymbols
            ? changedSymbols[..MaxChangedSymbols]
            : changedSymbols;

        // Impact aggregation: per reported changed symbol, an upward (blast-radius) DeepImpact;
        // layers, processes and risk are merged deduplicated and bounded.
        var impacted = new SortedDictionary<string, ImpactLayerSymbol>(StringComparer.Ordinal);
        var processes = new SortedDictionary<string, CodeProcessSummary>(StringComparer.Ordinal);
        var truncatedProcessesSeen = 0;
        var risk = RiskLevel.Low;
        var changedIds = new HashSet<string>(reportedChanged.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var symbol in reportedChanged)
        {
            var deep = await om.DeepImpactAsync(symbol.Id, new DeepImpactOptions(
                Direction: ImpactDirection.Up,
                MaxDepth: maxImpactDepth,
                MinConfidence: minConfidence), cancellationToken);
            foreach (var layerSymbol in deep.Layers.SelectMany(layer => layer.Symbols))
            {
                if (!changedIds.Contains(layerSymbol.SymbolId))
                {
                    impacted.TryAdd(layerSymbol.SymbolId, layerSymbol);
                }
            }

            foreach (var process in deep.AffectedProcesses)
            {
                processes.TryAdd(process.ProcessId, process);
            }

            truncatedProcessesSeen += deep.TruncatedProcesses;
            if (deep.Risk > risk)
            {
                risk = deep.Risk;
            }
        }

        var reportedImpacted = impacted.Values.Take(MaxImpactedSymbols).ToArray();
        var reportedProcesses = processes.Values.Take(MaxAffectedProcesses).ToArray();
        var (indexedCommit, headCommit, stale) = await GetStalenessAsync(workDirectory, cancellationToken);
        return new
        {
            scope = scope.ToString().ToLowerInvariant(),
            baseRef,
            workDirectory,
            changedFiles = diff.Files.Select(file => new
            {
                path = NormalizeDiffPath(file.Path),
                deleted = file.IsDeleted,
                binary = file.IsBinary,
                mapped = mappedFiles.ContainsValue(NormalizeDiffPath(file.Path)),
            }).ToArray(),
            unmappedFiles = changedByPath.Keys
                .Where(path => !mappedFiles.ContainsValue(path))
                .Order(StringComparer.Ordinal)
                .ToArray(),
            changedSymbols = reportedChanged.Select(symbol => new
            {
                symbolId = symbol.Id,
                name = symbol.Name,
                kind = symbol.Kind,
                path = symbol.Path,
                line = symbol.Line,
                location = $"{symbol.Path}:{symbol.Line}",
            }).ToArray(),
            truncatedChangedSymbols = changedSymbols.Count - reportedChanged.Count,
            impactedSymbols = reportedImpacted.Select(symbol => new
            {
                symbolId = symbol.SymbolId,
                name = symbol.Name,
                path = symbol.Path,
                line = symbol.Line,
                confidence = symbol.Confidence,
            }).ToArray(),
            truncatedImpactedSymbols = impacted.Count - reportedImpacted.Length,
            affectedProcesses = reportedProcesses,
            truncatedProcesses = processes.Count - reportedProcesses.Length + truncatedProcessesSeen,
            risk = risk.ToString().ToUpperInvariant(),
            indexedCommit,
            headCommit,
            stale,
            staleHint = stale == true
                ? "the index was built at a different commit than the current HEAD; re-run index_repo to refresh"
                : null,
            diagnostics = diagnostics.ToArray(),
        };
    }

    /// <summary>
    /// Resolves the compare base when none was given: the first candidate of origin/HEAD → main →
    /// master whose merge-base diff succeeds wins (an existing ref with an empty diff is a valid
    /// resolution); an exhausted chain degrades to empty results plus a diagnostic.
    /// </summary>
    private (GitDiffResult Diff, string? BaseRef) ResolveCompareBase(string workDirectory, List<string> diagnostics)
    {
        foreach (var candidate in CompareBaseRefChain)
        {
            var attempt = GitDiffProvider.GetDiff(workDirectory, GitDiffScope.Compare, candidate);
            if (attempt.Diagnostics.Count == 0)
            {
                return (attempt, candidate);
            }
        }

        diagnostics.Add(
            "compare scope: no baseRef was given and none of origin/HEAD, main, master resolved in "
            + workDirectory + "; pass an explicit baseRef");
        return (GitDiffResult.Empty, null);
    }

    private static string NormalizeDiffPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    // --- impact_of_change layered upgrade (track deepen-llm-wiki-context-impact T2.1) ---

    /// <summary>
    /// impact_of_change tool: the legacy flat reachability query (edges/impactedIds — same query
    /// as before, so calls without the new arguments keep the exact pre-upgrade fields) enriched
    /// add-only with DeepImpactAsync layers, risk rating and affected execution flows.
    /// direction semantics (decisions #1): up = who transitively calls / depends on me;
    /// down = whom I transitively call / depend on.
    /// </summary>
    private async Task<object> ImpactOfChangeToolAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var symbolId = Required(args, "symbolId");
        var direction = (Optional(args, "direction") ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "up" => ImpactDirection.Up,
            "down" => ImpactDirection.Down,
            var other => throw new ArgumentException($"Invalid direction: {other}. Expected up or down."),
        };
        var minConfidence = DoubleOrDefault(Optional(args, "minConfidence"), 0.0);
        var legacy = await om.ImpactOfChangeAsync(symbolId, minConfidence, cancellationToken);
        var deep = await om.DeepImpactAsync(symbolId, new DeepImpactOptions(
            Direction: direction,
            MaxDepth: PositiveIntOrDefault(Optional(args, "maxDepth"), 3),
            MinConfidence: minConfidence), cancellationToken);
        return legacy with
        {
            Direction = direction == ImpactDirection.Down ? "down" : "up",
            Layers = deep.Layers,
            Risk = deep.Risk.ToString().ToUpperInvariant(),
            AffectedProcesses = deep.AffectedProcesses,
            TruncatedProcesses = deep.TruncatedProcesses,
        };
    }

    private static double DoubleOrDefault(string? value, double fallback) =>
        double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : fallback;

    // --- trace/check tools (track add-llm-wiki-trace-and-check T2.1) ---

    /// <summary>Upper bound of the name-disambiguation candidate list (design §3: bounded, never guess).</summary>
    private const int MaxNameCandidates = 20;

    private async Task<object> TraceAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var options = new TraceOptions(
            MaxDepth: PositiveIntOrDefault(Optional(args, "maxDepth"), 16),
            MinConfidence: double.TryParse(
                Optional(args, "minConfidence"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var minConfidence) ? minConfidence : 0.7);

        var (fromId, fromError) = await ResolveSymbolArgumentAsync(Required(args, "from"), "from", cancellationToken);
        if (fromError is not null)
        {
            return fromError;
        }

        var (toId, toError) = await ResolveSymbolArgumentAsync(Required(args, "to"), "to", cancellationToken);
        if (toError is not null)
        {
            return toError;
        }

        var result = await om.TraceCallPathAsync(fromId!, toId!, options, cancellationToken);
        var filePaths = await FilePathsAsync(result.Hops.Select(h => h.FileId), cancellationToken);
        return new
        {
            found = result.Found,
            reason = result.Reason,
            from = fromId,
            to = toId,
            hops = result.Hops.Select(h => new
            {
                fromId = h.FromId,
                fromName = h.FromName,
                toId = h.ToId,
                toName = h.ToName,
                fileId = h.FileId,
                line = h.Line,
                // Clickable location: ck_file path when resolvable, otherwise the raw file id.
                location = $"{(filePaths.TryGetValue(h.FileId, out var path) && path.Length > 0 ? path : h.FileId)}:{h.Line}",
                kind = h.Kind,
                confidence = h.Confidence,
            }).ToArray(),
        };
    }

    /// <summary>
    /// Resolves a trace endpoint: a "symbol:"-prefixed input is used as the id verbatim; anything
    /// else is an exact ck_symbol name. A unique name resolves to its id; zero or multiple matches
    /// return an error payload (found=false + reason + bounded candidates) instead of guessing.
    /// </summary>
    private async Task<(string? SymbolId, object? Error)> ResolveSymbolArgumentAsync(
        string value, string argument, CancellationToken cancellationToken)
    {
        if (value.StartsWith("symbol:", StringComparison.Ordinal))
        {
            return (value, null);
        }

        // Deterministic order; fetch one row beyond MaxNameCandidates only to detect ambiguity
        // (the surfaced list stays bounded).
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, name, kind, file_id, start_line] :=
              *ck_symbol{ symbol_id, name, kind, file_id, start_line }, name == $name
            :sort symbol_id
            :limit 21
            """,
            new Dictionary<string, object?> { ["name"] = value },
            cancellationToken: cancellationToken);
        if (rows.Rows.Count == 1)
        {
            return (AsString(rows.Rows[0][0]), null);
        }

        var candidates = rows.Rows
            .Take(MaxNameCandidates)
            .Select(row => new
            {
                id = AsString(row[0]),
                name = AsString(row[1]),
                kind = AsString(row[2]),
                fileId = AsString(row[3]),
                line = row[4].ValueKind == JsonValueKind.Number ? row[4].GetInt32() : 0,
            })
            .ToArray();
        var reason = rows.Rows.Count == 0
            ? $"no symbol named '{value}' ({argument}); pass a symbol id (symbol:...) or an exact symbol name"
            : $"symbol name '{value}' ({argument}) is ambiguous ({(rows.Rows.Count > MaxNameCandidates ? $"more than {MaxNameCandidates}" : rows.Rows.Count.ToString())} matches); retry with one of the candidate ids";
        return (null, new { found = false, reason, argument, candidates });
    }

    private async Task<object> CheckCyclesAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var kind = (Optional(args, "cycles") ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "import" => CycleKind.Import,
            "calls" => CycleKind.Calls,
            "both" => CycleKind.Both,
            var other => throw new ArgumentException($"Invalid cycles kind: {other}. Expected import, calls, or both."),
        };
        var cycles = await om.DetectCyclesAsync(kind, cancellationToken);

        // Member decoration: symbol members (CALLS cycles) get name + file via a ck_symbol batch
        // lookup; file members (IMPORTS cycles) resolve their own ck_file path.
        var memberIds = cycles.SelectMany(c => c.Members).Distinct(StringComparer.Ordinal).ToList();
        var symbols = new Dictionary<string, (string Name, string FileId)>(StringComparer.Ordinal);
        if (memberIds.Count > 0)
        {
            var rows = await om.Runtime.Store.RunAsync(
                "?[symbol_id, name, file_id] := *ck_symbol{ symbol_id, name, file_id }, is_in(symbol_id, $ids)",
                new Dictionary<string, object?> { ["ids"] = memberIds },
                cancellationToken: cancellationToken);
            foreach (var row in rows.Rows)
            {
                symbols[AsString(row[0])] = (AsString(row[1]), AsString(row[2]));
            }
        }

        var filePaths = await FilePathsAsync(
            memberIds.Concat(symbols.Values.Select(s => s.FileId)), cancellationToken);
        return new
        {
            kind = kind.ToString().ToLowerInvariant(),
            count = cycles.Count,
            cycles = cycles.Select(c => new
            {
                kind = c.Kind,
                members = c.Members.Select(member =>
                {
                    var isSymbol = symbols.TryGetValue(member, out var symbol);
                    return new
                    {
                        id = member,
                        name = isSymbol ? symbol.Name : "",
                        file = isSymbol
                            ? filePaths.GetValueOrDefault(symbol.FileId, "")
                            : filePaths.GetValueOrDefault(member, ""),
                    };
                }).ToArray(),
            }).ToArray(),
        };
    }

    private async Task<IReadOnlyDictionary<string, string>> FilePathsAsync(
        IEnumerable<string> fileIds, CancellationToken cancellationToken)
    {
        var ids = fileIds.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return paths;
        }

        var rows = await om.Runtime.Store.RunAsync(
            "?[file_id, path] := *ck_file{ file_id, path }, is_in(file_id, $ids)",
            new Dictionary<string, object?> { ["ids"] = ids },
            cancellationToken: cancellationToken);
        foreach (var row in rows.Rows)
        {
            paths[AsString(row[0])] = AsString(row[1]);
        }

        return paths;
    }

    private static string AsString(System.Text.Json.JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();

    private async Task<object> QueryNamedAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var name = Required(args, "name");
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Optional(args, "parametersJson") is { Length: > 0 } raw)
        {
            var doc = JsonDocument.Parse(raw);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                parameters[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number when property.Value.TryGetInt64(out var l) => l,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => property.Value.GetRawText(),
                };
            }
        }

        var engine = new OmQueryEngine(om, CozoOmCodeKnowledgeExtensions.CreateCodeKnowledgeRegistry());
        return await engine.ExecuteNamedAsync(new NamedQueryInput(name, parameters), cancellationToken);
    }

    private async Task<object> RunBusinessOntologyAgentAsync(JsonObject args, CancellationToken cancellationToken)
    {
        RequireExactProperties(args, [
            "ontologyId",
            "generationId",
            "workItem",
            "maxTurns",
            "maxQueries",
            "maxRows",
            "maxSourceBytes",
            "maxInputTokens",
            "maxOutputTokens",
            "maxWallClockSeconds",
        ]);
        var ontologyId = Required(args, "ontologyId");
        var generationId = Required(args, "generationId");
        var workItem = RequiredBoundedWorkItem(args, "workItem");
        var limits = BudgetLimitsFromArgs(args);
        var inputDigest = Digest("run_business_ontology_agent_input", new
        {
            ontologyId,
            generationId,
            workItem,
            budget = BudgetLimitsSummary(limits),
        });
        var startedAt = DateTimeOffset.UtcNow;
        var runId = "analysis-run:agentic:" + Guid.NewGuid().ToString("N");
        var store = new BusinessOntologyAnalysisStore(om);
        var client = LlmClientFactory.FromEnvironment();

        if (!client.IsAvailable)
        {
            var unavailableRun = new BusinessOntologyAnalysisRunInput(
                runId,
                ontologyId,
                generationId,
                "business-ontology-agentic-runner",
                "unavailable",
                "agentic business ontology reconstruction: " + workItem,
                "failed",
                startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                inputDigest);
            await store.AppendRunAsync(unavailableRun, cancellationToken);
            await store.AppendRunCompletionAsync(
                new BusinessOntologyAnalysisRunCompletionInput(
                    runId,
                    BusinessOntologyAgentRunStatuses.Blocked,
                    startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    0,
                    0,
                    0,
                    ""),
                CancellationToken.None);
            return BusinessOntologyAgentRunSummary(
                runId,
                BusinessOntologyAgentRunStatuses.Blocked,
                BusinessOntologyAgentPhases.Explore,
                limits,
                null,
                [],
                [],
                [],
                "unavailable_provider",
                client.UnavailableReason,
                null,
                inputDigest);
        }

        var runInput = new BusinessOntologyAnalysisRunInput(
            runId,
            ontologyId,
            generationId,
            "business-ontology-agentic-runner",
            "environment",
            "agentic business ontology reconstruction: " + workItem,
            "running",
            startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "",
            inputDigest);
        var source = new BusinessOntologyAgentLlmActionSource(client);
        var result = await new BusinessOntologyAgenticReconstructionService(_investigation)
            .RunAsync(new BusinessOntologyAgentRunRequest(
                runId,
                source,
                limits,
                startedAt,
                new BusinessOntologyAgentPersistenceContext(store, runInput),
                workItem), cancellationToken);

        return BusinessOntologyAgentRunSummary(
            runId,
            result.Status,
            result.Phase,
            limits,
            result.BudgetState,
            result.Queries,
            result.Records,
            result.Steps,
            result.RejectionReason,
            null,
            source.LastModel,
            inputDigest,
            result.BudgetRejection,
            result.Finish,
            result.PendingSyntheses);
    }

    private async Task<object> RunBusinessSemanticSynthesisAsync(JsonObject args, CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["domainTerms"]);
        var domainTerms = LiteralStringListFromArgs(args, "domainTerms");
        var modelerConfiguration = LlmClientConfig.FromEnvironment(Environment.GetEnvironmentVariable);
        var criticConfiguration = LlmClientConfig.FromEnvironment(Environment.GetEnvironmentVariable);
        var result = await new BusinessOntologySemanticSynthesisOrchestrator(
                new BusinessOntologyInvestigationServiceOperations(_investigation),
                new BusinessOntologySemanticLlmDependency(
                    "modeler",
                    LlmClientFactory.Create(modelerConfiguration),
                    modelerConfiguration),
                new BusinessOntologySemanticLlmDependency(
                    "critic",
                    LlmClientFactory.Create(criticConfiguration),
                    criticConfiguration))
            .RunAsync(new BusinessOntologySemanticSynthesisRequest(domainTerms), cancellationToken);

        return new
        {
            schemaVersion = "business-ontology-semantic-synthesis-summary-v1",
            operation = "run_business_semantic_synthesis",
            status = ControlledSemanticSynthesisStatus(result.Status),
            phases = result.PhaseTrace.Select(ControlledSemanticSynthesisPhase).ToArray(),
            domains = new
            {
                completed = result.DomainStatuses.Count(item => item.Status is "completed" or "cached"),
                failed = result.DomainStatuses.Count(item => item.Status == "failed"),
                cancelled = result.DomainStatuses.Count(item => item.Status == "cancelled"),
                cached = result.DomainStatuses.Count(item => item.FromCache),
            },
            publication = new
            {
                pending = (result.PendingEnvelope?.PendingCandidateCount ?? 0) > 0,
                acceptedOntologyMutationCount = result.PendingEnvelope?.AcceptedOntologyMutationCount ?? 0,
                candidateDomainCount = result.PendingEnvelope?.DomainCount ?? 0,
                pendingCandidateCount = result.PendingEnvelope?.PendingCandidateCount ?? 0,
                reviewCandidateCount = result.PendingEnvelope?.ReviewCandidateCount ?? 0,
                diagnosisCandidateCount = result.PendingEnvelope?.DiagnosisCandidateCount ?? 0,
            },
            provenance = new
            {
                inputDigest = result.PendingEnvelope?.InputDigest,
                criticDigest = result.PendingEnvelope?.CriticDigest,
                criticSnapshotDigest = result.PendingEnvelope?.CriticRouting.Provenance.SnapshotDigest,
            },
        };
    }

    private async Task<object> PublishBusinessSemanticSynthesisAsync(JsonObject args, CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["domainTerms"]);
        var domainTerms = LiteralStringListFromArgs(args, "domainTerms");
        var modelerConfiguration = LlmClientConfig.FromEnvironment(Environment.GetEnvironmentVariable);
        var criticConfiguration = LlmClientConfig.FromEnvironment(Environment.GetEnvironmentVariable);
        var artifactRoot = Environment.GetEnvironmentVariable("DEPA_WIKI_SEMANTIC_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(artifactRoot))
        {
            throw new InvalidOperationException("DEPA_WIKI_SEMANTIC_ARTIFACT_ROOT must configure pending semantic artifact publication.");
        }
        IBusinessOntologySemanticPublicationQualityEvaluator qualityEvaluator = new BusinessOntologySemanticUnavailablePublicationQualityEvaluator();
        var baselineOntologyId = Environment.GetEnvironmentVariable("DEPA_WIKI_SEMANTIC_BASELINE_ONTOLOGY_ID");
        if (!string.IsNullOrWhiteSpace(baselineOntologyId))
        {
            try
            {
                qualityEvaluator = await BusinessOntologySemanticPublicationQualityEvaluator.CreateAsync(
                    new BusinessOntologyStore(om), baselineOntologyId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Publication remains pending-only; an unavailable trusted baseline is recorded as a
                // quality failure rather than exposed as a database detail or treated as a pass.
                qualityEvaluator = new BusinessOntologySemanticUnavailablePublicationQualityEvaluator();
            }
        }
        var result = await new BusinessOntologySemanticSynthesisOrchestrator(
                new BusinessOntologyInvestigationServiceOperations(_investigation),
                new BusinessOntologySemanticLlmDependency("modeler", LlmClientFactory.Create(modelerConfiguration), modelerConfiguration),
                new BusinessOntologySemanticLlmDependency("critic", LlmClientFactory.Create(criticConfiguration), criticConfiguration),
                qualityEvaluator.SourceFingerprint)
            .RunAsync(new BusinessOntologySemanticSynthesisRequest(domainTerms), cancellationToken,
                new BusinessOntologySemanticArtifactPublisher(artifactRoot, qualityEvaluator));
        return new
        {
            schemaVersion = "business-ontology-semantic-publication-summary-v1",
            operation = "publish_business_semantic_synthesis",
            status = ControlledSemanticSynthesisStatus(result.Status),
            published = result.PendingEnvelope is not null,
            publication = new
            {
                pendingCandidateCount = result.PendingEnvelope?.PendingCandidateCount ?? 0,
                reviewCandidateCount = result.PendingEnvelope?.ReviewCandidateCount ?? 0,
                diagnosisCandidateCount = result.PendingEnvelope?.DiagnosisCandidateCount ?? 0,
                acceptedOntologyMutationCount = result.PendingEnvelope?.AcceptedOntologyMutationCount ?? 0,
            },
            provenance = new { inputDigest = result.PendingEnvelope?.InputDigest, criticDigest = result.PendingEnvelope?.CriticDigest },
        };
    }

    private async Task<object> ExportBusinessOntologyCandidatesAsync(JsonObject args, CancellationToken cancellationToken)
    {
        RequireExactProperties(args, ["ontologyId", "generationId", "analysisRunId", "version", "bundleId"]);
        var configuration = CandidateExportConfiguration.Resolve();
        var result = await new BusinessOntologyCandidateExportService(
            om,
            configuration.OutputRoot,
            configuration.BunExecutable,
            configuration.ValidatorScript)
            .ExportAsync(new BusinessOntologyCandidateExportRequest(
                Required(args, "ontologyId"),
                Required(args, "generationId"),
                Required(args, "analysisRunId"),
                Optional(args, "version"),
                Optional(args, "bundleId")), cancellationToken);
        return new
        {
            schemaVersion = "business-ontology-candidate-export-summary-v1",
            operation = "export_business_ontology_candidates",
            published = result.PublishedRelativePath is not null,
            candidates = new { exported = result.CandidateCount, excluded = result.ExcludedCount },
            diagnosis = new { items = result.DiagnosisItemCount },
            provenance = new { inputDigest = result.InputDigest },
        };
    }

    internal static object BusinessOntologyAgentRunSummary(
        string runId,
        string status,
        string phase,
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState? state,
        IReadOnlyList<BusinessOntologyAgentQueryObservation> queries,
        IReadOnlyList<BusinessOntologyAnalysisRecordInput> records,
        IReadOnlyList<BusinessOntologyAgentStep> steps,
        string? rejectionReason,
        string? providerUnavailableReason,
        string? model,
        string inputDigest,
        BusinessOntologyAgentBudgetRejection? budgetRejection = null,
        BusinessOntologyAgentFinish? finish = null,
        IReadOnlyList<BusinessOntologyAgentPendingSynthesis>? pendingSyntheses = null) => new
        {
            schemaVersion = "business-ontology-agent-run-summary-v1",
            operation = "run_business_ontology_agent",
            status = ControlledRunStatus(status),
            phase = ControlledPhase(phase),
            analysis = new
            {
                recordCount = records.Count,
                candidateDraftCount = records.Count(record => record.Kind == "candidate_draft"),
                gapCount = records.Count(record => record.Kind == "gap"),
                conflictCount = records.Count(record => record.Kind == "conflict"),
            },
            pendingSynthesis = PendingSynthesisSummary(pendingSyntheses),
            budget = new
            {
                limits = BudgetLimitsSummary(limits),
                used = state is null ? null : new
                {
                    state.TurnsUsed,
                    state.QueriesUsed,
                    state.RowsRead,
                    state.SourceBytesRead,
                    state.InputTokensUsed,
                    state.OutputTokensUsed,
                    state.InFlightEffects,
                },
                rejection = budgetRejection is null ? null : new
                {
                    budgetRejection.Metric,
                    budgetRejection.Limit,
                    budgetRejection.Used,
                    budgetRejection.Requested,
                },
            },
            provenance = new
            {
                inputDigest,
                queryCount = queries.Count,
                evidenceCount = queries.SelectMany(query => query.EvidenceRefs)
                    .Select(evidence => evidence.EvidenceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                stepCount = steps.Count,
            },
            finish = finish is null ? null : new
            {
                Status = ControlledFinishStatus(finish.Status),
                unresolvedCount = finish.Unresolved.Count,
            },
            diagnostics = new
            {
                rejected = !string.IsNullOrWhiteSpace(rejectionReason),
                providerUnavailable = !string.IsNullOrWhiteSpace(providerUnavailableReason),
            },
        };

    private static object PendingSynthesisSummary(
        IReadOnlyList<BusinessOntologyAgentPendingSynthesis>? pendingSyntheses)
    {
        var syntheses = pendingSyntheses ?? [];
        return new
        {
            charterCount = syntheses.Sum(synthesis => synthesis.DomainCharters.Count),
            clusterCount = syntheses.Sum(synthesis => synthesis.Clusters.Count),
            workflowCount = syntheses.Sum(synthesis => synthesis.DomainCharters.Sum(charter => charter.WorkflowNames.Count)),
            anchorCount = syntheses.Sum(synthesis => synthesis.Clusters.Sum(cluster => cluster.ImplementationAnchors.Count)),
        };
    }

    private static string? ControlledRunStatus(string value) => value is
        BusinessOntologyAgentRunStatuses.Running or
        BusinessOntologyAgentRunStatuses.Finished or
        BusinessOntologyAgentRunStatuses.Rejected or
        BusinessOntologyAgentRunStatuses.BudgetExhausted or
        BusinessOntologyAgentRunStatuses.Blocked or
        BusinessOntologyAgentRunStatuses.Cancelled or
        BusinessOntologyAgentRunStatuses.TimedOut
        ? value
        : null;

    private static string? ControlledSemanticSynthesisStatus(string value) => value is
        BusinessOntologySemanticSynthesisStatuses.Completed or
        BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures or
        BusinessOntologySemanticSynthesisStatuses.Blocked or
        BusinessOntologySemanticSynthesisStatuses.Cancelled
        ? value
        : null;

    private static string? ControlledSemanticSynthesisPhase(string value) => value is
        "discover:completed" or "discover:failed" or "discover:cancelled" or "discover:skipped" or
        "explore:completed" or "explore:completed_with_failures" or "explore:cancelled" or "explore:skipped" or
        "synthesize:completed" or "synthesize:completed_with_failures" or "synthesize:cancelled" or "synthesize:skipped" or
        "critic:completed" or "critic:failed" or "critic:cancelled" or "critic:skipped" or
        "publish:pending" or "publish:not_published"
        ? value
        : null;

    private static string? ControlledPhase(string value) => value is
        BusinessOntologyAgentPhases.Explore or
        BusinessOntologyAgentPhases.Verify or
        BusinessOntologyAgentPhases.Reconcile or
        BusinessOntologyAgentPhases.Model
        ? value
        : null;

    private static string? ControlledFinishStatus(string value) =>
        BusinessOntologyAgentFinishStatuses.All.Contains(value) ? value : null;

    private static object BudgetLimitsSummary(BusinessOntologyAgentBudgetLimits limits) => new
    {
        limits.MaxTurns,
        limits.MaxQueries,
        limits.MaxRows,
        limits.MaxSourceBytes,
        limits.MaxInputTokens,
        limits.MaxOutputTokens,
        maxWallClockSeconds = (long)limits.MaxWallClock.TotalSeconds,
        limits.MaxConcurrency,
    };

    private static BusinessOntologyAgentBudgetLimits BudgetLimitsFromArgs(JsonObject args)
    {
        var defaults = BusinessOntologyAgentBudgetLimits.Default;
        return new BusinessOntologyAgentBudgetLimits(
            OptionalSmallerPositiveLong(args, "maxTurns", defaults.MaxTurns),
            OptionalSmallerPositiveLong(args, "maxQueries", defaults.MaxQueries),
            OptionalSmallerPositiveLong(args, "maxRows", defaults.MaxRows),
            OptionalSmallerPositiveLong(args, "maxSourceBytes", defaults.MaxSourceBytes),
            OptionalSmallerPositiveLong(args, "maxInputTokens", defaults.MaxInputTokens),
            OptionalSmallerPositiveLong(args, "maxOutputTokens", defaults.MaxOutputTokens),
            TimeSpan.FromSeconds(OptionalSmallerPositiveLong(args, "maxWallClockSeconds", (long)defaults.MaxWallClock.TotalSeconds)),
            defaults.MaxConcurrency);
    }

    private static long OptionalSmallerPositiveLong(JsonObject args, string name, long defaultValue)
    {
        var raw = Optional(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new ArgumentException($"{name} must be a positive integer.");
        }
        if (value > defaultValue)
        {
            throw new ArgumentException($"{name} must be less than or equal to the default cap ({defaultValue}).");
        }
        return value;
    }

    private static string RequiredBoundedWorkItem(JsonObject args, string name)
    {
        var value = Required(args, name).Trim();
        if (value.Length > 2048)
        {
            throw new ArgumentException($"{name} must be at most 2048 characters.");
        }
        return value;
    }

    private static void RequireExactProperties(JsonObject args, IReadOnlyList<string> allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var extras = args.Select(property => property.Key)
            .Where(key => !allowedSet.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (extras.Length != 0)
        {
            throw new ArgumentException(
                "This bounded business-ontology operation rejected arguments: "
                + string.Join(", ", extras));
        }
    }

    private sealed record CandidateExportConfiguration(string OutputRoot, string BunExecutable, string ValidatorScript)
    {
        // Keep generated candidate bundles local to the selected workspace by default. Deployments
        // that publish them elsewhere must opt in through DEPA_WIKI_CANDIDATE_ONTOLOGY_ROOT.
        private static readonly string DefaultOutputRoot = Path.Combine(
            Directory.GetCurrentDirectory(), ".depa-wiki", "ontology-candidates");

        public static CandidateExportConfiguration Resolve()
        {
            var outputRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("DEPA_WIKI_CANDIDATE_ONTOLOGY_ROOT") ?? DefaultOutputRoot);
            Directory.CreateDirectory(outputRoot);
            var workspaceRoot = FindWorkspaceRoot();
            var validator = Path.Combine(workspaceRoot, "skills", "ontology-xml-dsl", "scripts", "validate-ontology-xml.ts");
            if (!File.Exists(validator)) throw new InvalidOperationException("Canonical ontology XML validator is unavailable.");
            return new CandidateExportConfiguration(outputRoot, FindBunExecutable(), validator);
        }

        private static string FindWorkspaceRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "skills", "ontology-xml-dsl", "scripts", "validate-ontology-xml.ts")))
                    {
                        return directory.FullName;
                    }
                }
            }
            throw new InvalidOperationException("Could not locate the canonical ontology XML DSL workspace.");
        }

        private static string FindBunExecutable()
        {
            var candidates = new List<string>();
            var bunInstall = Environment.GetEnvironmentVariable("BUN_INSTALL");
            if (!string.IsNullOrWhiteSpace(bunInstall)) candidates.Add(Path.Combine(bunInstall, "bin", "bun"));
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "bun")));
            return candidates.FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("Bun is required for canonical ontology XML validation.");
        }
    }

    private static string Digest(string operation, object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { operation, value }, LlmWikiJson.Options)))).ToLowerInvariant();

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required is null ? new JsonArray() : new JsonArray(required.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>())
            }
        };

    private static JsonObject StringSchema(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject BoolSchema(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject StringArraySchema(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" }
    };

    private static string Required(JsonObject args, string name) =>
        Optional(args, name) ?? throw new ArgumentException($"Missing required argument: {name}");

    private static string? Optional(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var value) || value is null)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToJsonString(LlmWikiJson.Options),
        };
    }

    private static RepositoryIndexRequest IndexRequestFromArgs(JsonObject args) =>
        new(
            Required(args, "repoPath"),
            DocSymbolLinkMode: ParseDocSymbolLinkMode(Optional(args, "docSymbolLinkMode")),
            MaxDocLinksPerDoc: PositiveIntOrDefault(Optional(args, "maxDocLinksPerDoc"), 20),
            MaxInferredRelations: PositiveIntOrDefault(Optional(args, "maxInferredRelations"), 200_000));

    private static RepositoryDocSymbolLinkMode ParseDocSymbolLinkMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" => RepositoryDocSymbolLinkMode.Local,
            "off" => RepositoryDocSymbolLinkMode.Off,
            "local" => RepositoryDocSymbolLinkMode.Local,
            "global" => RepositoryDocSymbolLinkMode.Global,
            _ => throw new ArgumentException($"Invalid doc-symbol link mode: {value}. Expected off, local, or global.")
        };

    private static int PositiveIntOrDefault(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int? OptionalPositiveLimit(JsonObject args, string name) =>
        Optional(args, name) is not { Length: > 0 } raw ? null : int.TryParse(raw, out var parsed) ? parsed : throw new ArgumentException($"{name} must be an integer.");

    private static IReadOnlyList<string>? SourceKindsFromArgs(JsonObject args) =>
        StringListFromArgs(args, "sourceKinds") ??
        StringListFromArgs(args, "sourceKind") ??
        StringListFromArgs(args, "categories") ??
        StringListFromArgs(args, "category");

    private static IReadOnlyList<string>? CategoriesFromArgs(JsonObject args) =>
        StringListFromArgs(args, "categories") ??
        StringListFromArgs(args, "category") ??
        StringListFromArgs(args, "sourceKinds") ??
        StringListFromArgs(args, "sourceKind");

    private static IReadOnlyList<string>? StringListFromArgs(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonArray array)
        {
            return array
                .Select(item => item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : item?.ToJsonString(LlmWikiJson.Options))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        return Optional(args, name) is { Length: > 0 } raw
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
    }

    private static IReadOnlyList<string>? LiteralStringListFromArgs(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonArray array)
        {
            if (array.Any(item => item?.GetValueKind() != JsonValueKind.String))
            {
                throw new ArgumentException($"{name} must contain only literal strings.");
            }
            return array.Select(item => item!.GetValue<string>()).ToArray();
        }

        if (value.GetValueKind() != JsonValueKind.String)
        {
            throw new ArgumentException($"{name} must be a literal string or string array.");
        }
        return value.GetValue<string>()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
