using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.LlmWiki.SemanticParsing;
using Cozo.DotNet.LlmWiki.Server;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.LlmWiki.VectorSearch;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;
using Cozo.DotNet.Om.Depa;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static IEnumerable<ParsedSymbol> FlattenSymbols(IEnumerable<ParsedSymbol> symbols) =>
    symbols.SelectMany(symbol => new[] { symbol }.Concat(FlattenSymbols(symbol.Children)));

static bool HasEdge(IReadOnlyList<ParsedEdge> edges, string from, string to, string kind, double? confidence = null, string? evidence = null) =>
    edges.Any(edge => edge.FromQualified == from && edge.ToName == to && edge.Kind == kind
        && (confidence is null || Math.Abs(edge.Confidence - confidence.Value) < 1e-9)
        && (evidence is null || edge.Evidence == evidence));

// Oracle-only fast path (add-llm-wiki-eval-baseline track T2.1): the documented reproduction
// command for the eval oracle gate — EVAL_ORACLE_ONLY=1 dotnet run — skips the rest of the suite.
if (Environment.GetEnvironmentVariable("EVAL_ORACLE_ONLY") == "1")
{
    await Cozo.DotNet.LlmWiki.Tests.EvalOracleGateTests.RunAsync((condition, message) => Assert(condition, message));
    Console.WriteLine("Eval oracle gate passed.");
    return;
}

var root = Path.Combine(Path.GetTempPath(), $"llm-wiki-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var toolNames = LlmWikiToolRunner.ToolsJson()
        .Select(tool => tool?["name"]?.GetValue<string>())
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToArray();
    Assert(toolNames.Contains("semantic_search"), "shared tool metadata should include semantic_search");
    // T2.1 (hybrid-search track): semantic_search schema carries add-only mode/rrfK params.
    var semanticSearchSchema = LlmWikiToolRunner.ToolsJson()
        .First(tool => tool?["name"]?.GetValue<string>() == "semantic_search")!["inputSchema"]!["properties"]!.AsObject();
    Assert(semanticSearchSchema.ContainsKey("mode") && semanticSearchSchema.ContainsKey("rrfK"),
        "semantic_search schema should expose add-only mode and rrfK parameters");
    Assert(toolNames.Contains("overview_graph"), "shared tool metadata should include overview_graph");
    // T2.1 (depa-conformance track): the shared tool matrix is exactly these 18 tools
    // (15 pre-existing + depa_conformance / fact_grade_map / health_score).
    string[] expectedToolMatrix =
    [
        "index_repo", "build_wiki", "symbol_context", "impact_of_change", "docs_for_code",
        "explain_relation", "parser_status", "parse_file", "index_embeddings", "semantic_search",
        "overview_graph", "query_named", "trace", "check", "detect_changes",
        "depa_conformance", "fact_grade_map", "health_score"
    ];
    Assert(toolNames.Length == 18 && expectedToolMatrix.All(toolNames.Contains),
        "shared tool metadata should expose exactly the 18-tool matrix including the three DEPA tools (got: "
        + string.Join(", ", toolNames) + ")");
    Assert(LlmWikiCliOptions.KebabOptionToCamelName("--repo-path") == "repoPath", "CLI option conversion should preserve kebab-case to camelCase binding");

    await using (var webApp = LlmWikiWebServer.BuildApp(new LlmWikiServerOptions(
        new CozoWikiStorageOptions("mem", "", root, root),
        root,
        Port: 4186,
        ApiOnly: true)))
    {
        await webApp.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4186") };
        var health = await client.GetStringAsync("/health");
        Assert(health.Contains("\"status\":\"ok\"", StringComparison.Ordinal), "server health endpoint should return ok");
        // T1.1 (detect-changes track, non-git-safe): /api/status on a non-git work dir with an
        // empty db must answer ok with null staleness fields — never an error.
        var statusJson = await client.GetStringAsync("/api/status");
        Assert(statusJson.Contains("\"status\":\"ok\"", StringComparison.Ordinal), "server status endpoint should return ok");
        Assert(statusJson.Contains("\"indexedCommit\":null", StringComparison.Ordinal)
                && statusJson.Contains("\"headCommit\":null", StringComparison.Ordinal)
                && statusJson.Contains("\"stale\":null", StringComparison.Ordinal),
            "a non-git work dir without an index should yield null indexedCommit/headCommit/stale (got: " + statusJson + ")");
        var toolsJson = await client.GetStringAsync("/api/tools");
        Assert(toolsJson.Contains("semantic_search", StringComparison.Ordinal), "server tools endpoint should expose shared tool metadata");
        using var semanticResp = await client.PostAsync("/api/search/semantic", new StringContent("{\"query\":\"SampleService\",\"limit\":\"1\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(semanticResp.IsSuccessStatusCode, "server semantic search endpoint should return success status");
        // T2.1 (hybrid-search track): the request body forwards mode/rrfK add-only params;
        // on an empty db FTS is absent and embeddings are empty — degrade, never a 500.
        using var hybridModeResp = await client.PostAsync("/api/search/semantic", new StringContent("{\"query\":\"SampleService\",\"limit\":\"1\",\"mode\":\"text\",\"rrfK\":\"60\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(hybridModeResp.IsSuccessStatusCode, "server semantic search endpoint should accept mode/rrfK request-body params");
        using var overviewResp = await client.PostAsync("/api/graph/overview", new StringContent("{\"categories\":[\"code\",\"docs\"],\"maxNodes\":\"20\",\"maxEdges\":\"40\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(overviewResp.IsSuccessStatusCode, "server overview graph endpoint should return success status");
        // T2.1 (hybrid-search track): Chinese query end-to-end through the HTTP API — index a
        // CJK fixture via /api/index, then hit it via /api/search/semantic in text mode (UTF-8
        // JSON body → tool args → literalized FTS query → Cangjie-tokenized BM25 hit).
        var httpCjkRoot = Path.Combine(root, "httpcjk");
        Directory.CreateDirectory(httpCjkRoot);
        await File.WriteAllTextAsync(Path.Combine(httpCjkRoot, "README.md"), """
        # 社群模块

        这里实现了计算社群检测的算法说明。
        """);
        using var httpIndexResp = await client.PostAsync("/api/index", new StringContent(
            new JsonObject { ["repoPath"] = httpCjkRoot }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert(httpIndexResp.IsSuccessStatusCode, "server index endpoint should index the CJK fixture");
        using var httpCjkResp = await client.PostAsync("/api/search/semantic", new StringContent("{\"query\":\"社群检测\",\"limit\":\"5\",\"mode\":\"text\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(httpCjkResp.IsSuccessStatusCode, "server semantic search should accept a Chinese query");
        var httpCjkHits = JsonNode.Parse(await httpCjkResp.Content.ReadAsStringAsync())!["hits"]!.AsArray();
        Assert(httpCjkHits.Count > 0 && httpCjkHits.Any(hit => hit!["text"]!.GetValue<string>().Contains("社群检测", StringComparison.Ordinal)),
            "a Chinese query through the HTTP API should hit the CJK doc block end-to-end");
    }

    await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), """
    namespace Demo;

    public class SampleService
    {
        public string GetValue()
        {
            return "ok";
        }
    }
    """);
    await File.WriteAllTextAsync(Path.Combine(root, "README.md"), """
    # Demo

    SampleService is documented here.
    """);
    await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), """
    node_modules/
    ignored_dir/
    *.generated.cs
    """);
    Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
    await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "pkg", "IgnoredPackage.cs"), """
    namespace Ignored;
    public class IgnoredPackage { }
    """);
    Directory.CreateDirectory(Path.Combine(root, "ignored_dir"));
    await File.WriteAllTextAsync(Path.Combine(root, "ignored_dir", "IgnoredByGitIgnore.cs"), """
    namespace Ignored;
    public class IgnoredByGitIgnore { }
    """);
    await File.WriteAllTextAsync(Path.Combine(root, "Ignored.generated.cs"), """
    namespace Ignored;
    public class IgnoredGenerated { }
    """);

    using var db = new CozoDb("mem", "");
    var om = new CozoOm(db);
    var summary = await new RepositoryIndexer().IndexAsync(om, new RepositoryIndexRequest(root, RepositoryId: "repo:test", RepositoryName: "test"));
    Assert(summary.Files >= 2, "indexer should index code and markdown files");
    Assert(summary.SkippedFiles.Contains("Ignored.generated.cs"), "indexer should record ignored files as skipped");
    Assert(summary.Symbols >= 2, "indexer should extract C# symbols");
    Assert(summary.DocBlocks >= 1, "indexer should extract markdown doc blocks");
    var ignoredBatch = await new RepositoryIndexer().BuildBatchAsync(new RepositoryIndexRequest(root, RepositoryId: "repo:test", RepositoryName: "test"));
    var indexedFiles = ignoredBatch.Batch.Files ?? [];
    Assert(!indexedFiles.Any(file => file.Path.Contains("node_modules", StringComparison.Ordinal)), "indexer should honor .gitignore directory rules");
    Assert(!indexedFiles.Any(file => file.Path.Contains("ignored_dir", StringComparison.Ordinal)), "indexer should honor custom .gitignore directory rules");
    Assert(!indexedFiles.Any(file => file.Path.Contains("Ignored.generated.cs", StringComparison.Ordinal)), "indexer should honor .gitignore file glob rules");

    var wiki = await new WikiCompiler().BuildAsync(om);
    Assert(wiki.IndexMarkdown.Contains("Code Wiki", StringComparison.Ordinal), "wiki index should be generated");
    Assert(wiki.Pages.Count > 0, "wiki should contain pages");

    var sampleSymbol = (await om.BuildWikiPlanAsync()).Pages
        .SelectMany(page => page.SymbolIds)
        .First(id => id.Contains("SampleService", StringComparison.Ordinal));
    var context = await om.FindSymbolContextAsync(sampleSymbol);
    Assert(context.Symbol is not null && context.Symbol.Name == "SampleService", "symbol context should resolve indexed symbol");

    var docs = await om.DocsForCodeAsync(sampleSymbol);
    Assert(!docs.Missing && docs.Docs.Count > 0, "docs_for_code should find markdown mentions");

    var boundedRoot = Path.Combine(root, "bounded");
    Directory.CreateDirectory(boundedRoot);
    for (var i = 0; i < 30; i++)
    {
        var featureDir = Path.Combine(boundedRoot, $"feature-{i}");
        Directory.CreateDirectory(featureDir);
        await File.WriteAllTextAsync(Path.Combine(featureDir, "Feature.cs"), $$"""
        namespace Demo.Feature{{i}};
        public class CommonSymbol
        {
            public string GetValue() => "{{i}}";
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(featureDir, "README.md"), """
        # Feature

        CommonSymbol is documented locally.
        """);
    }

    var boundedBatch = await new RepositoryIndexer().BuildBatchAsync(new RepositoryIndexRequest(
        boundedRoot,
        RepositoryId: "repo:bounded",
        RepositoryName: "bounded",
        MaxDocLinksPerDoc: 3,
        ParserMode: RepositoryParserMode.RegexOnly));
    var boundedDocumentLinks = (boundedBatch.Batch.Edges ?? [])
        .Where(edge => edge.Kind == CodeEdgeKinds.DocLinks)
        .ToArray();
    Assert(boundedDocumentLinks.Length <= 30, "local doc-symbol linking should not create doc x symbol relation products");
    Assert(boundedDocumentLinks.Length >= 20, "local doc-symbol linking should preserve same-directory documentation links");
    Assert((boundedBatch.Batch.Edges ?? []).All(edge => Math.Abs(edge.Confidence - 0.3) < 1e-9 && edge.Resolver == "regex"),
        "regex indexer edges should carry confidence=0.3 and resolver=regex");
    Assert((boundedBatch.Batch.Symbols ?? []).Count > 0 && (boundedBatch.Batch.Symbols ?? []).All(symbol => symbol.Resolver == "regex"),
        "ParserMode.RegexOnly should force the legacy regex tier for every symbol");

    var offBatch = await new RepositoryIndexer().BuildBatchAsync(new RepositoryIndexRequest(
        boundedRoot,
        RepositoryId: "repo:bounded-off",
        RepositoryName: "bounded-off",
        DocSymbolLinkMode: RepositoryDocSymbolLinkMode.Off));
    Assert(!(offBatch.Batch.Edges ?? []).Any(edge => edge.Kind == CodeEdgeKinds.DocLinks), "doc-symbol link mode off should skip inferred documentation links");

    // T3.1: indexer integration — tree-sitter primary path with per-file regex fallback.
    // Delta cases: requirements/native-backend {native-preferred, fallback-regex}; dogfood-coverage
    // is gated in the track harness (findings.md records the counts).
    var integRoot = Path.Combine(root, "integ");
    Directory.CreateDirectory(integRoot);
    await File.WriteAllTextAsync(Path.Combine(integRoot, "Svc.cs"), """
    using System;

    namespace Integ;

    public class OrderService
    {
        public int Total { get; }

        public int Add(int amount) => amount;
    }
    """);
    await File.WriteAllTextAsync(Path.Combine(integRoot, "app.ts"), """
    import { join } from "path";

    export class AppRunner {
        run(name: string): string {
            return join(name, "x");
        }
    }
    """);
    await File.WriteAllTextAsync(Path.Combine(integRoot, "legacy.py"), """
    def legacy():
        return 1
    """);
    await File.WriteAllTextAsync(Path.Combine(integRoot, "Broken.cs"), "class {{{{\n");

    var integRequest = new RepositoryIndexRequest(
        integRoot,
        RepositoryId: "repo:integ",
        RepositoryName: "integ",
        IncludeExtensions: [".cs", ".ts", ".py"],
        UseGitIgnore: false);
    var integBatch = await new RepositoryIndexer().BuildBatchAsync(integRequest);
    var integFiles = integBatch.Batch.Files ?? [];
    var integSymbols = integBatch.Batch.Symbols ?? [];
    var integEdges = integBatch.Batch.Edges ?? [];
    Assert(integFiles.Count == 4, "mixed repo (C#/TS/grammarless/broken) should index every file without failing");
    Assert(integSymbols.All(s => s.SymbolId.StartsWith("symbol:file:repo:integ:", StringComparison.Ordinal)),
        "every extraction tier should keep the symbol:{fileId}:{line}:{name} id convention (12-tool compatibility)");

    // Whole-batch ingestion must succeed regardless of which per-file tier produced the facts.
    var integSummary = await new RepositoryIndexer().IndexAsync(om, integRequest with { RepositoryId = "repo:integ-db" });
    Assert(integSummary.Files == 4 && integSummary.Symbols > 0, "mixed-tier batch should ingest into CodeKnowledge successfully");

    // fallback-regex: without native grammars every supported language degrades to the regex tier
    // (the cli backend parses but extracts no symbols, so indexing falls through to regex).
    var degradedIndexer = new RepositoryIndexer(ParserBackendSelector.CreateDefault(nativeProbeDirectories: ["/nonexistent-treesitter-native-libs"]));
    var degradedIndexBatch = await degradedIndexer.BuildBatchAsync(integRequest with { RepositoryId = "repo:integ-degraded" });
    var degradedIndexSymbols = degradedIndexBatch.Batch.Symbols ?? [];
    Assert(degradedIndexSymbols.Count > 0 && degradedIndexSymbols.All(s => s.Resolver == "regex"),
        "without native grammars the indexer should fall back to resolver=regex and still succeed");

    if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
    {
        // native-preferred: C# and TS files produce resolver=treesitter facts with tree-shaped ParentId.
        var csFileId = integFiles.Single(f => f.Path == "Svc.cs").FileId;
        var tsFileId = integFiles.Single(f => f.Path == "app.ts").FileId;
        var csSymbols = integSymbols.Where(s => s.FileId == csFileId).ToArray();
        Assert(csSymbols.Length >= 4 && csSymbols.All(s => s.Resolver == "treesitter"),
            "C# file should produce resolver=treesitter symbols on the native primary path");
        var integNs = csSymbols.Single(s => s.Kind == "namespace");
        var orderService = csSymbols.Single(s => s.Name == "OrderService");
        var addMethod = csSymbols.Single(s => s.Name == "Add");
        Assert(integNs.ParentId == "" && orderService.ParentId == integNs.SymbolId && addMethod.ParentId == orderService.SymbolId,
            "ParentId should reflect the parsed symbol tree");
        Assert(orderService.SymKey == "csharp:Integ.OrderService#0" && orderService.Lang == "csharp" && orderService.Exported,
            "tree-sitter symbols should carry sym_key/lang/exported");
        Assert(integEdges.Any(e => e.FromId == csFileId && e.ToId == integNs.SymbolId && e.Kind == CodeEdgeKinds.Contains
                && e.Resolver == "treesitter" && Math.Abs(e.Confidence - 0.9) < 1e-9),
            "file should CONTAINS the namespace at treesitter confidence 0.9");
        Assert(integEdges.Any(e => e.FromId == orderService.SymbolId && e.ToId == addMethod.SymbolId && e.Kind == CodeEdgeKinds.HasMethod
                && e.Resolver == "treesitter"),
            "class should HAS_METHOD its method with both endpoints resolved to symbol ids");
        Assert(integEdges.Any(e => e.FromId == orderService.SymbolId && e.Kind == CodeEdgeKinds.HasProperty && e.Resolver == "treesitter"),
            "class should HAS_PROPERTY its property");

        // IMPORTS merge: tree-sitter supersedes the regex import scan per file — exactly one edge
        // per import target on the shared import:{hash} node, no regex-tier duplicate.
        var csImports = integEdges.Where(e => e.FromId == csFileId && e.Kind == CodeEdgeKinds.Imports).ToArray();
        Assert(csImports.Length == 1 && csImports[0].Resolver == "treesitter" && Math.Abs(csImports[0].Confidence - 0.9) < 1e-9
                && csImports[0].ToId.StartsWith("import:", StringComparison.Ordinal),
            "C# using should yield a single treesitter IMPORTS edge on the shared import id space");
        var tsImports = integEdges.Where(e => e.FromId == tsFileId && e.Kind == CodeEdgeKinds.Imports).ToArray();
        Assert(tsImports.Length == 1 && tsImports[0].Resolver == "treesitter",
            "TS import should yield a single treesitter IMPORTS edge (no regex duplicate)");

        // Broken C# degrades per-file to regex with a recorded diagnostic; the batch still built.
        Assert((integBatch.Batch.Diagnostics ?? []).Any(d => d.Kind == "parser_fallback" && d.TargetId.EndsWith("Broken.cs", StringComparison.Ordinal)),
            "error-ridden zero-symbol parse should record a parser_fallback diagnostic and degrade to regex");
        Assert(integSymbols.Any(s => s.FileId == tsFileId && s.Resolver == "treesitter" && s.SymKey.StartsWith("ts:", StringComparison.Ordinal)),
            "TS file should produce resolver=treesitter symbols with ts sym_keys");
    }

    var parser = new TreeSitterCliParser();
    var status = await parser.GetStatusAsync();
    Assert(status.Parser == "tree-sitter", "parser status should report tree-sitter parser");
    var parse = await parser.ParseAsync(new SemanticParseRequest(Path.Combine(root, "Sample.cs"), MaxNodes: 8));
    if (status.Available)
    {
        Assert(parse.Success || parse.Diagnostics.Count > 0, "tree-sitter parse should either succeed or return grammar diagnostics");
    }
    else
    {
        Assert(!parse.Success && parse.Diagnostics.Any(d => d.Code == "TSCLI001"), "missing tree-sitter CLI should return diagnostic");
    }

    var vectorSearch = new CozoVectorSearchService();
    var vectorIndex = await vectorSearch.IndexAsync(om);
    Assert(vectorIndex.Indexed > 0, "vector index should embed CodeKnowledge text");
    if (OnnxMiniLmEmbeddingProvider.TryCreateBundled() is { } onnxProvider)
    {
        using (onnxProvider)
        {
            var embedding = await onnxProvider.EmbedAsync("SampleService returns ok values");
            Assert(embedding.Dimensions == 384, "MiniLM embedding should be 384-dimensional");
            Assert(embedding.Values.Count == 384, "MiniLM embedding value count should be 384");
            var norm = Math.Sqrt(embedding.Values.Sum(value => value * value));
            Assert(Math.Abs(norm - 1.0) < 0.001, "MiniLM embedding should be L2-normalized");
            Assert(vectorIndex.Model == OnnxMiniLmEmbeddingProvider.DefaultModelName, "default vector search should use bundled MiniLM when available");
            Assert(vectorIndex.Dimensions == 384, "default vector search should create 384-dimensional embeddings when MiniLM is available");
        }
    }

    var vectorResult = await vectorSearch.SearchAsync(om, new VectorSearchRequest("SampleService", Limit: 5));
    Assert(vectorResult.Hits.Count > 0, "semantic search should return hits");
    Assert(vectorResult.Hits.Any(hit => hit.Text.Contains("SampleService", StringComparison.OrdinalIgnoreCase)), "semantic search should find SampleService-related text");
    var docOnlyResult = await vectorSearch.SearchAsync(om, new VectorSearchRequest("SampleService", Limit: 5, SourceKinds: ["docs"]));
    Assert(docOnlyResult.Hits.Count > 0, "docs-only semantic search should return hits");
    Assert(docOnlyResult.Hits.All(hit => hit.SourceKind == "doc"), "docs-only semantic search should only return doc hits");
    var codeOnlyResult = await vectorSearch.SearchAsync(om, new VectorSearchRequest("SampleService", Limit: 5, SourceKinds: ["code"]));
    Assert(codeOnlyResult.Hits.Count > 0, "code-only semantic search should return hits");
    Assert(codeOnlyResult.Hits.All(hit => hit.SourceKind == "symbol"), "code-only semantic search should only return symbol hits");

    var overview = await new LlmWikiOverviewGraphBuilder().BuildAsync(om, new LlmWikiOverviewGraphRequest(MaxNodes: 80, MaxEdges: 120, Categories: ["code", "docs"]));
    Assert(overview.Nodes.Count > 0, "overview graph should include nodes");
    Assert(overview.Edges.Count > 0, "overview graph should include edges");
    Assert(overview.Nodes.Any(node => node.Group == "symbol"), "overview graph should include code symbol nodes");
    Assert(overview.Nodes.Any(node => node.Group == "doc"), "overview graph should include documentation nodes");
    Assert(overview.Edges.Any(edge => edge.Label == CodeEdgeKinds.Contains), "overview graph should label containment edges with v2 CONTAINS");
    Assert(!overview.Edges.Any(edge => edge.Label is "defines" or "documents" or "imports" or "mentions" or "contains"),
        "overview graph should not emit v1 lowercase edge kinds");

    // T2.2: 12-tool dispatch smoke through LlmWikiToolRunner on the v2 schema.
    // Gap tools not exercised elsewhere: symbol_context/impact_of_change/explain_relation/
    // docs_for_code/query_named via runner dispatch; the rest re-checked through dispatch.
    var runner = new LlmWikiToolRunner(om);
    var runnerContext = (SymbolContextResult)await runner.CallAsync("symbol_context", new JsonObject { ["symbolId"] = sampleSymbol });
    Assert(runnerContext.Symbol is not null && runnerContext.Symbol.Name == "SampleService", "runner symbol_context should resolve symbol on v2 schema");
    var sampleFileId = runnerContext.Symbol!.FileId;

    var impact = (ImpactOfChangeResult)await runner.CallAsync("impact_of_change", new JsonObject { ["symbolId"] = sampleFileId });
    Assert(impact.ImpactedIds.Contains(sampleSymbol), "runner impact_of_change should reach contained symbols via ck_edge CONTAINS");
    Assert(impact.Edges.All(edge => edge.Kind is CodeEdgeKinds.Contains or CodeEdgeKinds.Imports or CodeEdgeKinds.DocLinks
            or CodeEdgeKinds.Mentions or CodeEdgeKinds.HasMethod or CodeEdgeKinds.HasProperty
            or CodeEdgeKinds.Extends or CodeEdgeKinds.Implements or "near"),
        "impact edges should only carry v2 kinds (plus regex-tier lowercase near)");

    // On the tree-sitter path the file CONTAINS the namespace and the namespace CONTAINS the class,
    // so explain the direct parent edge (regex tier: parent is the file itself — both shapes pass).
    var sampleParentId = runnerContext.Incoming.First(rel => rel.Kind == CodeEdgeKinds.Contains).FromId;
    var explain = (ExplainRelationResult)await runner.CallAsync("explain_relation", new JsonObject { ["fromId"] = sampleParentId, ["toId"] = sampleSymbol });
    Assert(explain.Relations.Count > 0 && explain.Relations.All(rel => rel.Kind == CodeEdgeKinds.Contains),
        "runner explain_relation should surface CONTAINS evidence from ck_edge");

    var runnerDocs = (DocsForCodeResult)await runner.CallAsync("docs_for_code", new JsonObject { ["targetId"] = sampleSymbol });
    Assert(!runnerDocs.Missing && runnerDocs.Docs.Count > 0, "runner docs_for_code should find DOC_LINKS docs on v2 schema");

    var namedImpact = (Cozo.DotNet.Om.Query.OmQueryExecutionResult)await runner.CallAsync("query_named", new JsonObject
    {
        ["name"] = "code.impactOfChange",
        ["parametersJson"] = $"{{\"symbolId\":\"{sampleFileId}\"}}"
    });
    Assert(namedImpact.Success, "runner query_named code.impactOfChange should succeed on v2 ck_edge");
    Assert(namedImpact.Graph is { Edges.Count: > 0 }, "runner query_named code.impactOfChange should return graph edges");

    var runnerIndex = (RepositoryIndexSummary)await runner.CallAsync("index_repo", new JsonObject { ["repoPath"] = root });
    Assert(runnerIndex.Symbols >= 2, "runner index_repo should index symbols on v2 schema");
    var runnerWiki = (WikiBuildResult)await runner.CallAsync("build_wiki", new JsonObject());
    Assert(runnerWiki.Pages.Count > 0, "runner build_wiki should build pages on v2 schema");

    // T2.2 (llm-pipeline track): build_wiki add-only params — the legacy default above stays a
    // WikiBuildResult untouched; pipeline=fractal routes to FractalWikiPipeline with page-level
    // incremental builds (skipped/rebuilt counters) and the force full-rebuild switch.
    var buildWikiSchema = LlmWikiToolRunner.ToolsJson()
        .First(tool => tool?["name"]?.GetValue<string>() == "build_wiki")!["inputSchema"]!["properties"]!.AsObject();
    Assert(new[] { "pipeline", "useLlm", "force", "outputDirectory" }.All(buildWikiSchema.ContainsKey),
        "build_wiki schema should expose add-only pipeline/useLlm/force params next to outputDirectory");
    static JsonObject WikiToolJson(object result) =>
        System.Text.Json.JsonSerializer.SerializeToNode(result, LlmWikiJson.Options)!.AsObject();
    var fractalDir = Path.Combine(root, "fractal-wiki");
    var fractalFirst = WikiToolJson(await runner.CallAsync("build_wiki", new JsonObject
    {
        ["pipeline"] = "fractal",
        ["useLlm"] = "false",
        ["outputDirectory"] = fractalDir
    }));
    var fractalPageCount = fractalFirst["pages"]!.AsArray().Count;
    // T1.1 (fix-wiki-fractal-entry-and-context-ranking track): canonical pipeline name —
    // delta case tool-alias {fractal-alias-compat}: pipeline=fractal stays a silent
    // compatibility alias whose result echoes the canonical codument-fractal name.
    Assert(fractalFirst["pipeline"]!.GetValue<string>() == "codument-fractal",
        "build_wiki pipeline=fractal should stay a silent alias echoing pipeline=codument-fractal");
    Assert(fractalPageCount > 0
            && fractalFirst["rebuilt"]!.GetValue<int>() == fractalPageCount
            && fractalFirst["skipped"]!.GetValue<int>() == 0
            && !fractalFirst["llmUsed"]!.GetValue<bool>()
            && fractalFirst["groupCount"] is not null,
        "build_wiki pipeline=fractal should run the fractal pipeline and report pages/groupCount/llmUsed/skipped/rebuilt");
    Assert(File.Exists(Path.Combine(fractalDir, "impl", "global", "overview", "index.md")),
        "build_wiki pipeline=fractal should write the preview pages to outputDirectory");
    var fractalSecond = WikiToolJson(await runner.CallAsync("build_wiki", new JsonObject
    {
        ["pipeline"] = "fractal",
        ["useLlm"] = "false",
        ["outputDirectory"] = fractalDir
    }));
    Assert(fractalSecond["skipped"]!.GetValue<int>() == fractalPageCount && fractalSecond["rebuilt"]!.GetValue<int>() == 0,
        "build_wiki pipeline=fractal should skip every unchanged page on the second run");
    var fractalForced = WikiToolJson(await runner.CallAsync("build_wiki", new JsonObject
    {
        ["pipeline"] = "fractal",
        ["useLlm"] = "false",
        ["force"] = "true",
        ["outputDirectory"] = fractalDir
    }));
    Assert(fractalForced["rebuilt"]!.GetValue<int>() == fractalPageCount && fractalForced["skipped"]!.GetValue<int>() == 0,
        "build_wiki force=true should rebuild every page");
    // T1.1 (fix-wiki-fractal-entry-and-context-ranking track): delta case tool-alias
    // {canonical-name-in-tool} — pipeline=codument-fractal runs the fractal pipeline and the
    // schema describes codument-fractal as the canonical name without advertising the alias.
    var canonicalFractal = WikiToolJson(await runner.CallAsync("build_wiki", new JsonObject
    {
        ["pipeline"] = "codument-fractal",
        ["useLlm"] = "false",
        ["outputDirectory"] = fractalDir
    }));
    Assert(canonicalFractal["pipeline"]!.GetValue<string>() == "codument-fractal"
            && canonicalFractal["pages"]!.AsArray().Count == fractalPageCount,
        "build_wiki pipeline=codument-fractal should run the fractal pipeline under the canonical name");
    var pipelineDescription = buildWikiSchema["pipeline"]!["description"]!.GetValue<string>();
    Assert(pipelineDescription.Contains("codument-fractal", StringComparison.Ordinal),
        "build_wiki schema should name codument-fractal as the canonical pipeline");
    Assert(!pipelineDescription.Replace("codument-fractal", "", StringComparison.Ordinal).Contains("fractal", StringComparison.Ordinal),
        "build_wiki schema should not advertise the bare fractal alias");
    var runnerParserStatus = (ParserStatus)await runner.CallAsync("parser_status", new JsonObject());
    Assert(runnerParserStatus.Parser == "tree-sitter", "runner parser_status should report tree-sitter");
    var runnerParse = (SemanticParseResult)await runner.CallAsync("parse_file", new JsonObject { ["filePath"] = Path.Combine(root, "Sample.cs"), ["maxNodes"] = "8" });
    Assert(runnerParse.Success || runnerParse.Diagnostics.Count > 0, "runner parse_file should succeed or report diagnostics");
    var runnerEmbed = (VectorIndexResult)await runner.CallAsync("index_embeddings", new JsonObject { ["limit"] = "5" });
    Assert(runnerEmbed.Indexed > 0, "runner index_embeddings should embed sources");
    var runnerSearch = (VectorSearchResult)await runner.CallAsync("semantic_search", new JsonObject { ["query"] = "SampleService", ["limit"] = "3" });
    Assert(runnerSearch.Hits.Count > 0, "runner semantic_search should return hits");
    var runnerOverview = (LlmWikiOverviewGraphResult)await runner.CallAsync("overview_graph", new JsonObject { ["maxNodes"] = "40", ["maxEdges"] = "80" });
    Assert(runnerOverview.Nodes.Count > 0 && runnerOverview.Edges.Count > 0, "runner overview_graph should return a bounded graph");

    // T2.1 (trace-and-check track): trace/check tool wiring through runner dispatch —
    // completes the 14-tool smoke matrix. Delta case requirements/trace-and-cycles
    // {name-disambiguation} is covered here at the tool layer; the API-layer cases
    // (known-path/no-path/min-confidence-cut/import-cycle/no-cycle) live in the Om tests.
    using (var traceDb = new CozoDb("mem", ""))
    {
        var traceOm = new CozoOm(traceDb);
        await traceOm.InitCodeKnowledgeAsync();
        var manyDuplicates = Enumerable.Range(0, 25)
            .Select(i => new CodeSymbolFact($"symbol:file:tw2:{100 + i}:Many", "file:tw2", "Many", "method", 100 + i, 100 + i, "Many()"))
            .ToArray();
        await traceOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Files:
            [
                new CodeFileFact("file:tw", "repo:tw", "src/Alpha.cs"),
                new CodeFileFact("file:tw2", "repo:tw", "src/Beta.cs"),
                new CodeFileFact("file:tm1", "repo:tw", "src/m1.cs"),
                new CodeFileFact("file:tm2", "repo:tw", "src/m2.cs"),
                new CodeFileFact("file:tm3", "repo:tw", "src/m3.cs")
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:file:tw:1:Alpha", "file:tw", "Alpha", "method", 1, 10, "Alpha()"),
                new CodeSymbolFact("symbol:file:tw:11:Beta", "file:tw", "Beta", "method", 11, 20, "Beta()"),
                new CodeSymbolFact("symbol:file:tw:21:Gamma", "file:tw", "Gamma", "method", 21, 30, "Gamma()"),
                // name-disambiguation: two same-name symbols in different files/kinds.
                new CodeSymbolFact("symbol:file:tw:31:Dup", "file:tw", "Dup", "method", 31, 40, "Dup()"),
                new CodeSymbolFact("symbol:file:tw2:5:Dup", "file:tw2", "Dup", "function", 5, 9, "Dup()"),
                .. manyDuplicates
            ],
            Edges:
            [
                // Known CALLS chain Alpha -> Beta -> Gamma at confidence 0.9.
                new CodeEdgeFact("symbol:file:tw:1:Alpha", "symbol:file:tw:11:Beta", CodeEdgeKinds.Calls, "file:tw", 5, 0.9, "roslyn", "Alpha calls Beta"),
                new CodeEdgeFact("symbol:file:tw:11:Beta", "symbol:file:tw:21:Gamma", CodeEdgeKinds.Calls, "file:tw", 15, 0.9, "roslyn", "Beta calls Gamma"),
                // IMPORTS cycle tm1 -> tm2 -> tm3 -> tm1; the CALLS graph stays acyclic for now.
                new CodeEdgeFact("file:tm1", "file:tm2", CodeEdgeKinds.Imports, "file:tm1", 1, 0.3, "regex", ""),
                new CodeEdgeFact("file:tm2", "file:tm3", CodeEdgeKinds.Imports, "file:tm2", 1, 0.3, "regex", ""),
                new CodeEdgeFact("file:tm3", "file:tm1", CodeEdgeKinds.Imports, "file:tm3", 1, 0.3, "regex", "")
            ]));

        var traceRunner = new LlmWikiToolRunner(traceOm);
        static JsonObject ToolResultJson(object result) =>
            System.Text.Json.JsonSerializer.SerializeToNode(result, LlmWikiJson.Options)!.AsObject();

        // Unique names resolve to their symbol ids and trace end-to-end through dispatch.
        var traceByName = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject { ["from"] = "Alpha", ["to"] = "Gamma" }));
        Assert(traceByName["found"]!.GetValue<bool>(), "trace by unique names should find the Alpha->Beta->Gamma path");
        var traceHops = traceByName["hops"]!.AsArray();
        Assert(traceHops.Count == 2, "trace Alpha->Gamma should have exactly two hops");
        Assert(traceHops[0]!["fromName"]!.GetValue<string>() == "Alpha" && traceHops[0]!["toName"]!.GetValue<string>() == "Beta"
            && traceHops[1]!["toName"]!.GetValue<string>() == "Gamma",
            "trace hops should be ordered and carry symbol names");
        Assert(traceHops[0]!["location"]!.GetValue<string>() == "src/Alpha.cs:5"
            && traceHops[1]!["location"]!.GetValue<string>() == "src/Alpha.cs:15",
            "trace hops should carry clickable path:line locations resolved via ck_file");
        Assert(Math.Abs(traceHops[0]!["confidence"]!.GetValue<double>() - 0.9) < 1e-9, "trace hops should carry edge confidence");

        // symbol: prefixed inputs are treated as ids and bypass name disambiguation.
        var traceById = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject
        {
            ["from"] = "symbol:file:tw:1:Alpha",
            ["to"] = "symbol:file:tw:21:Gamma"
        }));
        Assert(traceById["found"]!.GetValue<bool>() && traceById["hops"]!.AsArray().Count == 2,
            "trace by symbol ids should find the same path without name resolution");

        // maxDepth/minConfidence arguments flow into TraceOptions.
        var traceDepthCut = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject { ["from"] = "Alpha", ["to"] = "Gamma", ["maxDepth"] = "1" }));
        Assert(!traceDepthCut["found"]!.GetValue<bool>()
            && traceDepthCut["reason"]!.GetValue<string>().Contains("MaxDepth", StringComparison.Ordinal),
            "trace maxDepth argument should cut paths longer than the limit");
        var traceLowConfidence = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject { ["from"] = "Alpha", ["to"] = "Gamma", ["minConfidence"] = "0.95" }));
        Assert(!traceLowConfidence["found"]!.GetValue<bool>(),
            "trace minConfidence argument above the edge confidence should hide the path");

        // No path: found=false with a reason, never an exception.
        var traceNoPath = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject { ["from"] = "Gamma", ["to"] = "Alpha" }));
        Assert(!traceNoPath["found"]!.GetValue<bool>() && traceNoPath["reason"]!.GetValue<string>().Length > 0,
            "trace with no path should report found=false with a reason");

        // name-disambiguation (delta case): ambiguous name -> candidate list, no guessing.
        var traceAmbiguous = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject { ["from"] = "Dup", ["to"] = "Gamma" }));
        Assert(!traceAmbiguous["found"]!.GetValue<bool>(), "an ambiguous from name should not trace");
        var dupCandidates = traceAmbiguous["candidates"]!.AsArray();
        Assert(dupCandidates.Count == 2, "an ambiguous name should list every same-name symbol as a candidate");
        Assert(dupCandidates.All(c => c!["id"] is not null && c["name"]!.GetValue<string>() == "Dup"
                && c["kind"] is not null && c["fileId"] is not null && c["line"] is not null),
            "candidates should carry id/name/kind/fileId/line");
        Assert(dupCandidates[0]!["id"]!.GetValue<string>() == "symbol:file:tw2:5:Dup",
            "candidates should be deterministically ordered by symbol id");

        // The candidate list is bounded at 20 entries.
        var traceMany = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject { ["from"] = "Many", ["to"] = "Gamma" }));
        Assert(!traceMany["found"]!.GetValue<bool>() && traceMany["candidates"]!.AsArray().Count == 20,
            "an ambiguous name with more than 20 matches should return a bounded 20-candidate list");

        // Zero candidates: found=false with a reason naming the input, empty candidates.
        var traceUnknown = ToolResultJson(await traceRunner.CallAsync("trace", new JsonObject { ["from"] = "NoSuchSymbol", ["to"] = "Gamma" }));
        Assert(!traceUnknown["found"]!.GetValue<bool>()
            && traceUnknown["reason"]!.GetValue<string>().Contains("NoSuchSymbol", StringComparison.Ordinal)
            && traceUnknown["candidates"]!.AsArray().Count == 0,
            "an unknown name should report found=false with a reason naming the input");

        // check defaults to import cycles; members carry id/name/file decoration.
        var checkDefault = ToolResultJson(await traceRunner.CallAsync("check", new JsonObject()));
        var importCycles = checkDefault["cycles"]!.AsArray();
        Assert(importCycles.Count == 1 && importCycles[0]!["kind"]!.GetValue<string>() == CodeEdgeKinds.Imports,
            "check should default to import cycles and find the tm1->tm2->tm3 ring");
        var importMembers = importCycles[0]!["members"]!.AsArray();
        Assert(importMembers.Count == 3 && importMembers[0]!["id"]!.GetValue<string>() == "file:tm1",
            "import cycle members should start at the smallest id");
        Assert(importMembers[0]!["file"]!.GetValue<string>() == "src/m1.cs",
            "file cycle members should resolve their ck_file path");

        // check cycles=calls on the acyclic CALLS graph: empty list, no error.
        var checkCallsEmpty = ToolResultJson(await traceRunner.CallAsync("check", new JsonObject { ["cycles"] = "calls" }));
        Assert(checkCallsEmpty["cycles"]!.AsArray().Count == 0,
            "check cycles=calls should return an empty list for an acyclic CALLS graph");

        // Closing the CALLS ring: cycles=both reports both kinds, symbol members are named.
        await traceOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Edges: [new CodeEdgeFact("symbol:file:tw:21:Gamma", "symbol:file:tw:1:Alpha", CodeEdgeKinds.Calls, "file:tw", 25, 0.9, "roslyn", "")]));
        var checkBoth = ToolResultJson(await traceRunner.CallAsync("check", new JsonObject { ["cycles"] = "both" }));
        var bothCycles = checkBoth["cycles"]!.AsArray();
        Assert(bothCycles.Count == 2, "check cycles=both should report the CALLS and the IMPORTS cycle");
        var callsCycle = bothCycles.Single(c => c!["kind"]!.GetValue<string>() == CodeEdgeKinds.Calls)!;
        Assert(callsCycle["members"]!.AsArray().Any(m => m!["name"]!.GetValue<string>() == "Alpha"
                && m["file"]!.GetValue<string>() == "src/Alpha.cs"),
            "CALLS cycle members should be decorated with ck_symbol names and file paths");
    }

    // T2.1 (deepen-context-impact track): impact_of_change layered upgrade (add-only
    // direction/maxDepth/minConfidence params; layers/risk/affectedProcesses output next to the
    // legacy edges/impactedIds fields) and symbol_context enrichment pass-through. Hard
    // compatibility bar: a call without the new args still carries the old fields unchanged.
    using (var deepToolDb = new CozoDb("mem", ""))
    {
        var deepToolOm = new CozoOm(deepToolDb);
        await deepToolOm.InitCodeKnowledgeAsync();
        // Fixture: CALLS chain C -> B -> A (0.9), weak caller W -> A (0.5), callee A -> D (0.9);
        // one process containing B and A; A belongs to a community.
        await deepToolOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Files: [new CodeFileFact("file:di", "repo:di", "src/Impact.cs")],
            Symbols:
            [
                new CodeSymbolFact("symbol:di:a", "file:di", "ImpA", "method", 1, 10, "ImpA()"),
                new CodeSymbolFact("symbol:di:b", "file:di", "ImpB", "method", 11, 20, "ImpB()"),
                new CodeSymbolFact("symbol:di:c", "file:di", "ImpC", "method", 21, 30, "ImpC()"),
                new CodeSymbolFact("symbol:di:d", "file:di", "ImpD", "method", 31, 40, "ImpD()"),
                new CodeSymbolFact("symbol:di:w", "file:di", "ImpW", "method", 41, 50, "ImpW()")
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:di:c", "symbol:di:b", CodeEdgeKinds.Calls, "file:di", 25, 0.9, "roslyn", "C calls B"),
                new CodeEdgeFact("symbol:di:b", "symbol:di:a", CodeEdgeKinds.Calls, "file:di", 15, 0.9, "roslyn", "B calls A"),
                new CodeEdgeFact("symbol:di:w", "symbol:di:a", CodeEdgeKinds.Calls, "file:di", 45, 0.5, "treesitter", "weak call"),
                new CodeEdgeFact("symbol:di:a", "symbol:di:d", CodeEdgeKinds.Calls, "file:di", 5, 0.9, "roslyn", "A calls D")
            ]));
        using (deepToolDb.Run(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
              ["process:i01", "ImpactFlow", "symbol:di:b", "public_api", "public_api", 2]]
            :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
            """)) { }
        using (deepToolDb.Run(
            """
            ?[process_id, step, symbol_id, via_kind] <- [
              ["process:i01", 0, "symbol:di:b", ""], ["process:i01", 1, "symbol:di:a", "CALLS"]]
            :put ck_process_step {process_id, step => symbol_id, via_kind}
            """)) { }
        using (deepToolDb.Run(
            """
            ?[community_id, label, cohesion, symbol_count, algo] <- [["community:i01", "Demo.Impact", 1.0, 1, "louvain"]]
            :put ck_community {community_id => label, cohesion, symbol_count, algo}
            """)) { }
        using (deepToolDb.Run(
            """
            ?[symbol_id, community_id] <- [["symbol:di:a", "community:i01"]]
            :put ck_member {symbol_id => community_id}
            """)) { }

        var deepRunner = new LlmWikiToolRunner(deepToolOm);
        static JsonObject DeepToolJson(object result) =>
            System.Text.Json.JsonSerializer.SerializeToNode(result, LlmWikiJson.Options)!.AsObject();

        // Schema: add-only params, with the direction semantics spelled out (up/down + call).
        var impactSchema = LlmWikiToolRunner.ToolsJson()
            .First(tool => tool?["name"]?.GetValue<string>() == "impact_of_change")!["inputSchema"]!["properties"]!.AsObject();
        Assert(impactSchema.ContainsKey("direction") && impactSchema.ContainsKey("maxDepth") && impactSchema.ContainsKey("minConfidence"),
            "impact_of_change schema should expose add-only direction/maxDepth/minConfidence parameters");
        var directionDescription = impactSchema.ContainsKey("direction")
            ? impactSchema["direction"]!["description"]!.GetValue<string>() : "";
        Assert(directionDescription.Contains("up", StringComparison.Ordinal)
            && directionDescription.Contains("down", StringComparison.Ordinal)
            && directionDescription.Contains("call", StringComparison.OrdinalIgnoreCase),
            "impact_of_change direction description should state the up/down call semantics");

        // Legacy call (no new args): old fields keep the flat downward reachability semantics
        // and the result stays castable to ImpactOfChangeResult; new fields are add-only.
        var legacyImpact = (ImpactOfChangeResult)await deepRunner.CallAsync("impact_of_change", new JsonObject { ["symbolId"] = "symbol:di:a" });
        Assert(legacyImpact.RootId == "symbol:di:a"
            && legacyImpact.ImpactedIds.SequenceEqual(["symbol:di:d"])
            && legacyImpact.Edges.Single().Kind == CodeEdgeKinds.Calls,
            "a call without new args should keep the legacy flat edges/impactedIds semantics");
        var legacyJson = DeepToolJson(legacyImpact);
        Assert(new[] { "rootId", "edges", "impactedIds", "direction", "layers", "risk", "affectedProcesses" }.All(legacyJson.ContainsKey),
            "impact_of_change output should carry the legacy fields plus add-only layered fields");
        Assert(legacyJson["direction"]!.GetValue<string>() == "up", "impact_of_change direction should default to up");
        var defaultLayers = legacyJson["layers"]!.AsArray();
        Assert(defaultLayers.Count == 2, "default up impact of A should produce two layers");
        Assert(defaultLayers[0]!["symbols"]!.AsArray().Select(s => s!["symbolId"]!.GetValue<string>()).SequenceEqual(["symbol:di:b", "symbol:di:w"])
            && defaultLayers[0]!["total"]!.GetValue<int>() == 2
            && defaultLayers[0]!["truncated"]!.GetValue<int>() == 0,
            "layer 1 should carry symbols/total/truncated for the direct callers");
        Assert(defaultLayers[1]!["symbols"]!.AsArray().Single()!["symbolId"]!.GetValue<string>() == "symbol:di:c",
            "layer 2 should carry the transitive caller");
        Assert(legacyJson["risk"]!.GetValue<string>() == "LOW", "two direct callers should rate LOW");
        Assert(legacyJson["affectedProcesses"]!.AsArray().Select(p => p!["processId"]!.GetValue<string>()).SequenceEqual(["process:i01"]),
            "affected processes should surface flows containing the root or layer members");

        // direction two-state: down walks CALLS forward to the callees; legacy fields stay put.
        var downImpact = DeepToolJson(await deepRunner.CallAsync("impact_of_change", new JsonObject
        {
            ["symbolId"] = "symbol:di:a",
            ["direction"] = "down"
        }));
        Assert(downImpact["direction"]!.GetValue<string>() == "down"
            && downImpact["layers"]!.AsArray().Count == 1
            && downImpact["layers"]![0]!["symbols"]!.AsArray().Single()!["symbolId"]!.GetValue<string>() == "symbol:di:d",
            "direction=down should walk CALLS edges forward to the callees");
        Assert(downImpact["impactedIds"]!.AsArray().Single()!.GetValue<string>() == "symbol:di:d",
            "legacy fields should stay present when new args are supplied");

        // maxDepth bounds the layer count; minConfidence excludes weak edges from the layers.
        var shallowImpact = DeepToolJson(await deepRunner.CallAsync("impact_of_change", new JsonObject
        {
            ["symbolId"] = "symbol:di:a",
            ["maxDepth"] = "1"
        }));
        Assert(shallowImpact["layers"]!.AsArray().Count == 1, "maxDepth=1 should cut the second layer");
        var confidentImpact = DeepToolJson(await deepRunner.CallAsync("impact_of_change", new JsonObject
        {
            ["symbolId"] = "symbol:di:a",
            ["minConfidence"] = "0.7"
        }));
        Assert(confidentImpact["layers"]![0]!["symbols"]!.AsArray().Select(s => s!["symbolId"]!.GetValue<string>()).SequenceEqual(["symbol:di:b"]),
            "minConfidence=0.7 should exclude the 0.5-confidence caller from layer 1");

        // symbol_context: enrichment fields pass through the tool add-only, legacy fields intact.
        var contextJson = DeepToolJson(await deepRunner.CallAsync("symbol_context", new JsonObject { ["symbolId"] = "symbol:di:a" }));
        Assert(new[] { "symbol", "incoming", "outgoing", "docs", "processes", "communityId", "communityLabel", "incomingByKind", "outgoingByKind" }
            .All(contextJson.ContainsKey),
            "symbol_context output should carry the legacy fields plus add-only enrichment fields");
        Assert(contextJson["processes"]!.AsArray().Single()!["processId"]!.GetValue<string>() == "process:i01",
            "symbol_context should surface the execution flows the symbol participates in");
        Assert(contextJson["communityId"]!.GetValue<string>() == "community:i01"
            && contextJson["communityLabel"]!.GetValue<string>() == "Demo.Impact",
            "symbol_context should surface community membership");
        Assert(contextJson["incomingByKind"]!["CALLS"]!.GetValue<int>() == 2
            && contextJson["outgoingByKind"]!["CALLS"]!.GetValue<int>() == 1,
            "symbol_context should surface per-kind edge counts");

        // Empty enrichment sources stay empty at the tool layer without throwing.
        var plainContextJson = DeepToolJson(await deepRunner.CallAsync("symbol_context", new JsonObject { ["symbolId"] = "symbol:di:d" }));
        Assert(plainContextJson["processes"]!.AsArray().Count == 0
            && plainContextJson["communityId"]!.GetValue<string>() == ""
            && plainContextJson["communityLabel"]!.GetValue<string>() == "",
            "symbols outside processes/communities should keep empty enrichment fields");
    }

    // T1.1: tree-sitter native backend — load, ABI assertion, backend selection and degradation.
    // Delta cases: requirements/native-backend {native-preferred, fallback-regex, abi-assert}.
    var csSample = """
    namespace Demo;

    public class SampleService
    {
        public string GetValue()
        {
            return "ok";
        }
    }
    """;

    // abi-assert (unit level): out-of-range ABI must produce an error diagnostic containing version numbers.
    var badAbi = TreeSitterNativeBackend.ValidateAbiVersion("csharp", 99);
    Assert(badAbi is { Severity: "error" }, "out-of-range grammar ABI should produce an error diagnostic");
    Assert(badAbi!.Message.Contains("99", StringComparison.Ordinal)
        && badAbi.Message.Contains(TreeSitterNative.MaxSupportedLanguageAbi.ToString(), StringComparison.Ordinal)
        && badAbi.Message.Contains(TreeSitterNative.MinCompatibleLanguageAbi.ToString(), StringComparison.Ordinal),
        "ABI diagnostic should include the offending version and the supported range");
    Assert(TreeSitterNativeBackend.ValidateAbiVersion("csharp", TreeSitterNative.MaxSupportedLanguageAbi) is null,
        "in-range grammar ABI should pass the assertion");

    // fallback path: probing a directory without dylibs must degrade to cli/none, never throw.
    var degradedSelector = ParserBackendSelector.CreateDefault(nativeProbeDirectories: ["/nonexistent-treesitter-native-libs"]);
    Assert(!degradedSelector.NativeAvailable, "selector probed at an empty directory should report native unavailable");
    Assert(degradedSelector.Diagnostics.Count > 0, "degraded selector should carry diagnostics explaining the missing native libraries");
    var degradedBackend = degradedSelector.SelectFor("csharp");
    Assert(degradedBackend is null || degradedBackend.Name == "cli", "without native libs the selector should degrade to cli or report no backend");
    var degradedStatus = degradedSelector.DescribeStatus();
    Assert(!degradedStatus.NativeAvailable, "parser status should carry native availability field (add-only) reporting unavailable");

    if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
    {
        // native-preferred: on osx-arm64 the bundled dylibs must load and pass the ABI assertion.
        var nativeSelector = ParserBackendSelector.CreateDefault();
        Assert(nativeSelector.NativeAvailable, "native tree-sitter backend should load bundled dylibs on osx-arm64: "
            + string.Join("; ", nativeSelector.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var nativeStatus = nativeSelector.DescribeStatus();
        Assert(nativeStatus.NativeAvailable && nativeStatus.NativeDetail.Contains("abi", StringComparison.OrdinalIgnoreCase),
            "parser status native detail should report grammar ABI versions");

        var csBackend = nativeSelector.SelectFor("csharp");
        Assert(csBackend is not null && csBackend.Name == "native", "selector should prefer the native backend for csharp");
        Assert(csBackend!.SupportsLanguage("typescript") && !csBackend.SupportsLanguage("markdown"),
            "native backend should support typescript and reject languages without a grammar");

        var csParsed = csBackend.Parse("Sample.cs", csSample, "csharp");
        Assert(csParsed.Success && !csParsed.HasErrors, "native backend should parse valid C# without syntax errors: "
            + string.Join("; ", csParsed.Diagnostics.Select(d => d.Message)));
        Assert(csParsed.BackendName == "native" && csParsed.LanguageId == "csharp", "parsed result should carry backend and language ids");

        var tsParsed = nativeSelector.SelectFor("typescript")!.Parse("sample.ts", "class Greeter { greet(name: string): void { console.log(name); } }", "typescript");
        Assert(tsParsed.Success && !tsParsed.HasErrors, "native backend should parse valid TypeScript without syntax errors");

        var brokenParsed = csBackend.Parse("Broken.cs", "class {{{{", "csharp");
        Assert(brokenParsed.Success && brokenParsed.HasErrors, "native backend should flag syntax errors without failing the parse");

        // query infrastructure must be in place for P2: compile a query and read captures.
        var query = ((TreeSitterNativeBackend)csBackend).TryCompileQuery("csharp", "(class_declaration name: (identifier) @name)", out var queryDiagnostic);
        Assert(query is not null, $"query compilation should succeed: {queryDiagnostic?.Message}");
        using (query)
        {
            var captures = query!.Execute(csSample);
            Assert(captures.Count == 1 && captures[0].Text == "SampleService" && captures[0].CaptureName == "name",
                "query execution should capture the class identifier");
            Assert(captures[0].StartLine == 3, "query captures should carry 1-based line numbers");
        }

        var badQuery = ((TreeSitterNativeBackend)csBackend).TryCompileQuery("csharp", "(nonexistent_node) @x", out var badQueryDiagnostic);
        Assert(badQuery is null && badQueryDiagnostic is not null, "invalid query should return a diagnostic instead of throwing");

        // T2.1: C# extractor — fixed-sample symbol tree and structural edges.
        // Delta cases: requirements/symbol-extraction {csharp-symbols, csharp-structural-edges}.
        var extractorSample = """
        using System;
        using System.Collections.Generic;

        namespace Demo.Services;

        public interface IRepository
        {
            string Fetch(int id);
        }

        public abstract class RepositoryBase
        {
        }

        public class UserRepository : RepositoryBase, IRepository, IDisposable
        {
            private readonly List<string> _cache;

            public int Count { get; }

            public UserRepository(List<string> cache)
            {
                _cache = cache;
            }

            public string Fetch(int id) => _cache[id];

            public T Map<T>(string value, Func<string, T> mapper) => mapper(value);

            public void Dispose() { }

            public class Nested
            {
                internal void Touch() { }
            }
        }

        public record UserDto(string Name);

        public record AdminDto(string Name) : UserDto(Name);

        public enum UserKind { Admin, Guest }

        public struct Money
        {
            public decimal Amount;
        }

        public delegate void UserChanged(string name);
        """;

        var extracted = csBackend.Parse("Repo.cs", extractorSample, "csharp");
        Assert(extracted.Success && !extracted.HasErrors, "C# extractor sample should parse cleanly: "
            + string.Join("; ", extracted.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        // csharp-symbols: file-scoped namespace is the single root; nesting via parent/children.
        Assert(extracted.Symbols.Count == 1, $"file-scoped namespace should be the single root symbol (got {extracted.Symbols.Count})");
        var ns = extracted.Symbols[0];
        Assert(ns.Kind == "namespace" && ns.Name == "Demo.Services", "root symbol should be the file-scoped namespace");
        Assert(ns.SymKey == "csharp:Demo.Services#0", $"namespace sym_key should be csharp:Demo.Services#0 (got {ns.SymKey})");
        Assert(ns.Children.Count == 8, $"namespace should contain 8 top-level types (got {ns.Children.Count}: "
            + string.Join(", ", ns.Children.Select(c => c.Name)) + ")");

        var iface = ns.Children.Single(c => c.Name == "IRepository");
        Assert(iface.Kind == "interface" && iface.Visibility == "public" && iface.Exported, "interface should be public/exported");
        var ifaceFetch = iface.Children.Single(c => c.Name == "Fetch");
        Assert(ifaceFetch.Kind == "method" && ifaceFetch.SymKey == "csharp:Demo.Services.IRepository.Fetch#1",
            $"interface method sym_key should carry arity 1 (got {ifaceFetch.SymKey})");

        var repoBase = ns.Children.Single(c => c.Name == "RepositoryBase");
        Assert(repoBase.Kind == "class" && repoBase.Visibility == "public", "abstract base class visibility should read access modifiers only");

        var userRepo = ns.Children.Single(c => c.Name == "UserRepository");
        Assert(userRepo.Kind == "class" && userRepo.Exported, "UserRepository should be an exported class");
        Assert(userRepo.Signature.StartsWith("public class UserRepository : RepositoryBase", StringComparison.Ordinal),
            $"signature should be the truncated declaration line (got '{userRepo.Signature}')");
        var cacheField = userRepo.Children.Single(c => c.Name == "_cache");
        Assert(cacheField.Kind == "field" && cacheField.Visibility == "private" && !cacheField.Exported,
            "field should carry private visibility and not be exported");
        Assert(cacheField.SymKey == "csharp:Demo.Services.UserRepository._cache#0", $"field sym_key should carry arity 0 (got {cacheField.SymKey})");
        var countProp = userRepo.Children.Single(c => c.Name == "Count");
        Assert(countProp.Kind == "property" && countProp.Visibility == "public" && countProp.Exported, "property should be public/exported");
        var ctor = userRepo.Children.Single(c => c.Kind == "constructor");
        Assert(ctor.Name == "UserRepository" && ctor.SymKey == "csharp:Demo.Services.UserRepository.UserRepository#1",
            $"constructor sym_key should carry parameter arity (got {ctor.SymKey})");
        var mapMethod = userRepo.Children.Single(c => c.Name == "Map");
        Assert(mapMethod.Kind == "method" && mapMethod.SymKey == "csharp:Demo.Services.UserRepository.Map#2",
            $"generic method sym_key should carry arity 2 (got {mapMethod.SymKey})");
        var nestedClass = userRepo.Children.Single(c => c.Name == "Nested");
        Assert(nestedClass.Kind == "class", "nested class should be a child of its declaring class");
        var touch = nestedClass.Children.Single(c => c.Name == "Touch");
        Assert(touch.Kind == "method" && touch.Visibility == "internal"
            && touch.SymKey == "csharp:Demo.Services.UserRepository.Nested.Touch#0",
            $"nested-class method should carry full nesting in its sym_key (got {touch.SymKey})");

        Assert(ns.Children.Single(c => c.Name == "UserDto").Kind == "record", "record declaration should map to kind record");
        Assert(ns.Children.Single(c => c.Name == "UserKind").Kind == "enum", "enum declaration should map to kind enum");
        var money = ns.Children.Single(c => c.Name == "Money");
        Assert(money.Kind == "struct" && money.Children.Single(c => c.Name == "Amount").Kind == "field",
            "struct with public field should be extracted");
        Assert(ns.Children.Single(c => c.Name == "UserChanged").Kind == "delegate", "delegate declaration should map to kind delegate");

        var allSymbols = FlattenSymbols(extracted.Symbols).ToArray();
        Assert(allSymbols.All(s => s.SymKey.StartsWith("csharp:", StringComparison.Ordinal) && s.SymKey.Contains('#')),
            "every extracted symbol should carry a csharp sym_key with arity");
        Assert(allSymbols.All(s => s.StartLine >= 1 && s.EndLine >= s.StartLine), "symbols should carry 1-based line ranges");

        // csharp-structural-edges: CONTAINS/HAS_METHOD/HAS_PROPERTY at 0.9, EXTENDS/IMPLEMENTS heuristic at 0.7.
        var csEdges = extracted.Edges;
        Assert(HasEdge(csEdges, "Repo.cs", "Demo.Services", "CONTAINS", 0.9, "syntax"), "file should CONTAINS the top-level namespace");
        Assert(HasEdge(csEdges, "Demo.Services", "Demo.Services.UserRepository", "CONTAINS", 0.9, "syntax"), "namespace should CONTAINS its types");
        Assert(HasEdge(csEdges, "Demo.Services.UserRepository", "Demo.Services.UserRepository.Nested", "CONTAINS", 0.9, "syntax"),
            "declaring class should CONTAINS its nested class");
        Assert(HasEdge(csEdges, "Demo.Services.UserRepository", "Demo.Services.UserRepository.Fetch", "HAS_METHOD", 0.9, "syntax"),
            "class should HAS_METHOD its methods");
        Assert(HasEdge(csEdges, "Demo.Services.UserRepository", "Demo.Services.UserRepository.UserRepository", "HAS_METHOD", 0.9, "syntax"),
            "class should HAS_METHOD its constructor");
        Assert(HasEdge(csEdges, "Demo.Services.IRepository", "Demo.Services.IRepository.Fetch", "HAS_METHOD", 0.9, "syntax"),
            "interface should HAS_METHOD its members");
        Assert(HasEdge(csEdges, "Demo.Services.UserRepository", "Demo.Services.UserRepository.Count", "HAS_PROPERTY", 0.9, "syntax"),
            "class should HAS_PROPERTY its properties");
        Assert(HasEdge(csEdges, "Demo.Services.UserRepository", "RepositoryBase", "EXTENDS", 0.7, "heuristic"),
            "non-I-prefixed base should map to EXTENDS at heuristic confidence 0.7");
        Assert(HasEdge(csEdges, "Demo.Services.UserRepository", "IRepository", "IMPLEMENTS", 0.7, "heuristic"),
            "I-prefixed base should map to IMPLEMENTS at heuristic confidence 0.7");
        Assert(HasEdge(csEdges, "Demo.Services.UserRepository", "IDisposable", "IMPLEMENTS", 0.7, "heuristic"),
            "known BCL interface name should also pass the I-prefix heuristic");
        Assert(HasEdge(csEdges, "Demo.Services.AdminDto", "UserDto", "EXTENDS", 0.7, "heuristic"),
            "record primary-constructor base should EXTENDS with argument list stripped");
        Assert(HasEdge(csEdges, "Repo.cs", "System", "IMPORTS", 0.9, "syntax"), "using directive should produce a file-level IMPORTS edge");
        Assert(HasEdge(csEdges, "Repo.cs", "System.Collections.Generic", "IMPORTS", 0.9, "syntax"), "qualified using should keep the full namespace name");
        Assert(csEdges.All(edge => edge.Kind is "CONTAINS" or "HAS_METHOD" or "HAS_PROPERTY" or "EXTENDS" or "IMPLEMENTS" or "IMPORTS"),
            "extractor should only emit v2 structural edge kinds");

        // Block-style + nested namespaces and generic base stripping.
        var nestedNsSample = """
        namespace Outer
        {
            namespace Inner
            {
                public class Leaf : System.Collections.Generic.List<string>
                {
                    public void Run() { }
                }
            }
        }
        """;
        var nestedParsed = csBackend.Parse("Nested.cs", nestedNsSample, "csharp");
        Assert(nestedParsed.Success && !nestedParsed.HasErrors, "nested namespace sample should parse cleanly");
        var leaf = FlattenSymbols(nestedParsed.Symbols).Single(s => s.Name == "Leaf");
        Assert(leaf.SymKey == "csharp:Outer.Inner.Leaf#0", $"block namespaces should qualify sym_key (got {leaf.SymKey})");
        Assert(FlattenSymbols(nestedParsed.Symbols).Single(s => s.Name == "Run").SymKey == "csharp:Outer.Inner.Leaf.Run#0",
            "method inside nested namespaces should carry the full qualifier");
        Assert(HasEdge(nestedParsed.Edges, "Outer", "Outer.Inner", "CONTAINS", 0.9, "syntax"), "outer namespace should CONTAINS inner namespace");
        Assert(HasEdge(nestedParsed.Edges, "Outer.Inner.Leaf", "System.Collections.Generic.List", "EXTENDS", 0.7, "heuristic"),
            "generic base type should be stripped of type arguments and keep its qualifier");

        // T2.2: TypeScript extractor — fixed-sample symbol tree, IMPORTS and EXTENDS/IMPLEMENTS edges.
        // Delta case: requirements/symbol-extraction {typescript-symbols}.
        var tsSample = """
        import fs from "fs";
        import { join, resolve } from "path";
        import * as os from "os";
        import type { Config } from "./config";

        export interface IGreeter {
            name: string;
            greet(message: string): void;
        }

        export interface INamedGreeter extends IGreeter {
            title: string;
        }

        export type GreeterFactory = (name: string) => IGreeter;

        export enum GreeterKind { Friendly, Formal }

        export abstract class GreeterBase {
            protected prefix: string = ">";

            abstract describe(): string;
        }

        export class ConsoleGreeter extends GreeterBase implements IGreeter {
            name: string;
            private count = 0;

            constructor(name: string) {
                super();
                this.name = name;
            }

            greet(message: string): void {
                console.log(join(this.prefix, message));
            }

            describe(): string {
                return "console";
            }
        }

        export default function createGreeter(name: string): IGreeter {
            return new ConsoleGreeter(name);
        }

        function helper(a: number, b: number): number {
            return a + b;
        }
        """;

        var tsBackend = nativeSelector.SelectFor("typescript");
        Assert(tsBackend is not null && tsBackend.Name == "native", "selector should prefer the native backend for typescript");
        var tsExtracted = tsBackend!.Parse("sample.ts", tsSample, "typescript");
        Assert(tsExtracted.Success && !tsExtracted.HasErrors, "TS extractor sample should parse cleanly: "
            + string.Join("; ", tsExtracted.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        // typescript-symbols: TS has no namespace qualifier here, so top-level declarations are roots.
        var tsRoots = tsExtracted.Symbols;
        Assert(tsRoots.Count == 8, $"sample should have 8 top-level symbols (got {tsRoots.Count}: "
            + string.Join(", ", tsRoots.Select(s => s.Name)) + ")");

        var greeterIface = tsRoots.Single(s => s.Name == "IGreeter");
        Assert(greeterIface.Kind == "interface" && greeterIface.Exported, "interface should be exported via export modifier");
        Assert(greeterIface.SymKey == "ts:IGreeter#0", $"interface sym_key should be ts:IGreeter#0 (got {greeterIface.SymKey})");
        var ifaceName = greeterIface.Children.Single(c => c.Name == "name");
        Assert(ifaceName.Kind == "property" && ifaceName.SymKey == "ts:IGreeter.name#0", "property_signature should map to kind property");
        var ifaceGreet = greeterIface.Children.Single(c => c.Name == "greet");
        Assert(ifaceGreet.Kind == "method" && ifaceGreet.SymKey == "ts:IGreeter.greet#1",
            $"interface method_signature sym_key should carry arity 1 (got {ifaceGreet.SymKey})");

        var factory = tsRoots.Single(s => s.Name == "GreeterFactory");
        Assert(factory.Kind == "type_alias" && factory.Exported && factory.SymKey == "ts:GreeterFactory#0",
            "type_alias_declaration should map to kind type_alias");
        var greeterKind = tsRoots.Single(s => s.Name == "GreeterKind");
        Assert(greeterKind.Kind == "enum" && greeterKind.Exported, "enum_declaration should map to kind enum");

        var greeterBase = tsRoots.Single(s => s.Name == "GreeterBase");
        Assert(greeterBase.Kind == "class" && greeterBase.Exported, "abstract class should map to kind class and be exported");
        var prefixField = greeterBase.Children.Single(c => c.Name == "prefix");
        Assert(prefixField.Kind == "property" && prefixField.Visibility == "protected" && !prefixField.Exported,
            "public_field_definition should carry accessibility modifier as visibility");
        var describeAbstract = greeterBase.Children.Single(c => c.Name == "describe");
        Assert(describeAbstract.Kind == "method" && describeAbstract.SymKey == "ts:GreeterBase.describe#0",
            "abstract_method_signature should map to kind method");

        var consoleGreeter = tsRoots.Single(s => s.Name == "ConsoleGreeter");
        Assert(consoleGreeter.Kind == "class" && consoleGreeter.Exported, "exported class should be exported");
        Assert(consoleGreeter.Signature.StartsWith("class ConsoleGreeter extends GreeterBase", StringComparison.Ordinal),
            $"signature should be the truncated declaration line (got '{consoleGreeter.Signature}')");
        var nameField = consoleGreeter.Children.Single(c => c.Name == "name");
        Assert(nameField.Kind == "property" && nameField.Visibility == "", "member without accessibility modifier has empty visibility");
        var countField = consoleGreeter.Children.Single(c => c.Name == "count");
        Assert(countField.Visibility == "private" && !countField.Exported, "private field should carry private visibility");
        var tsCtor = consoleGreeter.Children.Single(c => c.Name == "constructor");
        Assert(tsCtor.Kind == "method" && tsCtor.SymKey == "ts:ConsoleGreeter.constructor#1",
            $"constructor method_definition sym_key should carry arity (got {tsCtor.SymKey})");
        var greetMethod = consoleGreeter.Children.Single(c => c.Name == "greet");
        Assert(greetMethod.Kind == "method" && greetMethod.SymKey == "ts:ConsoleGreeter.greet#1" && !greetMethod.Exported,
            "class method nesting should qualify the sym_key");

        var createGreeter = tsRoots.Single(s => s.Name == "createGreeter");
        Assert(createGreeter.Kind == "function" && createGreeter.Exported && createGreeter.SymKey == "ts:createGreeter#1",
            $"export default function should be an exported function (got {createGreeter.SymKey})");
        var helperFn = tsRoots.Single(s => s.Name == "helper");
        Assert(helperFn.Kind == "function" && !helperFn.Exported && helperFn.SymKey == "ts:helper#2",
            $"non-exported function should carry arity 2 (got {helperFn.SymKey})");

        var tsAll = FlattenSymbols(tsRoots).ToArray();
        Assert(tsAll.All(s => s.SymKey.StartsWith("ts:", StringComparison.Ordinal) && s.SymKey.Contains('#')),
            "every TS symbol should carry a ts sym_key with arity");
        Assert(tsAll.All(s => s.StartLine >= 1 && s.EndLine >= s.StartLine), "TS symbols should carry 1-based line ranges");

        // Structural edges: CONTAINS/HAS_METHOD/HAS_PROPERTY at 0.9 syntax; TS extends/implements are
        // syntactically distinct clauses so EXTENDS/IMPLEMENTS also carry 0.9 evidence=syntax.
        var tsEdges = tsExtracted.Edges;
        Assert(HasEdge(tsEdges, "sample.ts", "ConsoleGreeter", "CONTAINS", 0.9, "syntax"), "file should CONTAINS top-level TS symbols");
        Assert(HasEdge(tsEdges, "ConsoleGreeter", "ConsoleGreeter.greet", "CONTAINS", 0.9, "syntax"), "class should CONTAINS its members");
        Assert(HasEdge(tsEdges, "ConsoleGreeter", "ConsoleGreeter.greet", "HAS_METHOD", 0.9, "syntax"), "class should HAS_METHOD its methods");
        Assert(HasEdge(tsEdges, "ConsoleGreeter", "ConsoleGreeter.constructor", "HAS_METHOD", 0.9, "syntax"), "class should HAS_METHOD its constructor");
        Assert(HasEdge(tsEdges, "IGreeter", "IGreeter.greet", "HAS_METHOD", 0.9, "syntax"), "interface should HAS_METHOD its method signatures");
        Assert(HasEdge(tsEdges, "ConsoleGreeter", "ConsoleGreeter.count", "HAS_PROPERTY", 0.9, "syntax"), "class should HAS_PROPERTY its fields");
        Assert(HasEdge(tsEdges, "IGreeter", "IGreeter.name", "HAS_PROPERTY", 0.9, "syntax"), "interface should HAS_PROPERTY its property signatures");
        Assert(HasEdge(tsEdges, "ConsoleGreeter", "GreeterBase", "EXTENDS", 0.9, "syntax"),
            "class extends clause should map to EXTENDS at syntactic confidence 0.9");
        Assert(HasEdge(tsEdges, "ConsoleGreeter", "IGreeter", "IMPLEMENTS", 0.9, "syntax"),
            "class implements clause should map to IMPLEMENTS at syntactic confidence 0.9");
        Assert(HasEdge(tsEdges, "INamedGreeter", "IGreeter", "EXTENDS", 0.9, "syntax"),
            "interface extends_type_clause should map to EXTENDS");
        Assert(HasEdge(tsEdges, "sample.ts", "fs", "IMPORTS", 0.9, "syntax"), "default import should produce a file-level IMPORTS edge");
        Assert(HasEdge(tsEdges, "sample.ts", "path", "IMPORTS", 0.9, "syntax"), "named import should produce a file-level IMPORTS edge");
        Assert(HasEdge(tsEdges, "sample.ts", "os", "IMPORTS", 0.9, "syntax"), "namespace import should produce a file-level IMPORTS edge");
        Assert(HasEdge(tsEdges, "sample.ts", "./config", "IMPORTS", 0.9, "syntax"), "type-only relative import should keep the module specifier");
        Assert(tsEdges.Count(edge => edge.Kind == "IMPORTS") == 4, "one IMPORTS edge per import statement (module specifier)");
        Assert(tsEdges.All(edge => edge.Kind is "CONTAINS" or "HAS_METHOD" or "HAS_PROPERTY" or "EXTENDS" or "IMPLEMENTS" or "IMPORTS"),
            "TS extractor should only emit v2 structural edge kinds");

        // javascript languageId parses through the same typescript grammar (design decision; see track findings).
        var jsExtracted = tsBackend.Parse("sample.js", """
        import { readFile } from "node:fs";

        export class Loader {
            load(path) {
                readFile(path, () => {});
            }
        }

        function main(arg) {
            return new Loader().load(arg);
        }
        """, "javascript");
        Assert(jsExtracted.Success && !jsExtracted.HasErrors, "plain JS should parse via the typescript grammar: "
            + string.Join("; ", jsExtracted.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var loader = jsExtracted.Symbols.Single(s => s.Name == "Loader");
        Assert(loader.Kind == "class" && loader.Exported && loader.Children.Single(c => c.Name == "load").SymKey == "ts:Loader.load#1",
            "JS class/method should extract through the shared TS extractor");
        Assert(jsExtracted.Symbols.Single(s => s.Name == "main").SymKey == "ts:main#1", "JS function should carry arity in sym_key");
        Assert(HasEdge(jsExtracted.Edges, "sample.js", "node:fs", "IMPORTS", 0.9, "syntax"), "JS import should produce a file-level IMPORTS edge");

        // T1.1 (call-resolution track): call-site extraction — C#/TS fixed samples asserting
        // call/new/access sites with caller qualified name, receiver text, arity, accessMode, line.
        // Delta case: requirements/call-sites {csharp-call-sites, typescript-call-sites}.
        static bool HasCallSite(
            IReadOnlyList<ParsedCallSite> sites,
            string caller, string target, string? receiver, int arity, string kind, string? accessMode = null, int? line = null) =>
            sites.Any(site => site.CallerQualified == caller && site.TargetName == target
                && site.ReceiverText == receiver && site.Arity == arity && site.Kind == kind
                && site.AccessMode == accessMode && (line is null || site.Line == line));

        var csCallSample = """
        namespace Demo.Calls;

        public class Repo
        {
            public string Load(int id) => Convert.ToString(id);
            public static Repo Create() => new Repo();
        }

        public class Service
        {
            private readonly Repo _repo = new Repo();
            public int Count { get; set; }

            public string Run(int id)
            {
                var local = Repo.Create();
                Log(local.Load(id));
                this.Count = id;
                var doubled = this.Count + id;
                var buffer = new System.Text.StringBuilder(local.Load(id));
                var items = new List<int>(id);
                return local.Load(id).Trim().ToLower();
            }

            private void Log(string message)
            {
                int Inner(int v) => Add(v, 1);
                Inner(message.Length);
            }

            private int Add(int a, int b) => a + b;
        }
        """;

        var csCallParsed = csBackend.Parse("Calls.cs", csCallSample, "csharp");
        Assert(csCallParsed.Success && !csCallParsed.HasErrors, "C# call-site sample should parse cleanly: "
            + string.Join("; ", csCallParsed.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var csSites = csCallParsed.CallSites;

        // direct member/static calls with receiver text, arity and 1-based line
        Assert(HasCallSite(csSites, "Demo.Calls.Repo.Load", "ToString", "Convert", 1, "call", null, 5),
            "static member call should carry the type receiver text, arity and line");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "Create", "Repo", 0, "call"),
            "static call through the type name should be a call site with the type as receiver");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "Log", null, 1, "call"),
            "direct identifier invocation should have a null receiver");
        Assert(csSites.Count(site => site is { Kind: "call", TargetName: "Load", ReceiverText: "local", Arity: 1, CallerQualified: "Demo.Calls.Service.Run" }) == 3,
            "every occurrence of local.Load(id) should be captured as its own call site");

        // new: generic-stripped target, qualified type kept, arity from arguments
        Assert(HasCallSite(csSites, "Demo.Calls.Repo.Create", "Repo", null, 0, "new"),
            "object creation in an expression body should be a new site");
        Assert(HasCallSite(csSites, "Demo.Calls.Service._repo", "Repo", null, 0, "new"),
            "field initializer new should attribute to the field's qualified name");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "System.Text.StringBuilder", null, 1, "new"),
            "qualified new target should keep its qualifier");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "List", null, 1, "new"),
            "generic new target should be stripped of type arguments");

        // access: assignment lhs = write, plain member access = read; call functions are not accesses
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "Count", "this", 0, "access", "write"),
            "assignment left-hand member access should be a write access");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "Count", "this", 0, "access", "read"),
            "member access in an expression should be a read access");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Log", "Length", "message", 0, "access", "read"),
            "property read inside an argument should be a read access");
        Assert(csSites.Count(site => site.Kind == "access") == 3,
            $"call-function member accesses must not double-count as accesses (got {csSites.Count(s => s.Kind == "access")}: "
            + string.Join(", ", csSites.Where(s => s.Kind == "access").Select(s => $"{s.ReceiverText}.{s.TargetName}")) + ")");

        // chained calls: receiver text is the raw source of the receiver expression
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "Trim", "local.Load(id)", 0, "call"),
            "chained call receiver should be the raw receiver expression text");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Run", "ToLower", "local.Load(id).Trim()", 0, "call"),
            "second link of a chained call should keep the full receiver text");

        // nested caller attribution: local function body attributes to the local function
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Log.Inner", "Add", null, 2, "call"),
            "call inside a local function should attribute to the local function's qualified name");
        Assert(HasCallSite(csSites, "Demo.Calls.Service.Log", "Inner", null, 1, "call"),
            "call to the local function should attribute to the enclosing method");

        Assert(csSites.Count(site => site.Kind == "call") == 10 && csSites.Count(site => site.Kind == "new") == 4,
            $"C# sample should yield exactly 10 calls and 4 news (got {csSites.Count(s => s.Kind == "call")} calls, {csSites.Count(s => s.Kind == "new")} news)");
        Assert(csSites.All(site => site.Line >= 1), "call sites should carry 1-based lines");

        var tsCallSample = """
        function topHelper(n: number): number {
            return n * 2;
        }

        class Counter {
            value = 0;

            bump(by: number): void {
                this.value = this.value + by;
                console.log(topHelper(by));
            }

            reset(): void {
                const zero = () => this.clamp(0);
                this.value = zero();
            }

            clamp(v: number): number {
                return Math.max(v, 0);
            }
        }

        const counter = new Counter();
        counter.bump(topHelper(1));
        const chained = counter.toString().trim().length;
        """;

        var tsCallParsed = tsBackend.Parse("calls.ts", tsCallSample, "typescript");
        Assert(tsCallParsed.Success && !tsCallParsed.HasErrors, "TS call-site sample should parse cleanly: "
            + string.Join("; ", tsCallParsed.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var tsSites = tsCallParsed.CallSites;

        Assert(HasCallSite(tsSites, "Counter.bump", "log", "console", 1, "call", null, 10),
            "TS member call should carry receiver text, arity and line");
        Assert(HasCallSite(tsSites, "Counter.bump", "topHelper", null, 1, "call"),
            "TS direct identifier call should have a null receiver");
        Assert(HasCallSite(tsSites, "Counter.clamp", "max", "Math", 2, "call"),
            "TS static-style call should carry the object as receiver and argument arity");

        // access read/write and de-duplication against call functions
        Assert(HasCallSite(tsSites, "Counter.bump", "value", "this", 0, "access", "write"),
            "TS assignment lhs member access should be a write");
        Assert(HasCallSite(tsSites, "Counter.bump", "value", "this", 0, "access", "read"),
            "TS member access on the rhs should be a read");
        Assert(HasCallSite(tsSites, "Counter.reset", "value", "this", 0, "access", "write"),
            "TS write in another method should attribute to that method");

        // nested caller: arrow function assigned to a block-scoped const attributes to that binding
        Assert(HasCallSite(tsSites, "Counter.reset.zero", "clamp", "this", 1, "call"),
            "call inside an arrow function bound to a const should attribute to the binding's qualified name");
        Assert(HasCallSite(tsSites, "Counter.reset", "zero", null, 0, "call"),
            "invoking the arrow binding should attribute to the enclosing method");

        // top-level code: initializer news attribute to the constant, statements to <file>
        Assert(HasCallSite(tsSites, "counter", "Counter", null, 0, "new"),
            "top-level const initializer new should attribute to the constant symbol");
        Assert(HasCallSite(tsSites, "<file>", "bump", "counter", 1, "call"),
            "top-level expression statement call should attribute to <file>");
        Assert(HasCallSite(tsSites, "<file>", "topHelper", null, 1, "call"),
            "argument call inside a top-level statement should also attribute to <file>");

        // chained calls: raw receiver text; trailing property access stays a read
        Assert(HasCallSite(tsSites, "chained", "toString", "counter", 0, "call"),
            "first link of a TS chain should carry the plain receiver");
        Assert(HasCallSite(tsSites, "chained", "trim", "counter.toString()", 0, "call"),
            "second link of a TS chain should carry the raw chained receiver text");
        Assert(HasCallSite(tsSites, "chained", "length", "counter.toString().trim()", 0, "access", "read"),
            "property at the end of a chain should be a read access");

        Assert(tsSites.Count(site => site.Kind == "access") == 4,
            $"TS accesses should be exactly write+read+write+length-read (got {tsSites.Count(s => s.Kind == "access")}: "
            + string.Join(", ", tsSites.Where(s => s.Kind == "access").Select(s => $"{s.ReceiverText}.{s.TargetName}[{s.AccessMode}]")) + ")");
        Assert(tsSites.Count(site => site.Kind == "new") == 1 && tsSites.Count(site => site.Kind == "call") == 9,
            $"TS sample should yield exactly 9 calls and 1 new (got {tsSites.Count(s => s.Kind == "call")} calls, {tsSites.Count(s => s.Kind == "new")} news: "
            + string.Join(", ", tsSites.Where(s => s.Kind == "call").Select(s => s.TargetName)) + ")");

        // T2.1 (call-resolution track): registry-based resolution pipeline + semantic edges.
        // Delta cases: requirements/call-resolution {same-file-call, cross-file-import-call,
        // constructor-call, ambiguous-degrades, unresolved-no-edge} and
        // requirements/overrides-and-accesses {override-edge, accesses-budget, accesses-off}.
        static bool HasFactEdge(
            IReadOnlyList<CodeEdgeFact> edges,
            string from, string to, string kind,
            double? confidence = null, string? evidenceContains = null, int? line = null) =>
            edges.Any(e => e.FromId == from && e.ToId == to && e.Kind == kind
                && (confidence is null || Math.Abs(e.Confidence - confidence.Value) < 1e-9)
                && (evidenceContains is null || e.Evidence.Contains(evidenceContains, StringComparison.Ordinal))
                && (line is null || e.Line == line.Value));

        var callRoot = Path.Combine(root, "callrepo");
        Directory.CreateDirectory(callRoot);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "Orders.cs"), """
        namespace Fix.Orders;

        public class OrderService
        {
            public int Total { get; set; }

            public int Place(int amount)
            {
                var repo = new OrderRepo();
                Validate(amount);
                this.Total = repo.Save(amount);
                Ping(amount);
                Console.WriteLine(amount);
                return this.Total;
            }

            private void Validate(int amount)
            {
            }
        }

        public class OrderRepo
        {
            public OrderRepo()
            {
            }

            public int Save(int amount) => amount;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "PingA.cs"), """
        namespace Fix.A;

        public class Alpha
        {
            public void Ping(int n)
            {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "PingB.cs"), """
        namespace Fix.B;

        public class Beta
        {
            public void Ping(int n)
            {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "Helper.cs"), """
        namespace Fix.Util;

        public class MathHelper
        {
            public static int Twice(int n) => n * 2;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "UseHelper.cs"), """
        using Fix.Util;

        namespace Fix.Use;

        public class Consumer
        {
            public int Go(int n) => MathHelper.Twice(n);
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "Shapes.cs"), """
        namespace Fix.Shapes;

        public interface IShape
        {
            double Area();
        }

        public class BaseShape
        {
            public virtual double Area() => 0;

            public virtual string Label(int precision) => "";
        }

        public class Circle : BaseShape
        {
            public override double Area() => 3.14;

            public string Tag() => Label(2);
        }

        public class Square : IShape
        {
            public double Area() => 1.0;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "Busy.cs"), """
        namespace Fix.Busy;

        public class Gauge
        {
            public int A { get; set; }
            public int B { get; set; }
            public int C { get; set; }

            public int Sum()
            {
                this.A = 1;
                this.B = 2;
                this.C = 3;
                return this.A + this.B + this.C;
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "util.ts"), """
        export function twice(n: number): number {
            return n * 2;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(callRoot, "main.ts"), """
        import { twice } from "./util";

        export function run(n: number): number {
            return twice(n);
        }
        """);

        // EnableRoslynEnhancement=false: this fixture asserts the raw tree-sitter baseline
        // (confidences/resolver); the Roslyn merge is covered by the mergerepo fixture below.
        var callRequest = new RepositoryIndexRequest(callRoot, RepositoryId: "repo:calls", RepositoryName: "calls", UseGitIgnore: false, EnableRoslynEnhancement: false);
        var callBatch = await new RepositoryIndexer().BuildBatchAsync(callRequest);
        var callSymbols = callBatch.Batch.Symbols ?? [];
        var callEdges = callBatch.Batch.Edges ?? [];
        var callDiagnostics = callBatch.Batch.Diagnostics ?? [];
        string SymId(string symKey) => callSymbols.Single(s => s.SymKey == symKey).SymbolId;

        var placeId = SymId("csharp:Fix.Orders.OrderService.Place#1");
        var validateId = SymId("csharp:Fix.Orders.OrderService.Validate#1");
        var ordersFileId = callSymbols.Single(s => s.SymKey == "csharp:Fix.Orders.OrderService.Place#1").FileId;

        // same-file-call: unique member call resolves at 0.9 with file/line at the call site.
        Assert(HasFactEdge(callEdges, placeId, validateId, CodeEdgeKinds.Calls, 0.9, line: 10),
            "same-file unique member call should produce a CALLS edge at confidence 0.9 pointing at the call line");
        Assert(callEdges.Where(e => e.Kind == CodeEdgeKinds.Calls).All(e => e.Resolver == "treesitter"),
            "resolved CALLS edges should carry resolver=treesitter");

        // constructor-call: new OrderRepo() resolves to the constructor symbol (or type) at 0.9.
        Assert(HasFactEdge(callEdges, placeId, SymId("csharp:Fix.Orders.OrderRepo.OrderRepo#0"), CodeEdgeKinds.Calls, 0.9, line: 9),
            "new OrderRepo() should produce a CALLS edge to the in-repo constructor symbol");

        // local binding table: var repo = new OrderRepo() → repo.Save resolves at 0.9.
        Assert(HasFactEdge(callEdges, placeId, SymId("csharp:Fix.Orders.OrderRepo.Save#1"), CodeEdgeKinds.Calls, 0.9, line: 11),
            "receiver bound by a local new-assignment should resolve through the binding table at 0.9");

        // cross-file-import-call: static call through an imported type resolves cross-file at >=0.7.
        var twiceCs = callSymbols.Single(s => s.SymKey == "csharp:Fix.Util.MathHelper.Twice#1");
        Assert(HasFactEdge(callEdges, SymId("csharp:Fix.Use.Consumer.Go#1"), twiceCs.SymbolId, CodeEdgeKinds.Calls),
            "cross-file static-type call should produce a CALLS edge");
        Assert(callEdges.Single(e => e.FromId == SymId("csharp:Fix.Use.Consumer.Go#1") && e.ToId == twiceCs.SymbolId && e.Kind == CodeEdgeKinds.Calls).Confidence >= 0.7,
            "cross-file CALLS edge should carry confidence >= 0.7");
        Assert(callSymbols.Single(s => s.SymKey == "csharp:Fix.Use.Consumer.Go#1").FileId != twiceCs.FileId,
            "cross-file fixture sanity: caller and callee live in different files");

        // TS import-constrained fallback: name+arity unique within the imported file set at 0.7.
        Assert(HasFactEdge(callEdges, SymId("ts:run#1"), SymId("ts:twice#1"), CodeEdgeKinds.Calls, 0.7, evidenceContains: "import"),
            "TS relative-import call should resolve within the import-constrained file set at 0.7 with import evidence");

        // ambiguous-degrades: two same-name+arity candidates → 0.5 + ambiguous:<n> evidence.
        Assert(HasFactEdge(callEdges, placeId, SymId("csharp:Fix.A.Alpha.Ping#1"), CodeEdgeKinds.Calls, 0.5, evidenceContains: "ambiguous:2", line: 12),
            "ambiguous call should degrade to 0.5 with ambiguous:<n> evidence and pick the first candidate deterministically");

        // unresolved-no-edge: BCL Console.WriteLine gets no edge; counters + per-file diagnostic record it.
        Assert(!callEdges.Any(e => e.Kind == CodeEdgeKinds.Calls && e.FileId == ordersFileId && e.Line == 13),
            "call to a target outside the repo (BCL) should not produce a CALLS edge");
        Assert(callBatch.CallSites == 8 && callBatch.ResolvedCalls == 7 && callBatch.UnresolvedCalls == 1,
            $"summary counters should be callSites=8 resolved=7 unresolved=1 (got {callBatch.CallSites}/{callBatch.ResolvedCalls}/{callBatch.UnresolvedCalls})");
        Assert(callDiagnostics.Any(d => d.Kind == "call_unresolved" && d.TargetId == ordersFileId),
            "unresolved call sites should be recorded in a per-file diagnostic");

        // this-tier through the EXTENDS chain: Circle.Tag() → BaseShape.Label at 0.9.
        Assert(HasFactEdge(callEdges, SymId("csharp:Fix.Shapes.Circle.Tag#0"), SymId("csharp:Fix.Shapes.BaseShape.Label#1"), CodeEdgeKinds.Calls, 0.9),
            "receiver-less call to an inherited member should resolve through the EXTENDS chain at 0.9");

        // override-edge: METHOD_OVERRIDES via EXTENDS, METHOD_IMPLEMENTS via IMPLEMENTS, conf inherited from the base edge (0.7 heuristic).
        Assert(HasFactEdge(callEdges, SymId("csharp:Fix.Shapes.Circle.Area#0"), SymId("csharp:Fix.Shapes.BaseShape.Area#0"), CodeEdgeKinds.MethodOverrides, 0.7),
            "override with name+arity match should produce METHOD_OVERRIDES carrying the inheritance edge confidence");
        Assert(HasFactEdge(callEdges, SymId("csharp:Fix.Shapes.Square.Area#0"), SymId("csharp:Fix.Shapes.IShape.Area#0"), CodeEdgeKinds.MethodImplements, 0.7),
            "interface member implementation should produce METHOD_IMPLEMENTS");

        // ACCESSES default-on: this.Total write (line 11) and read (line 14) both land with read|write evidence.
        var totalId = SymId("csharp:Fix.Orders.OrderService.Total#0");
        Assert(HasFactEdge(callEdges, placeId, totalId, CodeEdgeKinds.Accesses, 0.9, evidenceContains: "write", line: 11),
            "assignment lhs member access should produce an ACCESSES edge with write evidence");
        Assert(HasFactEdge(callEdges, placeId, totalId, CodeEdgeKinds.Accesses, 0.9, evidenceContains: "read", line: 14),
            "member read should produce an ACCESSES edge with read evidence");
        var busyFileId = callSymbols.Single(s => s.SymKey == "csharp:Fix.Busy.Gauge.Sum#0").FileId;
        Assert(callEdges.Count(e => e.Kind == CodeEdgeKinds.Accesses && e.FileId == busyFileId) == 6,
            "within the default budget every resolvable member access should land as an ACCESSES edge");

        // accesses-budget: per-file cap enforced + truncation diagnostic recorded.
        var cappedBatch = await new RepositoryIndexer().BuildBatchAsync(callRequest with { RepositoryId = "repo:calls-capped", MaxAccessEdgesPerFile = 2 });
        var cappedEdges = cappedBatch.Batch.Edges ?? [];
        Assert(cappedEdges
                .Where(e => e.Kind == CodeEdgeKinds.Accesses)
                .GroupBy(e => e.FileId)
                .All(g => g.Count() <= 2),
            "per-file ACCESSES budget should cap the number of access edges per file");
        Assert((cappedBatch.Batch.Diagnostics ?? []).Any(d => d.Kind == "accesses_truncated" && d.TargetId.EndsWith("Busy.cs", StringComparison.Ordinal)),
            "exceeding the ACCESSES budget should record a truncation diagnostic for the file");
        Assert(cappedEdges.Count(e => e.Kind == CodeEdgeKinds.Calls) == callEdges.Count(e => e.Kind == CodeEdgeKinds.Calls),
            "the ACCESSES budget must not affect CALLS edges");

        // accesses-off: the global switch removes every ACCESSES edge but nothing else.
        var accessOffBatch = await new RepositoryIndexer().BuildBatchAsync(callRequest with { RepositoryId = "repo:calls-off", EmitAccessEdges = false });
        Assert(!(accessOffBatch.Batch.Edges ?? []).Any(e => e.Kind == CodeEdgeKinds.Accesses),
            "EmitAccessEdges=false should suppress every ACCESSES edge");
        Assert((accessOffBatch.Batch.Edges ?? []).Count(e => e.Kind == CodeEdgeKinds.Calls) == callEdges.Count(e => e.Kind == CodeEdgeKinds.Calls),
            "the ACCESSES switch must not affect CALLS edges");

        // summary passthrough (design §5 add-only counters on RepositoryIndexSummary).
        var callSummary = await new RepositoryIndexer().IndexAsync(om, callRequest with { RepositoryId = "repo:calls-db" });
        Assert(callSummary.CallSites == 8 && callSummary.ResolvedCalls == 7 && callSummary.UnresolvedCalls == 1,
            "IndexAsync summary should carry the call-resolution counters (add-only)");

        // T1.1 (deepen-llm-wiki-context-impact track): overload-caller-attribution — a call site
        // inside the second, longer overload must attribute its caller by line interval to that
        // overload's symbol, not to the first same-qualified match (delta case
        // overload-caller-attribution; P0 E2E #7 regression).
        var overloadRoot = Path.Combine(root, "overloadrepo");
        Directory.CreateDirectory(overloadRoot);
        await File.WriteAllTextAsync(Path.Combine(overloadRoot, "Impact.cs"), """
        namespace Fix.Ovl;

        public class Impact
        {
            public int Analyze(int depth) => depth;

            public int Analyze(int depth, string label)
            {
                var helper = new OvlHelper();
                var bonus = helper.Crunch(depth);
                return bonus + label.Length;
            }
        }

        public class OvlHelper
        {
            public OvlHelper()
            {
            }

            public int Crunch(int depth) => depth;
        }
        """);
        var overloadRequest = new RepositoryIndexRequest(overloadRoot, RepositoryId: "repo:overload", RepositoryName: "overload", UseGitIgnore: false, EnableRoslynEnhancement: false);
        var overloadBatch = await new RepositoryIndexer().BuildBatchAsync(overloadRequest);
        var overloadSymbols = overloadBatch.Batch.Symbols ?? [];
        var overloadEdges = overloadBatch.Batch.Edges ?? [];
        string OvlSymId(string symKey) => overloadSymbols.Single(s => s.SymKey == symKey).SymbolId;
        var analyzeOneId = OvlSymId("csharp:Fix.Ovl.Impact.Analyze#1");
        var analyzeTwoId = OvlSymId("csharp:Fix.Ovl.Impact.Analyze#2");
        Assert(HasFactEdge(overloadEdges, analyzeTwoId, OvlSymId("csharp:Fix.Ovl.OvlHelper.Crunch#1"), CodeEdgeKinds.Calls, 0.9, line: 10),
            "call site at line 10 lies inside the second overload's interval so the caller must be Analyze#2, not the first same-qualified overload");
        Assert(HasFactEdge(overloadEdges, analyzeTwoId, OvlSymId("csharp:Fix.Ovl.OvlHelper.OvlHelper#0"), CodeEdgeKinds.Calls, 0.9, line: 9),
            "constructor call at line 9 inside the second overload must also attribute the caller to Analyze#2");
        Assert(!overloadEdges.Any(e => e.FromId == analyzeOneId && (e.Kind == CodeEdgeKinds.Calls || e.Kind == CodeEdgeKinds.Accesses)),
            "the first (short) overload contains no call sites and must not own any CALLS/ACCESSES edge");
        Assert(overloadEdges.All(e => !e.Evidence.Contains("caller_fallback", StringComparison.Ordinal)),
            "interval-resolvable overload callers must not carry the caller_fallback marker");

        // T1.1 (refine-depa-detection-precision track): fallback candidate hygiene — cross-file
        // private/protected members leave the pool, a known call-site arity must match parseable
        // candidate arity, same-file candidates stay unrestricted; an emptied pool counts as
        // unresolved instead of degrading. Delta cases: requirements/ambiguous-fallback-precision
        // {private-excluded-cross-file, arity-checked, same-file-unrestricted}. The GetString shape
        // is the V-P2 ×9 false-positive root cause (BCL 0-arg instance call bound to another
        // file's private static 2-arg method at ambiguous/repo-unique confidence).
        var fallbackRoot = Path.Combine(root, "fallbackrepo");
        Directory.CreateDirectory(fallbackRoot);
        await File.WriteAllTextAsync(Path.Combine(fallbackRoot, "Reader.cs"), """
        namespace Fbk.Read;

        public class Reader
        {
            public string Grab(object element)
            {
                return element.GetString();
            }

            public int Route(int n)
            {
                return Dispatch(n);
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(fallbackRoot, "Secrets.cs"), """
        namespace Fbk.Data;

        public class Secrets
        {
            private static string GetString(int a, int b) => "x";
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(fallbackRoot, "DispatchOne.cs"), """
        namespace Fbk.D1;

        public class DispatchOne
        {
            public int Dispatch(int n) => n;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(fallbackRoot, "DispatchTwo.cs"), """
        namespace Fbk.D2;

        public class DispatchTwo
        {
            public int Dispatch(int a, int b) => a + b;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(fallbackRoot, "SameFile.cs"), """
        namespace Fbk.Same;

        public class Owner
        {
            private int Hidden(int n) => n;
        }

        public class Neighbor
        {
            public int Use(int n) => Hidden(n);
        }
        """);

        var fallbackRequest = new RepositoryIndexRequest(fallbackRoot, RepositoryId: "repo:fallback", RepositoryName: "fallback", UseGitIgnore: false, EnableRoslynEnhancement: false);
        var fallbackBatch = await new RepositoryIndexer().BuildBatchAsync(fallbackRequest);
        var fallbackSymbols = fallbackBatch.Batch.Symbols ?? [];
        var fallbackEdges = fallbackBatch.Batch.Edges ?? [];
        string FbkSymId(string symKey) => fallbackSymbols.Single(s => s.SymKey == symKey).SymbolId;

        // private-excluded-cross-file: GetString() (0 args) must not bind to the other-file
        // private static 2-arg method — no CALLS edge at all, the site counts as unresolved.
        Assert(!fallbackEdges.Any(e => e.Kind == CodeEdgeKinds.Calls && e.ToId == FbkSymId("csharp:Fbk.Data.Secrets.GetString#2")),
            "a cross-file private method must never be a fallback candidate (GetString V-P2 regression shape)");
        Assert(!fallbackEdges.Any(e => e.Kind == CodeEdgeKinds.Calls && e.FromId == FbkSymId("csharp:Fbk.Read.Reader.Grab#1")),
            "a call whose only in-repo candidates are illegal must stay unresolved rather than degrade to a wrong edge");

        // arity-checked: bare Dispatch(n) hits two public cross-file candidates (arity 1 / arity 2);
        // only the arity match survives and uniquifies the pool at the repo-unique tier.
        Assert(HasFactEdge(fallbackEdges, FbkSymId("csharp:Fbk.Read.Reader.Route#1"), FbkSymId("csharp:Fbk.D1.DispatchOne.Dispatch#1"), CodeEdgeKinds.Calls, 0.7, evidenceContains: "repo-unique"),
            "the arity-matching candidate should uniquify the fallback pool and resolve at 0.7 repo-unique");
        Assert(!fallbackEdges.Any(e => e.Kind == CodeEdgeKinds.Calls && e.ToId == FbkSymId("csharp:Fbk.D2.DispatchTwo.Dispatch#2")),
            "the arity-mismatched candidate must not receive the CALLS edge");

        // same-file-unrestricted: a same-file private candidate stays in the fallback pool.
        Assert(HasFactEdge(fallbackEdges, FbkSymId("csharp:Fbk.Same.Neighbor.Use#1"), FbkSymId("csharp:Fbk.Same.Owner.Hidden#1"), CodeEdgeKinds.Calls, 0.7),
            "visibility exclusion is cross-file only: a same-file private candidate must still resolve");

        // honest counters: exactly Grab→GetString unresolved, Route→Dispatch + Use→Hidden resolved.
        Assert(fallbackBatch.CallSites == 3 && fallbackBatch.ResolvedCalls == 2 && fallbackBatch.UnresolvedCalls == 1,
            $"fallback fixture counters should be callSites=3 resolved=2 unresolved=1 (got {fallbackBatch.CallSites}/{fallbackBatch.ResolvedCalls}/{fallbackBatch.UnresolvedCalls})");

        // T2.1 (roslyn-csharp-resolution track): RoslynMergeStep — Roslyn semantic edges override
        // the tree-sitter baseline per call point (key = caller+file+line), doc_id enrichment,
        // roslyn-only call points inserted, switch + summary counters.
        // Delta cases: requirements/roslyn-enhancement {override-baseline, docid-enrichment,
        // switch-off-identical, repo-external-not-edged}.
        var mergeRoot = Path.Combine(root, "mergerepo");
        Directory.CreateDirectory(mergeRoot);
        await File.WriteAllTextAsync(Path.Combine(mergeRoot, "WorkerA.cs"), """
        namespace Merge.A;

        public class Worker
        {
            public int Run(int n) => n;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(mergeRoot, "WorkerB.cs"), """
        namespace Merge.B;

        public class Worker
        {
            public Worker()
            {
            }

            public int Run(int n) => n * 2;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(mergeRoot, "Gadget.cs"), """
        namespace Merge.C;

        public class Gadget
        {
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(mergeRoot, "Service.cs"), """
        namespace Merge.Use;

        public class Service
        {
            public int Go(Merge.B.Worker helper)
            {
                Merge.B.Worker spare = new();
                var a = helper.Run(5);
                var b = spare.Run(1);
                return a + b;
            }

            public Merge.C.Gadget MakeGadget() => new Merge.C.Gadget();

            public void External()
            {
                Console.WriteLine("x");
            }
        }
        """);

        var mergeRequest = new RepositoryIndexRequest(mergeRoot, RepositoryId: "repo:merge", RepositoryName: "merge", UseGitIgnore: false);
        var mergeBatch = await new RepositoryIndexer().BuildBatchAsync(mergeRequest);
        var mergeSymbols = mergeBatch.Batch.Symbols ?? [];
        var mergeEdges = mergeBatch.Batch.Edges ?? [];
        string MergeSymId(string symKey) => mergeSymbols.Single(s => s.SymKey == symKey).SymbolId;
        var goId = MergeSymId("csharp:Merge.Use.Service.Go#1");
        var runAId = MergeSymId("csharp:Merge.A.Worker.Run#1");
        var runBId = MergeSymId("csharp:Merge.B.Worker.Run#1");
        var serviceFileId = mergeSymbols.Single(s => s.SymKey == "csharp:Merge.Use.Service#0").FileId;

        // override-baseline: both ambiguous 0.5 call points (typed parameter receiver + implicit-new
        // binding) are replaced by resolver=roslyn edges at 1.0 pointing at the correct target —
        // the tree-sitter baseline had deterministically picked the wrong namespace (Merge.A).
        Assert(HasFactEdge(mergeEdges, goId, runBId, CodeEdgeKinds.Calls, 1.0, evidenceContains: "semantic", line: 8),
            "Roslyn should override the 0.5 ambiguous baseline edge at line 8 with a 1.0 edge to the correct target");
        Assert(HasFactEdge(mergeEdges, goId, runBId, CodeEdgeKinds.Calls, 1.0, evidenceContains: "semantic", line: 9),
            "Roslyn should override the 0.5 ambiguous baseline edge at line 9 with a 1.0 edge to the correct target");
        Assert(!mergeEdges.Any(e => e.Kind == CodeEdgeKinds.Calls && e.ToId == runAId),
            "the wrongly-picked baseline ambiguous edges must be removed, not duplicated");
        Assert(mergeEdges.Count(e => e.Kind == CodeEdgeKinds.Calls && e.FromId == goId && e.Line == 8) == 1,
            "one call point should carry exactly one CALLS edge after the merge");
        Assert(mergeEdges.Where(e => e.Kind == CodeEdgeKinds.Calls && e.ToId == runBId).All(e => e.Resolver == "roslyn"),
            "merged semantic edges should carry resolver=roslyn");

        // roslyn-only call point (baseline had no edge: implicit `new()` is not a tree-sitter
        // call site) → inserted with the callee mapped through sym_key+line-overlap matching.
        var ctorBId = MergeSymId("csharp:Merge.B.Worker.Worker#0");
        Assert(HasFactEdge(mergeEdges, goId, ctorBId, CodeEdgeKinds.Calls, 1.0, evidenceContains: "semantic", line: 7),
            "a Roslyn-only call point (implicit new) should be inserted even though the baseline had no edge there");

        // callee mapping failure: `new Merge.C.Gadget()` resolves to the synthesized default ctor,
        // which has no declared Roslyn symbol → baseline edge kept + RoslynUnmappedCalls++.
        var gadgetTypeId = MergeSymId("csharp:Merge.C.Gadget#0");
        var makeGadgetId = MergeSymId("csharp:Merge.Use.Service.MakeGadget#0");
        var keptGadgetEdges = mergeEdges.Where(e => e.FromId == makeGadgetId && e.Kind == CodeEdgeKinds.Calls).ToArray();
        Assert(keptGadgetEdges.Length == 1 && keptGadgetEdges[0].ToId == gadgetTypeId
                && keptGadgetEdges[0].Resolver == "treesitter" && Math.Abs(keptGadgetEdges[0].Confidence - 0.9) < 1e-9,
            "callee mapping failure (synthesized default ctor) should keep the baseline tree-sitter edge");
        Assert(mergeBatch.RoslynUnmappedCalls == 1,
            $"unmappable Roslyn callee should be counted as unmapped (got {mergeBatch.RoslynUnmappedCalls})");

        // repo-external-not-edged: BCL Console.WriteLine gets no CALLS edge, only a counter.
        Assert(!mergeEdges.Any(e => e.Kind == CodeEdgeKinds.Calls && e.FileId == serviceFileId && e.Line == 17),
            "a BCL/external callee must not produce a CALLS edge");
        Assert(mergeBatch.RoslynExternalCalls == 1,
            $"external calls should pass through into the batch counters (got {mergeBatch.RoslynExternalCalls})");
        Assert(mergeBatch.RoslynResolvedCalls == 3 && mergeBatch.RoslynCandidateCalls == 0,
            $"merged 1.0 edges should be counted as resolved (got {mergeBatch.RoslynResolvedCalls}/{mergeBatch.RoslynCandidateCalls})");

        // docid-enrichment: sym_key + line-overlap matched symbols carry the Roslyn DocId while
        // keeping resolver=treesitter (decision 1: doc_id enrichment does not change the resolver).
        var runBSymbol = mergeSymbols.Single(s => s.SymKey == "csharp:Merge.B.Worker.Run#1");
        Assert(runBSymbol.DocId == "M:Merge.B.Worker.Run(System.Int32)" && runBSymbol.Resolver == "treesitter",
            $"matched method symbol should carry the Roslyn DocId with resolver unchanged (got '{runBSymbol.DocId}'/{runBSymbol.Resolver})");
        var serviceSymbol = mergeSymbols.Single(s => s.SymKey == "csharp:Merge.Use.Service#0");
        Assert(serviceSymbol.DocId == "T:Merge.Use.Service", $"matched type symbol should carry the Roslyn type DocId (got '{serviceSymbol.DocId}')");

        // switch-off-identical: EnableRoslynEnhancement=false yields the pure tree-sitter baseline —
        // no roslyn resolver facts, empty doc_ids, the 0.5 ambiguous edge intact, zero counters,
        // and every non-CALLS edge identical to the enhanced batch (the merge only touches CALLS).
        var mergeOffBatch = await new RepositoryIndexer().BuildBatchAsync(mergeRequest with { EnableRoslynEnhancement = false });
        var offEdges = mergeOffBatch.Batch.Edges ?? [];
        var offSymbols = mergeOffBatch.Batch.Symbols ?? [];
        Assert(offEdges.All(e => e.Resolver != "roslyn") && offSymbols.All(s => s.DocId == ""),
            "switch off should produce no roslyn resolver facts and no doc_id enrichment");
        Assert(HasFactEdge(offEdges, goId, runAId, CodeEdgeKinds.Calls, 0.5, evidenceContains: "ambiguous:2", line: 8),
            "switch off should keep the tree-sitter 0.5 ambiguous baseline edge");
        Assert(!offEdges.Any(e => e.Kind == CodeEdgeKinds.Calls && e.FileId == serviceFileId && e.Line == 7),
            "switch off should not have the roslyn-only inserted edge");
        Assert(mergeOffBatch.RoslynResolvedCalls == 0 && mergeOffBatch.RoslynCandidateCalls == 0
                && mergeOffBatch.RoslynExternalCalls == 0 && mergeOffBatch.RoslynUnmappedCalls == 0,
            "switch off should zero every roslyn counter");
        static string EdgeKey(CodeEdgeFact e) => $"{e.FromId}|{e.ToId}|{e.Kind}|{e.FileId}|{e.Line}|{e.Confidence}|{e.Resolver}|{e.Evidence}";
        Assert(new HashSet<string>(offEdges.Where(e => e.Kind != CodeEdgeKinds.Calls).Select(EdgeKey))
                .SetEquals(mergeEdges.Where(e => e.Kind != CodeEdgeKinds.Calls).Select(EdgeKey)),
            "the merge must only substitute CALLS edges — every other edge must be identical to the baseline");

        // summary passthrough (add-only counters on RepositoryIndexSummary).
        var mergeSummary = await new RepositoryIndexer().IndexAsync(om, mergeRequest with { RepositoryId = "repo:merge-db" });
        Assert(mergeSummary.RoslynResolvedCalls == 3 && mergeSummary.RoslynCandidateCalls == 0
                && mergeSummary.RoslynExternalCalls == 1 && mergeSummary.RoslynUnmappedCalls == 1,
            "IndexAsync summary should carry the roslyn merge counters (add-only)");
    }

    // T2.1 (add-llm-wiki-depa-ontology track): ck_external_call observation channel — the Roslyn
    // path aggregates out-of-repo call sites into (caller, target) summary rows with count,
    // built-in whitelist pre-classification, and first-call-site evidence (design §4.2).
    // Delta case: requirements/depa-ontology {external-call-summary}.
    {
        var externalRoot = Path.Combine(root, "externalrepo");
        Directory.CreateDirectory(externalRoot);
        await File.WriteAllTextAsync(Path.Combine(externalRoot, "CoreLogic.cs"), """
        namespace Ext.Core;

        public class CoreLogic
        {
            public void Persist(string path, string content)
            {
                System.IO.File.WriteAllText(path, content);
                System.IO.File.WriteAllText(path + ".bak", content);
                var home = System.Environment.GetEnvironmentVariable("HOME");
            }
        }
        """);

        var extRequest = new RepositoryIndexRequest(externalRoot, RepositoryId: "repo:external", RepositoryName: "external", UseGitIgnore: false);
        var extBatch = await new RepositoryIndexer().BuildBatchAsync(extRequest);
        var extCalls = extBatch.Batch.ExternalCalls ?? [];
        var persistId = (extBatch.Batch.Symbols ?? []).Single(s => s.SymKey == "csharp:Ext.Core.CoreLogic.Persist#2").SymbolId;
        var coreFileId = (extBatch.Batch.Files ?? []).Single(f => f.Path == "CoreLogic.cs").FileId;

        // external-call-summary: two WriteAllText calls collapse into one row with count=2 and
        // first_file/first_line pointing at the FIRST call site; overloads merge at method-name
        // granularity (both WriteAllText(string,string) sites share one target_key).
        var writeRow = extCalls.Single(c => c.TargetKey == "System.IO.File.WriteAllText");
        Assert(writeRow.CallerId == persistId && writeRow.Count == 2,
            $"two direct File.WriteAllText calls should aggregate into one (caller,target) row with count=2 (got count={writeRow.Count})");
        Assert(writeRow.FirstFileId == coreFileId && writeRow.FirstLine == 7,
            $"the summary row should carry the first call site as evidence (got {writeRow.FirstFileId}:{writeRow.FirstLine}, want {coreFileId}:7)");
        Assert(writeRow.Resolver == "roslyn" && writeRow.Category == "",
            "batch-level rows carry resolver=roslyn and stay unclassified until the observation-layer write");
        var envRow = extCalls.Single(c => c.TargetKey == "System.Environment.GetEnvironmentVariable");
        Assert(envRow.CallerId == persistId && envRow.Count == 1,
            "distinct external targets should keep separate summary rows");
        Assert(extBatch.RoslynExternalCalls == 3,
            $"the pass-through external counter still counts every site (got {extBatch.RoslynExternalCalls})");

        // IndexAsync persists the rows into ck_external_call with the built-in whitelist
        // pre-classification filled in (file_io / env) — queryable observation layer.
        using var extDb = new CozoDb("mem", "");
        var extOm = new CozoOm(extDb);
        var extSummary = await new RepositoryIndexer().IndexAsync(extOm, extRequest with { RepositoryId = "repo:external-db" });
        Assert(extSummary.RoslynExternalCalls == 3, "IndexAsync summary should keep the per-site external counter");
        var externalRows = await extOm.Runtime.Store.RunAsync(
            "?[caller_id, target_key, count, category, first_file_id, first_line] := *ck_external_call{ caller_id, target_key, count, category, first_file_id, first_line }");
        Assert(externalRows.Rows.Count == 2,
            $"ck_external_call should hold exactly the two aggregated rows (got {externalRows.Rows.Count})");
        var persistedWrite = externalRows.Rows.Single(r => r[1].GetString() == "System.IO.File.WriteAllText");
        Assert(persistedWrite[2].GetInt32() == 2 && persistedWrite[3].GetString() == "file_io"
                && persistedWrite[5].GetInt32() == 7,
            $"persisted WriteAllText row should carry count=2, category=file_io (built-in pre-classification) and the first call line (got count={persistedWrite[2]}, category={persistedWrite[3]}, line={persistedWrite[5]})");
        var persistedEnv = externalRows.Rows.Single(r => r[1].GetString() == "System.Environment.GetEnvironmentVariable");
        Assert(persistedEnv[3].GetString() == "env",
            $"persisted GetEnvironmentVariable row should pre-classify as env (got {persistedEnv[3]})");

        // Re-indexing puts the same keys — no duplicate rows (observation stays idempotent).
        await new RepositoryIndexer().IndexAsync(extOm, extRequest with { RepositoryId = "repo:external-db" });
        var externalRowsAgain = await extOm.Runtime.Store.RunAsync(
            "?[caller_id, target_key] := *ck_external_call{ caller_id, target_key }");
        Assert(externalRowsAgain.Rows.Count == 2, "re-indexing should upsert ck_external_call rows in place, not duplicate them");
    }

    // T2.1 (add-llm-wiki-depa-conformance-tools track): depa_conformance / fact_grade_map /
    // health_score tools plus the MISSION acceptance double sample through the REAL indexing
    // pipeline (Roslyn path — ck_external_call rows and cross-file CALLS edges are genuine).
    // Delta cases: requirements/depa-conformance {conformance-report, fact-grade-map,
    // health-score-no-merge}; mission success criterion: compliant vs violating distinguishable.
    {
        // --- compliant sample: the core only effects through the injected effect contract ---
        var compliantRoot = Path.Combine(root, "depacompliant");
        Directory.CreateDirectory(Path.Combine(compliantRoot, "src", "Core", "Contracts"));
        Directory.CreateDirectory(Path.Combine(compliantRoot, "src", "Edge"));
        await File.WriteAllTextAsync(Path.Combine(compliantRoot, "src", "Core", "Contracts", "IStore.cs"), """
        namespace Depa.Compliant.Core.Contracts;

        public interface IStore
        {
            void Save(string path, string content);
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(compliantRoot, "src", "Core", "Engine.cs"), """
        using Depa.Compliant.Core.Contracts;

        namespace Depa.Compliant.Core;

        public class Engine
        {
            private readonly IStore _store;

            public Engine(IStore store)
            {
                _store = store;
            }

            public void Run(string path, string content)
            {
                _store.Save(path, content);
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(compliantRoot, "src", "Edge", "FileStore.cs"), """
        using Depa.Compliant.Core.Contracts;

        namespace Depa.Compliant.Edge;

        public class FileStore : IStore
        {
            public void Save(string path, string content)
            {
                System.IO.File.WriteAllText(path, content);
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(compliantRoot, "depa-map.json"), """
        {
          "capsules": [
            { "name": "Core", "rootPath": "src/Core" },
            { "name": "Edge", "rootPath": "src/Edge" }
          ],
          "contractPackages": ["src/Core/Contracts"],
          "cores": ["Engine"]
        }
        """);

        using var compliantDb = new CozoDb("mem", "");
        var compliantOm = new CozoOm(compliantDb);
        var compliantRunner = new LlmWikiToolRunner(compliantOm);
        await compliantRunner.CallAsync("index_repo", new JsonObject { ["repoPath"] = compliantRoot });
        var compliantReport = (DepaConformanceReport)await compliantRunner.CallAsync(
            "depa_conformance", new JsonObject { ["workDirectory"] = compliantRoot });
        var compliantEffect = compliantReport.Dimensions.Single(d => d.Dimension == "effect");
        Assert(compliantEffect.Rules.Single(r => r.RuleId == "V-E1").Verdict == "PASS"
                && compliantEffect.Rules.All(r => r.Violations.Count == 0),
            "compliant sample: the effect dimension must have zero GAP — the core effects only through "
            + "the contract (got: " + string.Join("; ", compliantEffect.Rules.Select(r => $"{r.RuleId}:{r.Verdict}")) + ")");

        // --- violating sample: direct File IO in core + Func in config + internals crossing ---
        var violatingRoot = Path.Combine(root, "depaviolating");
        Directory.CreateDirectory(Path.Combine(violatingRoot, "src", "CapA"));
        Directory.CreateDirectory(Path.Combine(violatingRoot, "src", "CapB", "Internals"));
        await File.WriteAllTextAsync(Path.Combine(violatingRoot, "src", "CapA", "Core.cs"), """
        namespace Depa.Violating.CapA;

        public class Core
        {
            public void Persist(string path, string content)
            {
                System.IO.File.WriteAllText(path, content);
                var secret = Depa.Violating.CapB.Internals.Secret.Peek();
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(violatingRoot, "src", "CapA", "JobOptions.cs"), """
        namespace Depa.Violating.CapA;

        public class JobOptions
        {
            public System.Func<int, int>? OnDone { get; set; }

            public int Retries { get; set; }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(violatingRoot, "src", "CapB", "Internals", "Secret.cs"), """
        namespace Depa.Violating.CapB.Internals;

        public static class Secret
        {
            public static string Peek()
            {
                return "secret";
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(violatingRoot, "depa-map.json"), """
        {
          "capsules": [
            { "name": "CapA", "rootPath": "src/CapA" },
            { "name": "CapB", "rootPath": "src/CapB" }
          ],
          "cores": ["Core"],
          "runtimeParams": [ { "symbolOrPath": "opts", "role": "config", "declaredType": "JobOptions" } ]
        }
        """);

        using var violatingDb = new CozoDb("mem", "");
        var violatingOm = new CozoOm(violatingDb);
        var violatingRunner = new LlmWikiToolRunner(violatingOm);
        await violatingRunner.CallAsync("index_repo", new JsonObject { ["repoPath"] = violatingRoot });
        var violatingReport = (DepaConformanceReport)await violatingRunner.CallAsync(
            "depa_conformance", new JsonObject { ["workDirectory"] = violatingRoot });

        // conformance-report (delta case): dimension grouping with per-rule verdicts, BLOCKED
        // rows carrying their reason, violations ordered by confidence descending.
        var violatingRules = violatingReport.Dimensions.SelectMany(d => d.Rules).ToArray();
        // eight-dimensions (delta case, track expand-depa-detection-rules T1.1): 8 维分组
        // （新增 overdesign/vendor），V-F1/F2 归属修正为 layering，coverage 分母全目录口径。
        Assert(violatingReport.Dimensions.Select(d => d.Dimension)
                .SequenceEqual(["data", "effect", "processor", "layering", "fact_source", "actor", "overdesign", "vendor"]),
            "the tool must group rules by the eight dimensions (incl. overdesign/vendor) "
            + "in canonical order (got: " + string.Join(", ", violatingReport.Dimensions.Select(d => d.Dimension)) + ")");
        Assert(violatingRules.Single(r => r.RuleId == "V-D1").Verdict == "BLOCKED"
                && violatingRules.Single(r => r.RuleId == "V-D1").BlockedReason.Contains("fact", StringComparison.OrdinalIgnoreCase),
            "rules whose inputs are missing must surface BLOCKED with the reason on the rule row");
        Assert(new[] { "V-D2", "V-P1", "V-P3", "V-P4", "V-A*", "V-G2", "V-G3", "V-V*" }
                .Select(id => violatingRules.Single(r => r.RuleId == id))
                .All(r => r.Verdict == "BLOCKED"
                    && r.BlockedReason.Contains("静态观测不足", StringComparison.Ordinal)
                    && r.Violations.Count == 0),
            "the placeholder rules (rule-map.md status placeholder-BLOCKED) must surface as BLOCKED "
            + "rows with 原因=静态观测不足 — never silently dropped, never PASS");
        Assert(violatingRules.All(r => r.Violations.Zip(r.Violations.Skip(1))
                .All(pair => pair.First.Confidence >= pair.Second.Confidence)),
            "violations within each rule row must be ordered by confidence descending");

        // Mission acceptance: the three staged red lights each produce a GAP with path:line.
        var ve1 = violatingRules.Single(r => r.RuleId == "V-E1");
        Assert(ve1.Verdict == "GAP"
                && ve1.Violations.Single().Evidence.Single().Path == "src/CapA/Core.cs"
                && ve1.Violations.Single().Evidence.Single().Line == 7
                && ve1.Violations.Single().Evidence.Single().Detail.Contains("System.IO.File.WriteAllText", StringComparison.Ordinal),
            "V-E1: the direct File.WriteAllText in the core must be a GAP with its real call site "
            + $"(got {string.Join("; ", ve1.Violations.SelectMany(v => v.Evidence).Select(e => $"{e.Path}:{e.Line} {e.Detail}"))})");
        var vf1 = violatingRules.Single(r => r.RuleId == "V-F1");
        Assert(vf1.Verdict == "GAP"
                && vf1.Violations.Single().Evidence.Single().Path == "src/CapA/JobOptions.cs"
                && vf1.Violations.Single().Evidence.Single().Line == 5
                && vf1.Violations.Single().Message.Contains("OnDone", StringComparison.Ordinal),
            "V-F1: the Func field in the config type must be a GAP pointing at its declaration line "
            + $"(got {string.Join("; ", vf1.Violations.SelectMany(v => v.Evidence).Select(e => $"{e.Path}:{e.Line}"))})");
        var vl1 = violatingRules.Single(r => r.RuleId == "V-L1");
        Assert(vl1.Verdict == "GAP"
                && vl1.Violations.Single().Evidence.All(e => e.Path == "src/CapA/Core.cs" && e.Line > 0)
                && vl1.Violations.Single().Message.Contains("CapB", StringComparison.Ordinal),
            "V-L1: the cross-capsule internals reach must be a GAP with the crossing site "
            + $"(got {string.Join("; ", vl1.Violations.SelectMany(v => v.Evidence).Select(e => $"{e.Path}:{e.Line} {e.Detail}"))})");
        Assert(violatingRules.Where(r => r.Verdict == "GAP").Select(r => r.RuleId).Order(StringComparer.Ordinal)
                .SequenceEqual(["V-E1", "V-F1", "V-L1"]),
            "exactly the three staged red lights must be GAP — compliant and violating samples are distinguishable "
            + $"(got {string.Join("; ", violatingRules.Select(r => $"{r.RuleId}:{r.Verdict}"))})");

        // health-score-no-merge (delta case): per-dimension rows only, no merged overall field.
        var health = (DepaHealthScore)await violatingRunner.CallAsync(
            "health_score", new JsonObject { ["workDirectory"] = violatingRoot });
        var healthByDim = health.Dimensions.ToDictionary(d => d.Dimension, StringComparer.Ordinal);
        // Batch-1 + batch-2 detectors (track expand-depa-detection-rules T2.1/T3.1): the
        // denominator spans 24 detectors + 8 placeholders; undeclared inputs (incl. the
        // batch-2 recoveryPaths/layers keys) keep the new rules BLOCKED here.
        Assert(healthByDim["effect"] is { GapCount: 1, BlockedCount: 1, RulesCovered: 2, RulesTotal: 3, Score: 0.3333 },
            $"effect health: V-E1 GAP, V-E2 PASS, V-E3 BLOCKED (no contracts declared) (got {healthByDim["effect"]})");
        Assert(healthByDim["data"] is { GapCount: 0, BlockedCount: 3, RulesCovered: 0, RulesTotal: 3, Score: 0 },
            "a BLOCKED dimension must score 0 with zero coverage, not PASS — the V-D2 placeholder "
            + $"and the input-missing V-D3 count in the rule total (got {healthByDim["data"]})");
        Assert(healthByDim["processor"] is { GapCount: 0, BlockedCount: 5, RulesCovered: 0, RulesTotal: 5, Score: 0 },
            "processor pairs V-C1 (BLOCKED: no role-annotated parameter under any core) and the "
            + "layers-keyed V-P2 (BLOCKED: no layers declared) with the three placeholders so "
            + $"coverage is not inflated (got {healthByDim["processor"]})");
        Assert(healthByDim["layering"] is { GapCount: 2, BlockedCount: 6, RulesCovered: 4, RulesTotal: 10, Score: 0.2 },
            "layering spans the batch-1 rules: V-F1/V-L1 GAP, V-L2/V-L4 run clean, "
            + $"V-F2/V-F3/V-L3/V-L5/V-L6/V-R1 BLOCKED on missing inputs (got {healthByDim["layering"]})");
        Assert(healthByDim["fact_source"] is { GapCount: 0, BlockedCount: 4, RulesCovered: 1, RulesTotal: 5, Score: 0.2 },
            "fact_source: V-S1/V-S3 BLOCKED without a grading table, V-S2a/V-S2b BLOCKED without "
            + $"recoveryPaths, the V-S4 probe detector runs clean (got {healthByDim["fact_source"]})");
        Assert(healthByDim["actor"] is { GapCount: 0, BlockedCount: 1, RulesCovered: 1, RulesTotal: 2, Score: 0.5 },
            "the actor dimension pairs the V-A* placeholder with the V-A1 density detector "
            + $"(no threading calls here → PASS) (got {healthByDim["actor"]})");
        Assert(healthByDim["overdesign"] is { GapCount: 0, BlockedCount: 2, RulesCovered: 1, RulesTotal: 3, Score: 0.3333 }
            && healthByDim["vendor"] is { GapCount: 0, BlockedCount: 1, RulesCovered: 0, RulesTotal: 1, Score: 0 },
            "overdesign gains the V-G1 detector (no single-impl abstraction here → PASS); the "
            + $"placeholders stay BLOCKED, never PASS (got {healthByDim["overdesign"]} / {healthByDim["vendor"]})");
        Assert(healthByDim["layering"].GapCount != healthByDim["data"].GapCount,
            "dimensions carry their own independent gap counts");
        Assert(typeof(DepaHealthScore).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
                .SequenceEqual(["Dimensions", "Note"])
            && health.Note.Contains("independently", StringComparison.Ordinal),
            "health_score must expose per-dimension rows plus the no-merge note only — no combined verdict");

        // fact-grade-map (delta case): graded nodes + written-by / derived-from adjacency.
        using var gradeDb = new CozoDb("mem", "");
        var gradeOm = new CozoOm(gradeDb);
        await gradeOm.InitDepaOntologyAsync();
        await gradeOm.UpsertEntityAsync("depa:factsource:Ledger", "depa_fact_source", "Ledger");
        await gradeOm.SetPropertyAsync("depa:factsource:Ledger", "grade", 1);
        await gradeOm.SetPropertyAsync("depa:factsource:Ledger", "grade_id", "authoritative_fact");
        await gradeOm.SetPropertyAsync("depa:factsource:Ledger", "expected_owner", "depa:impl:Writer");
        await gradeOm.UpsertEntityAsync("depa:impl:Writer", "depa_impl", "Writer");
        await gradeOm.UpsertEntityAsync("depa:projection:View", "depa_projection", "View");
        await gradeOm.LinkEntitiesAsync("depa:factsource:Ledger", "fact_written_by", "depa:impl:Writer");
        await gradeOm.LinkEntitiesAsync("depa:projection:View", "projection_derived_from", "depa:factsource:Ledger");
        var gradeMap = (DepaFactGradeMap)await new LlmWikiToolRunner(gradeOm).CallAsync("fact_grade_map", new JsonObject());
        var ledgerNode = gradeMap.FactSources.Single();
        Assert(ledgerNode.EntityId == "depa:factsource:Ledger" && ledgerNode.Grade == 1
                && ledgerNode.GradeId == "authoritative_fact" && ledgerNode.ExpectedOwner == "depa:impl:Writer",
            $"fact_grade_map must expose the graded node with grade/grade_id/expected_owner (got {ledgerNode})");
        Assert(gradeMap.Edges.Any(e => e is { Relation: "fact_written_by", FromId: "depa:factsource:Ledger", ToId: "depa:impl:Writer" })
                && gradeMap.Edges.Any(e => e is { Relation: "projection_derived_from", FromId: "depa:projection:View", ToId: "depa:factsource:Ledger" }),
            "fact_grade_map must expose the fact_written_by and projection_derived_from adjacency "
            + $"(got {string.Join("; ", gradeMap.Edges.Select(e => $"{e.FromId}-{e.Relation}->{e.ToId}"))})");
    }

    // T2.1 (community-detection track): pipeline integration — ComputeCommunities switch,
    // Communities/NoiseSymbols summary passthrough, community_failed failure safety.
    // Delta case: requirements/community-detection {pipeline-switch}.
    {
        var communityRoot = Path.Combine(root, "communityrepo");
        Directory.CreateDirectory(communityRoot);
        // Two disjoint 3-method CALLS rings (dense enough for Louvain to keep each ring as one
        // community — a 2-edge star fragments) + one sub-threshold pair (noise).
        await File.WriteAllTextAsync(Path.Combine(communityRoot, "Billing.cs"), """
        namespace Community.Billing;

        public class Invoicer
        {
            public int Total(int a) => Add(a, a);
            private int Add(int a, int b) => Tax(a) + b;
            private int Tax(int a) => Total(a) / 10;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(communityRoot, "Shipping.cs"), """
        namespace Community.Shipping;

        public class Shipper
        {
            public int Ship(int w) => Pack(w) + 1;
            private int Pack(int w) => Track(w) + 1;
            private int Track(int w) => Ship(w) * 2;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(communityRoot, "Tiny.cs"), """
        namespace Community.Tiny;

        public class Pair
        {
            public int One(int n) => Two(n);
            private int Two(int n) => n;
        }
        """);

        // pipeline-switch: ComputeCommunities=false ⇒ no ck_community/ck_member rows at all.
        using (var offDb = new CozoDb("mem", ""))
        {
            var offOm = new CozoOm(offDb);
            var offSummary = await new RepositoryIndexer().IndexAsync(offOm, new RepositoryIndexRequest(
                communityRoot, RepositoryId: "repo:community-off", UseGitIgnore: false, ComputeCommunities: false));
            Assert(offSummary.Symbols > 0, "community fixture should index symbols");
            Assert((await offOm.ListCommunitiesAsync()).Count == 0,
                "ComputeCommunities=false should leave ck_community empty");
            var offMembers = await offOm.Runtime.Store.RunAsync("?[symbol_id] := *ck_member{ symbol_id }");
            Assert(offMembers.Rows.Count == 0, "ComputeCommunities=false should leave ck_member empty");
            Assert(offSummary.Communities == 0 && offSummary.NoiseSymbols == 0,
                "switch off should zero the community summary counters");
        }

        // switch on (default): detection runs at the index tail and the summary counters match
        // the persisted rows exactly.
        using (var onDb = new CozoDb("mem", ""))
        {
            var onOm = new CozoOm(onDb);
            var onSummary = await new RepositoryIndexer().IndexAsync(onOm, new RepositoryIndexRequest(
                communityRoot, RepositoryId: "repo:community-on", UseGitIgnore: false));
            var persisted = await onOm.ListCommunitiesAsync();
            Assert(onSummary.Communities == persisted.Count,
                $"Summary.Communities should match persisted ck_community rows (got {onSummary.Communities} vs {persisted.Count})");
            if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                // On the tree-sitter/Roslyn path the CALLS graph exists: the two 3-method clusters
                // become communities, the 2-method pair falls below MinCommunitySize=3 as noise.
                Assert(persisted.Count == 2,
                    $"two disjoint call clusters should produce exactly two communities (got {persisted.Count}: "
                    + string.Join(", ", persisted.Select(c => $"{c.Label}({c.SymbolCount})")) + ")");
                Assert(persisted.All(c => c.SymbolCount >= 3), "persisted communities should respect MinCommunitySize=3");
                Assert(persisted.Any(c => c.Label.StartsWith("Community.Billing", StringComparison.Ordinal))
                        && persisted.Any(c => c.Label.StartsWith("Community.Shipping", StringComparison.Ordinal)),
                    "community labels should carry the common qualified-name prefixes");
                Assert(onSummary.NoiseSymbols == 2,
                    $"the sub-threshold pair should pass through as NoiseSymbols=2 (got {onSummary.NoiseSymbols})");
                var onMembers = await onOm.Runtime.Store.RunAsync("?[symbol_id, community_id] := *ck_member{ symbol_id, community_id }");
                Assert(onMembers.Rows.Count == persisted.Sum(c => c.SymbolCount),
                    "ck_member rows should match the sum of persisted community sizes");
            }
        }

        // failure safety: an injected detector failure records a community_failed diagnostic;
        // the index result stands and the community counters report zero.
        using (var failDb = new CozoDb("mem", ""))
        {
            var failOm = new CozoOm(failDb);
            var failingIndexer = new RepositoryIndexer
            {
                CommunityDetector = (_, _) => throw new InvalidOperationException("community boom"),
            };
            var failSummary = await failingIndexer.IndexAsync(failOm, new RepositoryIndexRequest(
                communityRoot, RepositoryId: "repo:community-fail", UseGitIgnore: false));
            Assert(failSummary.Symbols > 0, "a community-detection failure must not fail the index");
            Assert(failSummary.Communities == 0 && failSummary.NoiseSymbols == 0,
                "a failed detection should zero the community summary counters");
            var failDiagRows = await failOm.Runtime.Store.RunAsync(
                """?[target_id, message, severity] := *ck_diagnostic{ target_id, kind: "community_failed", message, severity }""");
            Assert(failDiagRows.Rows.Count == 1
                    && failDiagRows.Rows[0][0].GetString() == "repo:community-fail"
                    && failDiagRows.Rows[0][1].GetString()!.Contains("community boom", StringComparison.Ordinal)
                    && failDiagRows.Rows[0][2].GetString() == "warning",
                "a failed detection should record a community_failed warning diagnostic targeting the repo");
        }
    }

    // T2.1 (process-extraction track): pipeline integration — ExtractProcesses switch,
    // EntryPoints/Processes/DroppedProcesses summary passthrough, process_failed failure safety.
    // Delta cases: requirements/process-extraction {pipeline-switch, route-entry}.
    {
        var processRoot = Path.Combine(root, "processrepo");
        Directory.CreateDirectory(processRoot);
        // MapGet registration (external target — never an edge, only a call site) marks the
        // registering method as an http_route entry; its private chain gives >= 3 BFS steps.
        await File.WriteAllTextAsync(Path.Combine(processRoot, "Api.cs"), """
        namespace Proc.Api;

        public class ApiSetup
        {
            public void ConfigureRoutes(object app)
            {
                MapGet("/orders", 1);
                LoadOrders(2);
            }

            private int LoadOrders(int n) => Validate(n) + 1;
            private int Validate(int n) => n * 2;
        }
        """);
        // *ToolRunner* container + CallAsync method = mcp_tool heuristic entry.
        await File.WriteAllTextAsync(Path.Combine(processRoot, "Tools.cs"), """
        namespace Proc.Tools;

        public class WikiToolRunner
        {
            public int CallAsync(string name) => Dispatch(name);
            private int Dispatch(string name) => Execute(name.Length);
            private int Execute(int n) => n + 1;
        }
        """);

        // pipeline-switch: ExtractProcesses=false ⇒ no ck_entry_point/ck_process/ck_process_step
        // rows at all (the batch candidates are gated on the same switch).
        using (var offDb = new CozoDb("mem", ""))
        {
            var offOm = new CozoOm(offDb);
            var offSummary = await new RepositoryIndexer().IndexAsync(offOm, new RepositoryIndexRequest(
                processRoot, RepositoryId: "repo:process-off", UseGitIgnore: false, ExtractProcesses: false));
            Assert(offSummary.Symbols > 0, "process fixture should index symbols");
            var offEntries = await offOm.Runtime.Store.RunAsync("?[symbol_id] := *ck_entry_point{ symbol_id }");
            Assert(offEntries.Rows.Count == 0, "ExtractProcesses=false should leave ck_entry_point empty");
            var offProcesses = await offOm.Runtime.Store.RunAsync("?[process_id] := *ck_process{ process_id }");
            Assert(offProcesses.Rows.Count == 0, "ExtractProcesses=false should leave ck_process empty");
            var offSteps = await offOm.Runtime.Store.RunAsync("?[process_id, step] := *ck_process_step{ process_id, step }");
            Assert(offSteps.Rows.Count == 0, "ExtractProcesses=false should leave ck_process_step empty");
            Assert(offSummary.EntryPoints == 0 && offSummary.Processes == 0 && offSummary.DroppedProcesses == 0,
                "switch off should zero the process summary counters");
        }

        // switch on (default): syntax-level candidates flow through the batch, extraction runs at
        // the index tail, and the summary counters match the persisted rows exactly.
        using (var onDb = new CozoDb("mem", ""))
        {
            var onOm = new CozoOm(onDb);
            var onSummary = await new RepositoryIndexer().IndexAsync(onOm, new RepositoryIndexRequest(
                processRoot, RepositoryId: "repo:process-on", UseGitIgnore: false));
            var persistedEntries = await onOm.Runtime.Store.RunAsync(
                "?[name, kind, metadata] := *ck_entry_point{ symbol_id, kind, metadata }, *ck_symbol{ symbol_id, name }");
            var persistedProcesses = await onOm.ListProcessesAsync();
            Assert(onSummary.EntryPoints == persistedEntries.Rows.Count,
                $"Summary.EntryPoints should match persisted ck_entry_point rows (got {onSummary.EntryPoints} vs {persistedEntries.Rows.Count})");
            Assert(onSummary.Processes == persistedProcesses.Count,
                $"Summary.Processes should match persisted ck_process rows (got {onSummary.Processes} vs {persistedProcesses.Count})");
            if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                // route-entry: the MapGet registration site's method is an http_route entry and
                // the indexing-detector metadata survives the extraction-side rebuild merge.
                var entries = persistedEntries.Rows
                    .Select(r => (Name: r[0].GetString(), Kind: r[1].GetString(), Metadata: r[2].GetString()!))
                    .ToArray();
                Assert(entries.Any(e => e is { Name: "ConfigureRoutes", Kind: "http_route" }
                        && e.Metadata.Contains("registrar=MapGet", StringComparison.Ordinal)),
                    "the MapGet registering method should be a kind=http_route entry with detector evidence");
                Assert(entries.Any(e => e is { Name: "CallAsync", Kind: "mcp_tool" }
                        && e.Metadata.Contains("ToolRunner", StringComparison.Ordinal)),
                    "a CallAsync method on a *ToolRunner* container should be a kind=mcp_tool entry");
                Assert(persistedProcesses.Any(p => p.EntryKind == "http_route" && p.StepCount >= 3),
                    "the http_route entry chain should produce a >=3 step process");
                Assert(persistedProcesses.Any(p => p.EntryKind == "mcp_tool" && p.StepCount >= 3),
                    "the mcp_tool entry chain should produce a >=3 step process");
            }
        }

        // failure safety: an injected extraction failure records a process_failed diagnostic;
        // the index result stands and the process counters report zero.
        using (var failDb = new CozoDb("mem", ""))
        {
            var failOm = new CozoOm(failDb);
            var failingIndexer = new RepositoryIndexer
            {
                ProcessExtractor = (_, _) => throw new InvalidOperationException("process boom"),
            };
            var failSummary = await failingIndexer.IndexAsync(failOm, new RepositoryIndexRequest(
                processRoot, RepositoryId: "repo:process-fail", UseGitIgnore: false));
            Assert(failSummary.Symbols > 0, "a process-extraction failure must not fail the index");
            Assert(failSummary.EntryPoints == 0 && failSummary.Processes == 0 && failSummary.DroppedProcesses == 0,
                "a failed extraction should zero the process summary counters");
            var processFailDiagRows = await failOm.Runtime.Store.RunAsync(
                """?[target_id, message, severity] := *ck_diagnostic{ target_id, kind: "process_failed", message, severity }""");
            Assert(processFailDiagRows.Rows.Count == 1
                    && processFailDiagRows.Rows[0][0].GetString() == "repo:process-fail"
                    && processFailDiagRows.Rows[0][1].GetString()!.Contains("process boom", StringComparison.Ordinal)
                    && processFailDiagRows.Rows[0][2].GetString() == "warning",
                "a failed extraction should record a process_failed warning diagnostic targeting the repo");
        }
    }

    // T2.1 failure safety: an Analyze failure records a roslyn_failed diagnostic and leaves the
    // baseline facts untouched (index never fails because of the enhancement).
    {
        var failSymbols = new List<CodeSymbolFact> { new("symbol:x", "file:x", "X", SymKey: "csharp:X#0", Resolver: "treesitter") };
        var failEdges = new List<CodeEdgeFact> { new("a", "b", CodeEdgeKinds.Calls, "file:x", 1, 0.9, "treesitter", "e") };
        var failDiagnostics = new List<CodeDiagnosticFact>();
        var failStats = RoslynMergeStep.Enhance(
            [("X.cs", "class X { }")],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["X.cs"] = "file:x" },
            failSymbols,
            failEdges,
            failDiagnostics,
            diagnosticTargetId: "repo:fail",
            analyze: _ => throw new InvalidOperationException("boom"));
        Assert(failStats == new RoslynMergeStep.RoslynMergeStats(0, 0, 0, 0),
            "a failed Roslyn analysis should return zero counters");
        var failDiag = failDiagnostics.Single();
        Assert(failDiag.Kind == "roslyn_failed" && failDiag.Severity == "warning" && failDiag.Message.Contains("boom", StringComparison.Ordinal)
                && failDiag.TargetId == "repo:fail",
            "a failed Roslyn analysis should record a roslyn_failed warning diagnostic");
        Assert(failEdges.Count == 1 && failEdges[0].Resolver == "treesitter" && failSymbols.Single().DocId == "",
            "a failed Roslyn analysis must leave the baseline facts untouched");
    }

    // T1.1 (hybrid-search track): FTS foundation — ck_search_text projection rebuilt at the
    // IndexAsync tail, ::fts ensure idempotence, literalized BM25 text search, CJK tokenization
    // (tokenizer conclusion recorded in the track findings). Delta case covered: literal-query.
    {
        var ftsRoot = Path.Combine(root, "ftsrepo");
        Directory.CreateDirectory(ftsRoot);
        await File.WriteAllTextAsync(Path.Combine(ftsRoot, "Community.cs"), """
        namespace Fts.Demo;

        public class CommunityAnalyzer
        {
            public int ComputeCommunitiesAsync(int n) => n + 1;

            public int UnrelatedHelper(int n) => n - 1;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(ftsRoot, "README.md"), """
        # 社群模块

        这里实现了计算社群检测的算法说明。
        """);

        var ftsRequest = new RepositoryIndexRequest(ftsRoot, RepositoryId: "repo:fts", UseGitIgnore: false);
        using (var ftsDb = new CozoDb("mem", ""))
        {
            var ftsOm = new CozoOm(ftsDb);
            var ftsSummary = await new RepositoryIndexer().IndexAsync(ftsOm, ftsRequest);
            Assert(ftsSummary.Symbols > 0 && ftsSummary.DocBlocks > 0, "fts fixture should index symbols and doc blocks");

            // pipeline wiring: the IndexAsync tail rebuilt ck_search_text with code + docs rows.
            var searchRows = await ftsOm.Runtime.Store.RunAsync("?[source_id, source_kind] := *ck_search_text{ source_id, source_kind }");
            Assert(searchRows.Rows.Count > 0, "IndexAsync should rebuild the ck_search_text projection");
            var searchKinds = searchRows.Rows.Select(r => r[1].GetString()).ToHashSet(StringComparer.Ordinal);
            Assert(searchKinds.Contains("code") && searchKinds.Contains("docs"),
                "ck_search_text should carry source_kind=code (symbols) and source_kind=docs (doc blocks)");

            // exact identifier hit through BM25 (text channel guarantees precise matches).
            var identHits = await CozoVectorSearchService.TextSearchAsync(ftsOm, "ComputeCommunitiesAsync", limit: 5);
            Assert(identHits.Count > 0, "exact identifier text search should return hits");
            Assert(identHits[0].SourceKind == "code" && identHits[0].Text.Contains("ComputeCommunitiesAsync", StringComparison.Ordinal),
                "the exact identifier should rank first with source_kind=code");
            Assert(identHits[0].Score > 0, "text search hits should carry a positive BM25 score");
            Assert(identHits.Zip(identHits.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score),
                "text search hits should be ordered by descending score");

            // CJK sample hit: the query is a substring of the indexed sentence — this requires a
            // CJK-segmenting tokenizer (Simple keeps a CJK run as one token and misses it).
            var cjkHits = await CozoVectorSearchService.TextSearchAsync(ftsOm, "社群检测", limit: 5);
            Assert(cjkHits.Count > 0 && cjkHits.Any(hit => hit.SourceKind == "docs" && hit.Text.Contains("社群检测", StringComparison.Ordinal)),
                "a Chinese sub-phrase query should hit the Chinese doc block");

            // literal-query delta case: FTS expression syntax must be treated as literal tokens.
            _ = await CozoVectorSearchService.TextSearchAsync(ftsOm, "foo AND (bar", limit: 5);
            _ = await CozoVectorSearchService.TextSearchAsync(ftsOm, "NEAR(\"x\" OR NOT) ^2", limit: 5);
            _ = await CozoVectorSearchService.TextSearchAsync(ftsOm, "AND", limit: 5);
            var literalHits = await CozoVectorSearchService.TextSearchAsync(ftsOm, "ComputeCommunitiesAsync AND (nothing", limit: 5);
            Assert(literalHits.Count > 0 && literalHits[0].Text.Contains("ComputeCommunitiesAsync", StringComparison.Ordinal),
                "syntax characters should be literalized while real tokens still match (OR semantics)");
            var emptyHits = await CozoVectorSearchService.TextSearchAsync(ftsOm, "   ", limit: 5);
            Assert(emptyHits.Count == 0, "a whitespace-only query should return no hits without touching FTS");

            // sourceKinds filter (accepts the same aliases as the vector channel).
            var codeOnlyHits = await CozoVectorSearchService.TextSearchAsync(ftsOm, "CommunityAnalyzer", limit: 5, sourceKinds: ["code"]);
            Assert(codeOnlyHits.Count > 0 && codeOnlyHits.All(hit => hit.SourceKind == "code"),
                "code-only text search should only return code hits");
            var docsOnlyHits = await CozoVectorSearchService.TextSearchAsync(ftsOm, "社群模块", limit: 5, sourceKinds: ["docs"]);
            Assert(docsOnlyHits.Count > 0 && docsOnlyHits.All(hit => hit.SourceKind == "docs"),
                "docs-only text search should only return docs hits");

            // idempotence: a second IndexAsync must rebuild (no duplicates) and the second
            // ::fts create must be swallowed as a create conflict.
            var ftsSummary2 = await new RepositoryIndexer().IndexAsync(ftsOm, ftsRequest);
            Assert(ftsSummary2.Symbols == ftsSummary.Symbols, "re-index should be stable");
            var searchRows2 = await ftsOm.Runtime.Store.RunAsync("?[source_id] := *ck_search_text{ source_id }");
            Assert(searchRows2.Rows.Count == searchRows.Rows.Count,
                "re-index should rebuild ck_search_text without duplicating rows");
            var identHits2 = await CozoVectorSearchService.TextSearchAsync(ftsOm, "ComputeCommunitiesAsync", limit: 5);
            Assert(identHits2.Count > 0, "text search should still hit after an idempotent re-index");
            Assert(identHits2.Select(hit => hit.SourceId).Distinct(StringComparer.Ordinal).Count() == identHits2.Count,
                "FTS results after a re-index should contain no duplicate source rows");
            var noSearchFailDiag = await ftsOm.Runtime.Store.RunAsync(
                """?[diagnostic_id] := *ck_diagnostic{ diagnostic_id, kind: "search_index_failed" }""");
            Assert(noSearchFailDiag.Rows.Count == 0, "idempotent re-index should not record search_index_failed");
        }

        // failure safety: an injected search-text failure records a search_index_failed
        // diagnostic; the index result stands.
        using (var failDb = new CozoDb("mem", ""))
        {
            var failOm = new CozoOm(failDb);
            var failingIndexer = new RepositoryIndexer
            {
                SearchTextIndexer = (_, _) => throw new InvalidOperationException("search boom"),
            };
            var failSummary = await failingIndexer.IndexAsync(failOm, ftsRequest with { RepositoryId = "repo:fts-fail" });
            Assert(failSummary.Symbols > 0, "a search-index failure must not fail the index");
            var searchFailDiagRows = await failOm.Runtime.Store.RunAsync(
                """?[target_id, message, severity] := *ck_diagnostic{ target_id, kind: "search_index_failed", message, severity }""");
            Assert(searchFailDiagRows.Rows.Count == 1
                    && searchFailDiagRows.Rows[0][0].GetString() == "repo:fts-fail"
                    && searchFailDiagRows.Rows[0][1].GetString()!.Contains("search boom", StringComparison.Ordinal)
                    && searchFailDiagRows.Rows[0][2].GetString() == "warning",
                "a failed search-text rebuild should record a search_index_failed warning diagnostic targeting the repo");
        }
    }

    // T2.1 (hybrid-search track): RRF fusion + semantic_search tool upgrade.
    // Delta cases: exact-identifier-hit, rrf-fusion, mode-selection, fts-fallback,
    // literal-query (tool layer). RRF: score = sum over channels of 1/(rrfK + rank),
    // rank 1-based per channel, channels fetch top-(limit*4, capped 50), merge by source id.
    {
        var hybridRoot = Path.Combine(root, "hybridrepo");
        Directory.CreateDirectory(hybridRoot);
        await File.WriteAllTextAsync(Path.Combine(hybridRoot, "Community.cs"), """
        namespace Hybrid.Demo;

        public class CommunityAnalyzer
        {
            public int ComputeCommunitiesAsync(int n) => n + 1;

            public int UnrelatedHelper(int n) => n - 1;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(hybridRoot, "README.md"), """
        # 社群模块

        这里实现了计算社群检测的算法说明。
        """);
        var hybridRequest = new RepositoryIndexRequest(hybridRoot, RepositoryId: "repo:hybrid", UseGitIgnore: false);

        using (var hybridDb = new CozoDb("mem", ""))
        {
            var hybridOm = new CozoOm(hybridDb);
            var hybridSummary = await new RepositoryIndexer().IndexAsync(hybridOm, hybridRequest);
            Assert(hybridSummary.Symbols > 0 && hybridSummary.DocBlocks > 0, "hybrid fixture should index symbols and doc blocks");
            var hybridService = new CozoVectorSearchService();

            // no-embedding degradation: index_embeddings has not run — the vector channel is
            // empty (no error), hybrid effectively degrades to text-only with a diagnostic.
            var noEmbed = await hybridService.HybridSearchAsync(hybridOm, "ComputeCommunitiesAsync", limit: 5);
            Assert(noEmbed.Hits.Count > 0, "hybrid search without embeddings should still return text-channel hits");
            Assert(noEmbed.Hits.All(hit => hit.Channels is ["text"]), "without embeddings every hybrid hit should come from the text channel only");
            Assert(noEmbed.Diagnostics is not null && noEmbed.Diagnostics.Any(d => d.Contains("vector_channel_empty", StringComparison.Ordinal)),
                "an empty vector channel in hybrid mode should be reported as a diagnostic, not an error");

            // symbol-only embeddings so the docs side stays vector-invisible (rrf-fusion setup).
            var embedResult = await hybridService.IndexAsync(hybridOm, new VectorIndexRequest(IncludeDocs: false, Limit: 100));
            Assert(embedResult.Indexed > 0, "hybrid fixture should embed symbol sources");

            // exact-identifier-hit: the text channel guarantees the precise name in the top 3.
            var exactHits = await hybridService.HybridSearchAsync(hybridOm, "ComputeCommunitiesAsync", limit: 5);
            Assert(exactHits.Mode == "hybrid", "hybrid search should report mode=hybrid");
            Assert(exactHits.Hits.Take(3).Any(hit => hit.Text.Contains("ComputeCommunitiesAsync", StringComparison.Ordinal)),
                "the exact identifier should appear in the top 3 hybrid results");
            var exactTop = exactHits.Hits.First(hit => hit.Text.Contains("ComputeCommunitiesAsync", StringComparison.Ordinal));
            Assert(exactTop.Channels is not null && exactTop.Channels.Contains("text") && exactTop.Channels.Contains("vector"),
                "a source hit by both channels should merge by source id and carry both channel markers");
            Assert(exactHits.Hits.All(hit => hit.RrfScore is > 0), "every hybrid hit should carry a positive RRF score");
            Assert(exactHits.Hits.Zip(exactHits.Hits.Skip(1)).All(pair => pair.First.RrfScore >= pair.Second.RrfScore),
                "hybrid hits should be ordered by descending RRF score");

            // rrf-fusion: the Chinese doc phrase only exists in the (unembedded) doc block —
            // text-only hit; the embedded symbols are vector-only hits. Both must appear.
            var fusionHits = await hybridService.HybridSearchAsync(hybridOm, "社群检测", limit: 10);
            Assert(fusionHits.Hits.Any(hit => hit.Channels is ["text"] && hit.SourceKind == "doc" && hit.Text.Contains("社群检测", StringComparison.Ordinal)),
                "rrf fusion should keep the text-only doc hit");
            Assert(fusionHits.Hits.Any(hit => hit.Channels is ["vector"] && hit.SourceKind == "symbol"),
                "rrf fusion should keep vector-only symbol hits");
            Assert(fusionHits.Hits.Zip(fusionHits.Hits.Skip(1)).All(pair => pair.First.RrfScore >= pair.Second.RrfScore),
                "fused hits should be sorted by RRF score");

            // mode-selection: text only walks FTS, vector only walks embeddings, one shape.
            var textMode = await hybridService.HybridSearchAsync(hybridOm, "ComputeCommunitiesAsync", limit: 5, mode: "text");
            Assert(textMode.Mode == "text" && textMode.Hits.Count > 0 && textMode.Hits.All(hit => hit.Channels is ["text"]),
                "text mode should only return text-channel hits");
            var vectorMode = await hybridService.HybridSearchAsync(hybridOm, "ComputeCommunitiesAsync", limit: 5, mode: "vector");
            Assert(vectorMode.Mode == "vector" && vectorMode.Hits.Count > 0 && vectorMode.Hits.All(hit => hit.Channels is ["vector"]),
                "vector mode should only return vector-channel hits");
            Assert(vectorMode.Hits.All(hit => hit.RrfScore is > 0) && textMode.Hits.All(hit => hit.RrfScore is > 0),
                "single-channel modes should return the same result shape with RRF scores");
            var invalidMode = false;
            try
            {
                _ = await hybridService.HybridSearchAsync(hybridOm, "x", mode: "fuzzy");
            }
            catch (ArgumentException)
            {
                invalidMode = true;
            }

            Assert(invalidMode, "an unknown mode should be rejected with ArgumentException");

            // sourceKinds filter flows into both channels (vector: symbol/doc, text: code/docs).
            var docsOnly = await hybridService.HybridSearchAsync(hybridOm, "社群模块", limit: 5, sourceKinds: ["docs"]);
            Assert(docsOnly.Hits.Count > 0 && docsOnly.Hits.All(hit => hit.SourceKind == "doc"),
                "docs-only hybrid search should only return doc hits");

            // literal-query (tool layer) + tool upgrade: semantic_search dispatches hybrid by
            // default, accepts mode/rrfK, and literalizes FTS syntax characters.
            var hybridRunner = new LlmWikiToolRunner(hybridOm);
            var toolDefault = (VectorSearchResult)await hybridRunner.CallAsync("semantic_search", new JsonObject { ["query"] = "ComputeCommunitiesAsync", ["limit"] = "3" });
            Assert(toolDefault.Mode == "hybrid" && toolDefault.Hits.Count > 0, "semantic_search should default to hybrid mode");
            var toolLiteral = (VectorSearchResult)await hybridRunner.CallAsync("semantic_search", new JsonObject
            {
                ["query"] = "ComputeCommunitiesAsync AND (nothing",
                ["limit"] = "5",
                ["mode"] = "text",
                ["rrfK"] = "60"
            });
            Assert(toolLiteral.Mode == "text" && toolLiteral.Hits.Count > 0
                    && toolLiteral.Hits[0].Text.Contains("ComputeCommunitiesAsync", StringComparison.Ordinal),
                "semantic_search text mode should literalize FTS syntax and still hit the real token");
        }

        // fts-fallback: ck_search_text/FTS never built (injected no-op) — hybrid must return
        // pure vector results plus a degradation diagnostic, without throwing.
        using (var fallbackDb = new CozoDb("mem", ""))
        {
            var fallbackOm = new CozoOm(fallbackDb);
            var noFtsIndexer = new RepositoryIndexer { SearchTextIndexer = (_, _) => Task.CompletedTask };
            var fallbackSummary = await noFtsIndexer.IndexAsync(fallbackOm, hybridRequest with { RepositoryId = "repo:hybrid-fallback" });
            Assert(fallbackSummary.Symbols > 0, "fallback fixture should index symbols");
            var fallbackService = new CozoVectorSearchService();
            await fallbackService.IndexAsync(fallbackOm, new VectorIndexRequest(Limit: 100));
            var fallbackHits = await fallbackService.HybridSearchAsync(fallbackOm, "ComputeCommunitiesAsync", limit: 5);
            Assert(fallbackHits.Hits.Count > 0 && fallbackHits.Hits.All(hit => hit.Channels is ["vector"]),
                "with FTS unavailable hybrid should degrade to pure vector results");
            Assert(fallbackHits.Diagnostics is not null && fallbackHits.Diagnostics.Any(d => d.Contains("fts_unavailable", StringComparison.Ordinal)),
                "the FTS degradation should be surfaced as a diagnostic");
        }
    }

    // T1.1 (detect-changes track): Git capsule (-U0 hunk parsing, three scopes, non-git safety)
    // + staleness (ck_meta.indexed_commit, stale two-state).
    {
        static (int ExitCode, string StdOut) Git(string dir, params string[] args)
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(dir);
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(info)!;
            var stdOut = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdOut);
        }

        // Pure -U0 hunk-header parsing (no repo needed): omitted d → 1 line; d = 0 → pure
        // deletion anchored at [c, c]; multi-line hunk → [c, c+d-1]; deleted file; binary skip.
        var parsedDiff = GitCliDiffProvider.ParseUnifiedDiff(
            "diff --git a/src/App.cs b/src/App.cs\n"
            + "index 1111111..2222222 100644\n"
            + "--- a/src/App.cs\n"
            + "+++ b/src/App.cs\n"
            + "@@ -3 +3 @@\n-old\n+new\n"
            + "@@ -10,2 +10,3 @@\n-x\n-y\n+x1\n+y1\n+z1\n"
            + "@@ -20 +20,0 @@\n-gone\n"
            + "diff --git a/gone.txt b/gone.txt\n"
            + "deleted file mode 100644\n"
            + "--- a/gone.txt\n"
            + "+++ /dev/null\n"
            + "@@ -1,2 +0,0 @@\n-a\n-b\n"
            + "diff --git a/logo.png b/logo.png\n"
            + "Binary files a/logo.png and b/logo.png differ\n"
            // core.quotepath=off prints non-ASCII paths raw (spaces stay unquoted too) ...
            + "diff --git a/sub dir/服务模块.cs b/sub dir/服务模块.cs\n"
            + "--- a/sub dir/服务模块.cs\n"
            + "+++ b/sub dir/服务模块.cs\n"
            + "@@ -2 +2 @@\n-旧\n+新\n"
            // ... while paths containing quotes/backslashes stay C-quoted with octal escapes.
            + "diff --git \"a/q\\\"o\\346\\234\\215.cs\" \"b/q\\\"o\\346\\234\\215.cs\"\n"
            + "--- \"a/q\\\"o\\346\\234\\215.cs\"\n"
            + "+++ \"b/q\\\"o\\346\\234\\215.cs\"\n"
            + "@@ -1 +1 @@\n-x\n+y\n");
        Assert(parsedDiff.Files.Count == 5, $"synthetic diff should yield 5 files (got {parsedDiff.Files.Count})");
        Assert(parsedDiff.Files[3] is { Path: "sub dir/服务模块.cs" } && parsedDiff.Files[3].Ranges.SequenceEqual([(2, 2)]),
            "a raw non-ASCII path with spaces should parse verbatim (got " + parsedDiff.Files[3].Path + ")");
        Assert(parsedDiff.Files[4] is { Path: "q\"o服.cs" },
            "a C-quoted path should be unquoted with octal escapes decoded as UTF-8 (got " + parsedDiff.Files[4].Path + ")");
        var appFile = parsedDiff.Files[0];
        Assert(appFile.Path == "src/App.cs" && !appFile.IsDeleted && !appFile.IsBinary,
            "the modified file should keep its b-side path without deleted/binary flags");
        Assert(appFile.Ranges.SequenceEqual([(3, 3), (10, 12), (20, 20)]),
            "hunk headers should map to new-side ranges: omitted d → [c,c], +10,3 → [10,12], d=0 deletion → [c,c] (got "
            + string.Join(",", appFile.Ranges.Select(r => $"[{r.Start},{r.End}]")) + ")");
        Assert(parsedDiff.Files[1] is { Path: "gone.txt", IsDeleted: true }, "a +++ /dev/null file should be marked deleted and keep the a-side path");
        Assert(parsedDiff.Files[2] is { Path: "logo.png", IsBinary: true, Ranges.Count: 0 }, "a binary file should be flagged and carry no ranges");
        Assert(parsedDiff.Diagnostics.Any(d => d.Contains("logo.png", StringComparison.Ordinal)), "the skipped binary file should be recorded in diagnostics");

        var provider = GitCliDiffProvider.Default;

        // non-git directory (behavior delta case non-git-safe): empty result + diagnostic, no throw.
        var plainDir = Path.Combine(root, "plaindir");
        Directory.CreateDirectory(plainDir);
        Assert(provider.TryGetHeadCommit(plainDir) is null, "TryGetHeadCommit on a non-git directory should return null");
        var nonGitDiff = provider.GetDiff(plainDir, GitDiffScope.Unstaged);
        Assert(nonGitDiff.Files.Count == 0 && nonGitDiff.Diagnostics.Count > 0,
            "GetDiff on a non-git directory should return no files plus an explanatory diagnostic");

        if (Git(Path.GetTempPath(), "--version").ExitCode != 0)
        {
            Console.WriteLine("git is unavailable — skipping git-fixture diff/staleness cases");
        }
        else
        {
            var gitRoot = Path.Combine(root, "gitrepo");
            Directory.CreateDirectory(gitRoot);
            Assert(Git(gitRoot, "init", "-q").ExitCode == 0, "git init should succeed in the fixture repo");
            Git(gitRoot, "config", "user.email", "test@example.com");
            Git(gitRoot, "config", "user.name", "LlmWiki Tests");
            Git(gitRoot, "config", "commit.gpgsign", "false");
            var calcPath = Path.Combine(gitRoot, "Calc.cs");
            await File.WriteAllTextAsync(calcPath, "line1\nline2\nline3\nline4\nline5\nline6\n");
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "Doomed.txt"), "doomed\n");
            Git(gitRoot, "add", "-A");
            Assert(Git(gitRoot, "commit", "-q", "-m", "c1").ExitCode == 0, "fixture commit c1 should succeed");
            var head1 = provider.TryGetHeadCommit(gitRoot);
            Assert(head1 is { Length: 40 }, "TryGetHeadCommit should return the 40-hex HEAD of the fixture repo");

            // unstaged scope: modify line 3 and delete line 5 → [3,3] plus deletion anchor [4,4].
            await File.WriteAllTextAsync(calcPath, "line1\nline2\nline3-modified\nline4\nline6\n");
            var unstagedDiff = provider.GetDiff(gitRoot, GitDiffScope.Unstaged);
            Assert(unstagedDiff.Files.Count == 1 && unstagedDiff.Files[0].Path == "Calc.cs",
                "unstaged diff should contain exactly the modified file");
            Assert(unstagedDiff.Files[0].Ranges.SequenceEqual([(3, 3), (4, 4)]),
                "unstaged diff should map the edit to [3,3] and the deleted line to the new-side anchor [4,4] (got "
                + string.Join(",", unstagedDiff.Files[0].Ranges.Select(r => $"[{r.Start},{r.End}]")) + ")");

            // staged vs unstaged separation: stage the edit, then make a second unstaged edit.
            Git(gitRoot, "add", "Calc.cs");
            await File.WriteAllTextAsync(calcPath, "line1-x\nline2\nline3-modified\nline4\nline6\n");
            var stagedDiff = provider.GetDiff(gitRoot, GitDiffScope.Staged);
            Assert(stagedDiff.Files.Count == 1 && stagedDiff.Files[0].Ranges.SequenceEqual([(3, 3), (4, 4)]),
                "staged diff should only contain the staged edit ranges");
            var unstagedDiff2 = provider.GetDiff(gitRoot, GitDiffScope.Unstaged);
            Assert(unstagedDiff2.Files.Count == 1 && unstagedDiff2.Files[0].Ranges.SequenceEqual([(1, 1)]),
                "unstaged diff should only contain the not-yet-staged edit");

            Git(gitRoot, "add", "-A");
            Assert(Git(gitRoot, "commit", "-q", "-m", "c2").ExitCode == 0, "fixture commit c2 should succeed");
            var c2 = provider.TryGetHeadCommit(gitRoot)!;

            // compare scope (baseRef...HEAD): a committed edit, a new file and a deletion.
            await File.WriteAllTextAsync(calcPath, "line1-x\nline2-y\nline3-modified\nline4\nline6\n");
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "New.cs"), "n1\nn2\nn3\n");
            File.Delete(Path.Combine(gitRoot, "Doomed.txt"));
            Git(gitRoot, "add", "-A");
            Assert(Git(gitRoot, "commit", "-q", "-m", "c3").ExitCode == 0, "fixture commit c3 should succeed");
            var compareDiff = provider.GetDiff(gitRoot, GitDiffScope.Compare, c2);
            Assert(compareDiff.Files.Count == 3, $"compare diff should carry the three touched files (got {compareDiff.Files.Count})");
            var compareCalc = compareDiff.Files.Single(f => f.Path == "Calc.cs");
            Assert(compareCalc.Ranges.SequenceEqual([(2, 2)]), "compare diff should map the committed edit to [2,2]");
            var compareNew = compareDiff.Files.Single(f => f.Path == "New.cs");
            Assert(!compareNew.IsDeleted && compareNew.Ranges.SequenceEqual([(1, 3)]),
                "a new file should map to its full-file range [1,N]");
            Assert(compareDiff.Files.Single(f => f.Path == "Doomed.txt").IsDeleted,
                "a deleted file should be marked IsDeleted in the compare diff");

            // compare without a resolvable baseRef → diagnostic, never a throw.
            var noBaseDiff = provider.GetDiff(gitRoot, GitDiffScope.Compare);
            Assert(noBaseDiff.Files.Count == 0 && noBaseDiff.Diagnostics.Any(d => d.Contains("baseRef", StringComparison.Ordinal)),
                "compare without baseRef should return empty files plus a baseRef diagnostic");
            var badBaseDiff = provider.GetDiff(gitRoot, GitDiffScope.Compare, "no-such-ref");
            Assert(badBaseDiff.Files.Count == 0 && badBaseDiff.Diagnostics.Count > 0,
                "compare with an unresolvable baseRef should degrade to a diagnostic");

            // staleness: IndexAsync records ck_meta.indexed_commit; status compares it to HEAD.
            using (var gitDb = new CozoDb("mem", ""))
            {
                var gitOm = new CozoOm(gitDb);
                await new RepositoryIndexer().IndexAsync(gitOm, new RepositoryIndexRequest(gitRoot, RepositoryId: "repo:gitfixture", UseGitIgnore: false));
                var metaRows = await gitOm.Runtime.Store.RunAsync(
                    """?[value] := *ck_meta{ key: "indexed_commit", value }""");
                Assert(metaRows.Rows.Count == 1 && metaRows.Rows[0][0].GetString() == provider.TryGetHeadCommit(gitRoot),
                    "IndexAsync on a git repo should record the HEAD commit as ck_meta.indexed_commit");

                var staleRunner = new LlmWikiToolRunner(gitOm);
                var fresh = await staleRunner.GetStalenessAsync(gitRoot);
                Assert(fresh.Stale == false && fresh.IndexedCommit == fresh.HeadCommit && fresh.HeadCommit is not null,
                    "right after indexing (no new commit) the status must report stale=false");

                await File.WriteAllTextAsync(calcPath, "line1-x\nline2-y\nline3-modified\nline4\nline6\nline7\n");
                Git(gitRoot, "add", "-A");
                Assert(Git(gitRoot, "commit", "-q", "-m", "c4").ExitCode == 0, "fixture commit c4 should succeed");
                var staleNow = await staleRunner.GetStalenessAsync(gitRoot);
                Assert(staleNow.Stale == true && staleNow.IndexedCommit != staleNow.HeadCommit,
                    "a new commit after indexing must flip the status to stale=true");

                // non-git work dir on an indexed db: headCommit/stale stay null, no error.
                var nonGitStale = await staleRunner.GetStalenessAsync(plainDir);
                Assert(nonGitStale.IndexedCommit is not null && nonGitStale.HeadCommit is null && nonGitStale.Stale is null,
                    "staleness for a non-git work dir should carry null headCommit/stale without failing");
            }

            // never-indexed db (ck_meta absent): indexedCommit/stale null, headCommit still live.
            using (var emptyDb = new CozoDb("mem", ""))
            {
                var emptyStale = await new LlmWikiToolRunner(new CozoOm(emptyDb)).GetStalenessAsync(gitRoot);
                Assert(emptyStale.IndexedCommit is null && emptyStale.Stale is null && emptyStale.HeadCommit is not null,
                    "staleness on a never-indexed db should degrade to null indexedCommit/stale");
            }

            // indexing a non-git directory must not write indexed_commit (and must not fail).
            using (var plainDb = new CozoDb("mem", ""))
            {
                var plainOm = new CozoOm(plainDb);
                await File.WriteAllTextAsync(Path.Combine(plainDir, "readme.md"), "# plain\n");
                await new RepositoryIndexer().IndexAsync(plainOm, new RepositoryIndexRequest(plainDir, RepositoryId: "repo:plain", UseGitIgnore: false));
                var plainMeta = await plainOm.Runtime.Store.RunAsync(
                    """?[value] := *ck_meta{ key: "indexed_commit", value }""");
                Assert(plainMeta.Rows.Count == 0, "indexing a non-git directory should not record an indexed_commit");
            }
        }
    }

    // T2.1 (detect-changes track): detect_changes tool — diff → ck_file/ck_symbol mapping →
    // bounded impact aggregation + staleness. Delta cases: requirements/detect-changes
    // {unstaged-mapping, staged-vs-unstaged, compare-base, impact-aggregation, stale-hint,
    // non-git-safe}.
    {
        static (int ExitCode, string StdOut) Git(string dir, params string[] args)
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(dir);
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(info)!;
            var stdOut = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdOut);
        }

        static JsonObject DetectJson(object result) =>
            System.Text.Json.JsonSerializer.SerializeToNode(result, LlmWikiJson.Options)!.AsObject();

        static string[] Ids(JsonObject result, string field) =>
            result[field]!.AsArray().Select(s => s!["symbolId"]!.GetValue<string>()).ToArray();

        // Schema: detect_changes is registered with the design §3 parameter set.
        var detectSchema = LlmWikiToolRunner.ToolsJson()
            .First(tool => tool?["name"]?.GetValue<string>() == "detect_changes")!["inputSchema"]!["properties"]!.AsObject();
        Assert(new[] { "workDirectory", "scope", "baseRef", "maxImpactDepth", "minConfidence" }.All(detectSchema.ContainsKey),
            "detect_changes schema should expose workDirectory/scope/baseRef/maxImpactDepth/minConfidence parameters");

        if (Git(Path.GetTempPath(), "--version").ExitCode != 0)
        {
            Console.WriteLine("git is unavailable — skipping detect_changes git-fixture cases");
        }
        else
        {
            var dcRoot = Path.Combine(root, "dcrepo");
            Directory.CreateDirectory(dcRoot);
            Assert(Git(dcRoot, "init", "-q", "-b", "main").ExitCode == 0, "git init should succeed in the detect_changes fixture repo");
            Git(dcRoot, "config", "user.email", "test@example.com");
            Git(dcRoot, "config", "user.name", "LlmWiki Tests");
            Git(dcRoot, "config", "commit.gpgsign", "false");
            var svcPath = Path.Combine(dcRoot, "Svc.cs");
            var utilPath = Path.Combine(dcRoot, "Util.cs");
            var notesPath = Path.Combine(dcRoot, "notes.txt");
            // 22 numbered lines; the manually indexed symbols sit at MethodA [3,8], MethodB [10,15].
            await File.WriteAllTextAsync(svcPath, string.Join('\n', Enumerable.Range(1, 22).Select(i => $"// svc line {i}")) + "\n");
            await File.WriteAllTextAsync(utilPath, string.Join('\n', Enumerable.Range(1, 5).Select(i => $"// util line {i}")) + "\n");
            await File.WriteAllTextAsync(notesPath, "note1\nnote2\n");
            Git(dcRoot, "add", "-A");
            Assert(Git(dcRoot, "commit", "-q", "-m", "c1").ExitCode == 0, "detect_changes fixture commit c1 should succeed");
            var c1 = Git(dcRoot, "rev-parse", "HEAD").StdOut.Trim();

            using var dcDb = new CozoDb("mem", "");
            var dcOm = new CozoOm(dcDb);
            await dcOm.InitCodeKnowledgeAsync();
            // Hand-indexed facts pinned to the fixture's line layout: ck_file.path uses the same
            // repo-root-relative forward-slash form that both RepositoryIndexer and git diff emit.
            await dcOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Files:
                [
                    new CodeFileFact("file:dc:svc", "repo:dc", "Svc.cs"),
                    new CodeFileFact("file:dc:util", "repo:dc", "Util.cs")
                ],
                Symbols:
                [
                    new CodeSymbolFact("symbol:dc:a", "file:dc:svc", "MethodA", "method", 3, 8, "MethodA()"),
                    new CodeSymbolFact("symbol:dc:b", "file:dc:svc", "MethodB", "method", 10, 15, "MethodB()"),
                    new CodeSymbolFact("symbol:dc:u", "file:dc:util", "UtilX", "method", 1, 5, "UtilX()")
                ],
                Edges:
                [
                    new CodeEdgeFact("symbol:dc:b", "symbol:dc:a", CodeEdgeKinds.Calls, "file:dc:svc", 12, 0.9, "roslyn", "B calls A")
                ]));
            using (dcDb.Run(
                """
                ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
                  ["process:dc01", "DetectFlow", "symbol:dc:b", "public_api", "public_api", 2]]
                :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
                """)) { }
            using (dcDb.Run(
                """
                ?[process_id, step, symbol_id, via_kind] <- [
                  ["process:dc01", 0, "symbol:dc:b", ""], ["process:dc01", 1, "symbol:dc:a", "CALLS"]]
                :put ck_process_step {process_id, step => symbol_id, via_kind}
                """)) { }
            await dcOm.Runtime.Store.RunAsync(
                """
                ?[key, value] <- [["indexed_commit", $commit]]
                :put ck_meta {key => value}
                """,
                new Dictionary<string, object?> { ["commit"] = c1 });

            var dcRunner = new LlmWikiToolRunner(dcOm);

            // unstaged-mapping + impact-aggregation: an edit inside MethodA [3,8] plus a change
            // to a file the index has never seen (notes.txt).
            var svcLines = (await File.ReadAllTextAsync(svcPath)).Split('\n');
            svcLines[4] = "// svc line 5 modified";
            await File.WriteAllTextAsync(svcPath, string.Join('\n', svcLines));
            await File.WriteAllTextAsync(notesPath, "note1 changed\nnote2\n");
            var unstaged = DetectJson(await dcRunner.CallAsync("detect_changes", new JsonObject { ["workDirectory"] = dcRoot }));
            Assert(unstaged["scope"]!.GetValue<string>() == "unstaged", "detect_changes should default to the unstaged scope");
            Assert(Ids(unstaged, "changedSymbols").SequenceEqual(["symbol:dc:a"]),
                "an unstaged edit inside MethodA should map to exactly that symbol (got: "
                + string.Join(", ", Ids(unstaged, "changedSymbols")) + ")");
            var changedA = unstaged["changedSymbols"]!.AsArray().Single()!.AsObject();
            Assert(changedA["name"]!.GetValue<string>() == "MethodA" && changedA["location"]!.GetValue<string>() == "Svc.cs:3",
                "a changed symbol should carry its name and a clickable file:line location");
            Assert(unstaged["changedFiles"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(["Svc.cs", "notes.txt"]),
                "changedFiles should list every diffed file, mapped or not");
            Assert(unstaged["unmappedFiles"]!.AsArray().Select(f => f!.GetValue<string>()).SequenceEqual(["notes.txt"]),
                "files without a ck_file row should be surfaced as unmappedFiles");
            Assert(Ids(unstaged, "impactedSymbols").Contains("symbol:dc:b"),
                "the caller of a changed symbol should appear in impactedSymbols");
            Assert(unstaged["affectedProcesses"]!.AsArray().Select(p => p!["processId"]!.GetValue<string>()).SequenceEqual(["process:dc01"]),
                "execution flows containing changed or impacted symbols should be aggregated deduplicated");
            Assert(unstaged["risk"]!.GetValue<string>() == "LOW", "one direct caller should aggregate to LOW risk");
            Assert(unstaged["truncatedChangedSymbols"]!.GetValue<int>() == 0
                    && unstaged["truncatedImpactedSymbols"]!.GetValue<int>() == 0
                    && unstaged["truncatedProcesses"]!.GetValue<int>() == 0,
                "bounded outputs should carry explicit truncation counters");
            Assert(unstaged["stale"]!.GetValue<bool>() == false && unstaged["diagnostics"]!.AsArray().Count == 0,
                "a fresh index over a clean HEAD should report stale=false without diagnostics");

            // staged-vs-unstaged: stage everything, then make one new unstaged edit in UtilX.
            Git(dcRoot, "add", "-A");
            var utilLines = (await File.ReadAllTextAsync(utilPath)).Split('\n');
            utilLines[1] = "// util line 2 modified";
            await File.WriteAllTextAsync(utilPath, string.Join('\n', utilLines));
            var stagedOnly = DetectJson(await dcRunner.CallAsync("detect_changes", new JsonObject { ["workDirectory"] = dcRoot, ["scope"] = "staged" }));
            Assert(stagedOnly["scope"]!.GetValue<string>() == "staged" && Ids(stagedOnly, "changedSymbols").SequenceEqual(["symbol:dc:a"]),
                "scope=staged should only see the staged MethodA edit (got: " + string.Join(", ", Ids(stagedOnly, "changedSymbols")) + ")");
            var unstagedOnly = DetectJson(await dcRunner.CallAsync("detect_changes", new JsonObject { ["workDirectory"] = dcRoot, ["scope"] = "unstaged" }));
            Assert(Ids(unstagedOnly, "changedSymbols").SequenceEqual(["symbol:dc:u"]),
                "scope=unstaged should only see the not-yet-staged UtilX edit (got: " + string.Join(", ", Ids(unstagedOnly, "changedSymbols")) + ")");

            // compare-base + stale-hint: commit everything on a feature branch; main stays at c1.
            Git(dcRoot, "checkout", "-q", "-b", "feature");
            Git(dcRoot, "add", "-A");
            Assert(Git(dcRoot, "commit", "-q", "-m", "c2").ExitCode == 0, "detect_changes fixture commit c2 should succeed");
            var compare = DetectJson(await dcRunner.CallAsync("detect_changes", new JsonObject
            {
                ["workDirectory"] = dcRoot,
                ["scope"] = "compare",
                ["baseRef"] = c1
            }));
            Assert(Ids(compare, "changedSymbols").Order(StringComparer.Ordinal).SequenceEqual(["symbol:dc:a", "symbol:dc:u"]),
                "compare against the base commit should map both committed edits (got: " + string.Join(", ", Ids(compare, "changedSymbols")) + ")");
            Assert(compare["baseRef"]!.GetValue<string>() == c1, "an explicit baseRef should be echoed back");
            Assert(compare["stale"]!.GetValue<bool>() == true
                    && compare["staleHint"]!.GetValue<string>().Contains("index", StringComparison.OrdinalIgnoreCase),
                "a HEAD ahead of indexed_commit must surface stale=true plus a reindex hint");

            // compare default chain: origin/HEAD is absent, so the chain must fall back to main.
            var compareDefault = DetectJson(await dcRunner.CallAsync("detect_changes", new JsonObject
            {
                ["workDirectory"] = dcRoot,
                ["scope"] = "compare"
            }));
            Assert(compareDefault["baseRef"]!.GetValue<string>() == "main",
                "compare without baseRef should resolve through the origin/HEAD → main → master chain");
            Assert(Ids(compareDefault, "changedSymbols").Order(StringComparer.Ordinal).SequenceEqual(["symbol:dc:a", "symbol:dc:u"]),
                "the resolved default baseRef should yield the same mapping as the explicit base commit");

            // compare default chain exhausted: a repo whose only branch is trunk (no origin/main/
            // master) must degrade to a diagnostic, never a throw.
            var trunkRoot = Path.Combine(root, "dctrunk");
            Directory.CreateDirectory(trunkRoot);
            Assert(Git(trunkRoot, "init", "-q", "-b", "trunk").ExitCode == 0, "git init -b trunk should succeed");
            Git(trunkRoot, "config", "user.email", "test@example.com");
            Git(trunkRoot, "config", "user.name", "LlmWiki Tests");
            Git(trunkRoot, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(trunkRoot, "t.txt"), "t\n");
            Git(trunkRoot, "add", "-A");
            Assert(Git(trunkRoot, "commit", "-q", "-m", "t1").ExitCode == 0, "trunk fixture commit should succeed");
            var chainExhausted = DetectJson(await dcRunner.CallAsync("detect_changes", new JsonObject
            {
                ["workDirectory"] = trunkRoot,
                ["scope"] = "compare"
            }));
            Assert(chainExhausted["changedSymbols"]!.AsArray().Count == 0
                    && chainExhausted["diagnostics"]!.AsArray().Any(d => d!.GetValue<string>().Contains("baseRef", StringComparison.Ordinal)),
                "an exhausted baseRef chain should return empty results plus an explanatory diagnostic");

            // non-git-safe: a plain directory yields diagnostics + empty results, never a throw.
            var dcPlain = Path.Combine(root, "dcplain");
            Directory.CreateDirectory(dcPlain);
            var nonGit = DetectJson(await dcRunner.CallAsync("detect_changes", new JsonObject { ["workDirectory"] = dcPlain }));
            Assert(nonGit["diagnostics"]!.AsArray().Count > 0
                    && nonGit["changedSymbols"]!.AsArray().Count == 0
                    && nonGit["changedFiles"]!.AsArray().Count == 0
                    && nonGit["impactedSymbols"]!.AsArray().Count == 0,
                "detect_changes on a non-git directory should degrade to diagnostics plus empty results");
            Assert(nonGit["stale"] is null, "staleness on a non-git work dir should stay null");
        }
    }
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

// Roslyn semantic enhancement capsule (roslyn-csharp-resolution track T1.1) — pure managed, no native deps.
Cozo.DotNet.LlmWiki.Tests.RoslynEnhancerTests.Run(Assert);

// LLM backend capsule (add-llm-wiki-llm-pipeline track T1.1) — mock HttpMessageHandler only, no network.
await Cozo.DotNet.LlmWiki.Tests.LlmClientTests.RunAsync((condition, message) => Assert(condition, message));

// Four-phase fractal wiki pipeline (add-llm-wiki-llm-pipeline track T2.1) — pinned communities + mock ILlmClient.
await Cozo.DotNet.LlmWiki.Tests.FractalWikiPipelineTests.RunAsync((condition, message) => Assert(condition, message));

// Engineering fractal generation (add-llm-wiki-engineering-fractal track T1.1) — structure layer only, no LLM.
await Cozo.DotNet.LlmWiki.Tests.FractalWikiEngineeringFractalTests.RunAsync((condition, message) => Assert(condition, message));

// Modeling fractal skeleton (add-llm-wiki-modeling-fractal track T1.1) — deterministic context discovery, no LLM.
await Cozo.DotNet.LlmWiki.Tests.FractalWikiModelingFractalTests.RunAsync((condition, message) => Assert(condition, message));

// Modeling context budget ranking (fix-wiki-fractal-entry-and-context-ranking track T2.1) —
// public-API member count desc → member count desc → name asc; deterministic tiebreak.
await Cozo.DotNet.LlmWiki.Tests.FractalWikiContextRankingTests.RunAsync((condition, message) => Assert(condition, message));

// Skills generation (add-llm-wiki-skills-generation track T1.1) — per-context + workflow skills,
// deterministic/bounded, own-prefix sync discipline at the generator layer; zero LLM.
await Cozo.DotNet.LlmWiki.Tests.SkillsGeneratorTests.RunAsync((condition, message) => Assert(condition, message));

// Fractal spec checker gate (add-llm-wiki-engineering-fractal track T2.1) — clean output passes, mutations discriminate per RuleId.
await Cozo.DotNet.LlmWiki.Tests.FractalSpecCheckerTests.RunAsync((condition, message) => Assert(condition, message));

// File-level incremental indexing (add-llm-wiki-incremental-indexing track T1.1) — git fixture + hash-baseline fallback.
await Cozo.DotNet.LlmWiki.Tests.IncrementalIndexingTests.RunAsync((condition, message) => Assert(condition, message));

// Full-table consistency gate + derived-layers freshness (add-llm-wiki-incremental-indexing track T2.1)
// — DumpFacts per-table equality after change/remove/add rounds, git and plain fixtures.
await Cozo.DotNet.LlmWiki.Tests.ConsistencyGateTests.RunAsync((condition, message) => Assert(condition, message));

// Claude Code agent hook subcommands (add-llm-wiki-agent-hooks track T1.1)
// — augment enrichment/budget, staleness two-state, silent degrade (empty stdout, exit 0).
await Cozo.DotNet.LlmWiki.Tests.HookCommandTests.RunAsync((condition, message) => Assert(condition, message));

// hooks install/uninstall/status installer (add-llm-wiki-agent-hooks track T2.1)
// — merge-safe settings.json edits: user entries zero-touch, idempotent, parse failure aborts.
await Cozo.DotNet.LlmWiki.Tests.HookInstallerTests.RunAsync((condition, message) => Assert(condition, message));

// skills generate/clean/status CLI wiring (add-llm-wiki-skills-generation track T2.1)
// — own-prefix-only: user directories zero-touch, clean removes own only, idempotent generate.
await Cozo.DotNet.LlmWiki.Tests.SkillsCommandTests.RunAsync((condition, message) => Assert(condition, message));

// wiki subcommand --pipeline dispatch (fix-wiki-fractal-entry-and-context-ranking track T1.1)
// — codument-fractal first-class entry, legacy default untouched, invalid value rejected.
await Cozo.DotNet.LlmWiki.Tests.WikiCliPipelineTests.RunAsync((condition, message) => Assert(condition, message));

// Eval baseline task set + keyword scorer (add-llm-wiki-eval-baseline track T1.1)
// — task-set-shape (>=20 well-formed tasks over real symbols) and scorer-discriminates.
await Cozo.DotNet.LlmWiki.Tests.EvalBaselineTests.RunAsync((condition, message) => Assert(condition, message));

// Eval oracle gate (add-llm-wiki-eval-baseline track T2.1) — both real work roots indexed
// through the shared runner, every task answered by its suggested tool sequence; the
// aggregate must-keyword hit rate is gated at >= 90% (delta case oracle-gate).
await Cozo.DotNet.LlmWiki.Tests.EvalOracleGateTests.RunAsync((condition, message) => Assert(condition, message));

Console.WriteLine("LLM wiki smoke tests passed.");
