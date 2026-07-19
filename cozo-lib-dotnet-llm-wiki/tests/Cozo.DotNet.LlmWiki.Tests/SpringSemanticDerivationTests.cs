using System.Diagnostics;
using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class SpringSemanticDerivationTests
{
    private const string SpringRole = "SPRING_ROLE";
    private const string SpringTransaction = "SPRING_TRANSACTION";

    public static async Task RunAsync(string root, Action<bool, string> assert)
    {
        var repoRoot = Path.Combine(root, "java-spring-semantic-repo");
        await WriteFixtureAsync(repoRoot);

        var batchResult = await new RepositoryIndexer().BuildBatchAsync(new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:java-spring-semantic",
            RepositoryName: "java-spring-semantic",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false));

        var files = batchResult.Batch.Files ?? [];
        var symbols = batchResult.Batch.Symbols ?? [];
        var edges = batchResult.Batch.Edges ?? [];
        var concepts = batchResult.Batch.Concepts ?? [];
        var entryPoints = batchResult.Batch.EntryPoints ?? [];
        var diagnostics = batchResult.Batch.Diagnostics ?? [];
        var failures = new List<string>();

        void Expect(bool condition, string message)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }

        Expect(files.Count(file => file.Language == "java") >= 12,
            "spring fixture sanity: batch should include the Java Spring fixture files");
        Expect(symbols.Any(symbol => symbol.Lang == "java" && symbol.Name == "SpringOrderController" && symbol.Resolver == "treesitter"),
            "spring fixture sanity: Java symbols should be available before Spring derivation runs");

        ExpectConcepts(concepts, failures);

        var restController = Symbol(symbols, "SpringOrderController", "class");
        var mvcController = Symbol(symbols, "MvcStatusController", "class");
        var service = Symbol(symbols, "OrderApplicationService", "class");
        var component = Symbol(symbols, "RecordEnricher", "class");
        var configuration = Symbol(symbols, "RecordSpringConfiguration", "class");
        var repository = Symbol(symbols, "OrderJpaRepository", "class");
        var mybatisMapper = Symbol(symbols, "OrderMyBatisMapper", "interface");
        var mapStructMapper = Symbol(symbols, "OrderViewMapper", "interface");
        var wildcardMyBatisMapper = Symbol(symbols, "WildcardMyBatisMapper", "interface");
        var wildcardMapStructMapper = Symbol(symbols, "WildcardMapStructMapper", "interface");
        var ambiguousWildcardMapper = Symbol(symbols, "AmbiguousWildcardMapper", "interface");
        var entity = Symbol(symbols, "OrderEntity", "class");
        var dto = Symbol(symbols, "OrderCreateDTO", "class");
        var vo = Symbol(symbols, "OrderSummaryVO", "class");
        var handler = Symbol(symbols, "OrderCommandHandler", "class");
        var listener = Symbol(symbols, "OrderDomainListener", "class");

        ExpectAnnotatedRole(edges, restController, "spring:role:controller", 0.99, "RestController", failures);
        ExpectAnnotatedRole(edges, mvcController, "spring:role:controller", 0.99, "Controller", failures);
        ExpectAnnotatedRole(edges, service, "spring:role:service", 0.99, "Service", failures);
        ExpectAnnotatedRole(edges, component, "spring:role:component", 0.99, "Component", failures);
        ExpectAnnotatedRole(edges, configuration, "spring:role:configuration", 0.99, "Configuration", failures);
        ExpectAnnotatedRole(edges, repository, "spring:role:repository", 0.99, "Repository", failures);
        ExpectAnnotatedRole(edges, entity, "spring:role:entity", 0.99, "Entity", failures);
        ExpectNamingRole(edges, dto, "spring:role:dto", "OrderCreateDTO", failures);
        ExpectNamingRole(edges, vo, "spring:role:vo", "OrderSummaryVO", failures);
        ExpectNamingRole(edges, handler, "spring:role:handler", "OrderCommandHandler", failures);
        ExpectAnnotatedRole(edges, listener, "spring:role:listener", 0.99, "EventListener", failures);

        ExpectRole(edges, mybatisMapper, "spring:role:mapper", 0.95, "spring_mapper_import", "org.apache.ibatis.annotations.Mapper", failures);
        ExpectRole(edges, mybatisMapper, "spring:role:repository", 0.95, "spring_mapper_import", "org.apache.ibatis.annotations.Mapper", failures);
        ExpectRole(edges, mapStructMapper, "spring:role:mapper", 0.95, "spring_mapper_import", "org.mapstruct.Mapper", failures);
        Expect(!HasRole(edges, mapStructMapper, "spring:role:repository"),
            "MapStruct @Mapper should not be classified as a repository role");
        ExpectRole(edges, wildcardMyBatisMapper, "spring:role:mapper", 0.95, "spring_mapper_import", "org.apache.ibatis.annotations.Mapper", failures);
        ExpectRole(edges, wildcardMyBatisMapper, "spring:role:repository", 0.95, "spring_mapper_import", "org.apache.ibatis.annotations.Mapper", failures);
        ExpectRole(edges, wildcardMapStructMapper, "spring:role:mapper", 0.95, "spring_mapper_import", "org.mapstruct.Mapper", failures);
        Expect(!HasRole(edges, wildcardMapStructMapper, "spring:role:repository"),
            "MapStruct wildcard @Mapper should not be classified as a repository role");
        Expect(!HasRole(edges, ambiguousWildcardMapper, "spring:role:mapper")
                && !HasRole(edges, ambiguousWildcardMapper, "spring:role:repository"),
            "ambiguous Mapper wildcard imports should produce neither mapper nor repository roles");
        Expect(diagnostics.Any(diagnostic => diagnostic.Kind == "spring_ambiguous_mapper"
                && diagnostic.TargetId == ambiguousWildcardMapper.SymbolId),
            "ambiguous Mapper wildcard imports should emit spring_ambiguous_mapper for the annotated symbol");

        var getOrder = Symbol(symbols, "getOrder", "method");
        var searchOrders = Symbol(symbols, "searchOrders", "method");
        var createOrder = Symbol(symbols, "createOrder", "method");
        var anyOrder = Symbol(symbols, "anyOrder", "method");
        var bulkUpdate = Symbol(symbols, "bulkUpdate", "method");
        var feignRemote = Symbol(symbols, "remoteOrder", "method");
        var transactional = Symbol(symbols, "replaceOrder", "method");
        var kafka = Symbol(symbols, "onKafkaOrderEvent", "method");
        var domainEvent = Symbol(symbols, "onOrderCreated", "method");

        ExpectEntry(entryPoints, getOrder, "http_route",
            ["methods", "GET", "paths", "/api/v1/orders/{id}", "/api/v1/orders/detail", "site", "SpringOrderController.getOrder", "resolver", "spring_annotation", "confidence", "0.99", "evidence", "GetMapping"],
            failures);
        ExpectEntry(entryPoints, searchOrders, "http_route",
            ["methods", "GET", "paths", "/api/v1/orders/search", "evidence", "RequestMethod.GET"],
            failures);
        ExpectEntry(entryPoints, createOrder, "http_route",
            ["methods", "POST", "paths", "/api/v1/orders", "evidence", "PostMapping"],
            failures);
        ExpectEntry(entryPoints, anyOrder, "http_route",
            ["methods", "ANY", "paths", "/api/v1/orders/any", "evidence", "RequestMapping"],
            failures);
        var anyRoute = entryPoints.FirstOrDefault(entry => entry.SymbolId == anyOrder.SymbolId && entry.Kind == "http_route");
        Expect(anyRoute is not null && !anyRoute.Metadata.Contains("methods=GET", StringComparison.Ordinal),
            "bare @RequestMapping should use methods=ANY instead of defaulting to GET");
        ExpectEntry(entryPoints, bulkUpdate, "http_route",
            ["methods", "POST", "PUT", "paths", "/api/v1/bulk", "/api/v1/batch", "evidence", "RequestMapping"],
            failures);
        Expect(!entryPoints.Any(entry => entry.SymbolId == feignRemote.SymbolId && entry.Kind == "http_route"),
            "Feign interface mapping must not become an inbound http_route entry point");

        ExpectRole(edges, transactional, "spring:transaction", 0.99, "spring_annotation", "rollbackFor = Throwable.class", failures, kind: SpringTransaction);
        Expect(edges.Any(edge => edge.FromId == transactional.SymbolId
                && edge.Kind == SpringTransaction
                && edge.Evidence.Contains("propagation = Propagation.REQUIRES_NEW", StringComparison.Ordinal)),
            "SPRING_TRANSACTION evidence should retain propagation parameters");

        ExpectEntry(entryPoints, kafka, "kafka_listener",
            ["topics", "record.order.created", "record.order.updated", "containerFactory", "recordKafkaListenerFactory", "site", "OrderDomainListener.onKafkaOrderEvent", "resolver", "spring_annotation", "confidence", "0.99", "evidence", "KafkaListener"],
            failures);
        ExpectEntry(entryPoints, domainEvent, "event_listener",
            ["eventTypes", "OrderCreatedEvent", "site", "OrderDomainListener.onOrderCreated", "resolver", "spring_annotation", "confidence", "0.99", "evidence", "EventListener"],
            failures);
        ExpectRole(edges, kafka, "spring:role:listener", 0.99, "spring_annotation", "KafkaListener", failures);
        ExpectRole(edges, domainEvent, "spring:role:listener", 0.99, "spring_annotation", "EventListener", failures);

        assert(failures.Count == 0,
            "Spring semantic derivation facts are missing or incomplete:\n- " + string.Join("\n- ", failures));

        await ExpectNoSpringConceptsWithoutDerivedFactsAsync(root, assert);
        await ExpectPersistenceAndProcessAsync(root, assert);
        await ExpectIncrementalCleanupAsync(root, assert);
        await ExpectExtractProcessesSwitchAsync(root, assert);
    }

    private static void ExpectConcepts(IReadOnlyList<CodeConceptFact> concepts, List<string> failures)
    {
        foreach (var conceptId in new[]
        {
            "spring:role:controller",
            "spring:role:service",
            "spring:role:component",
            "spring:role:configuration",
            "spring:role:repository",
            "spring:role:mapper",
            "spring:role:listener",
            "spring:role:handler",
            "spring:role:dto",
            "spring:role:entity",
            "spring:role:vo",
            "spring:transaction",
        })
        {
            if (!concepts.Any(concept => concept.ConceptId == conceptId))
            {
                failures.Add($"missing CodeConceptFact {conceptId}");
            }
        }
    }

    private static void ExpectAnnotatedRole(
        IReadOnlyList<CodeEdgeFact> edges,
        CodeSymbolFact symbol,
        string conceptId,
        double confidence,
        string evidence,
        List<string> failures) =>
        ExpectRole(edges, symbol, conceptId, confidence, "spring_annotation", evidence, failures);

    private static void ExpectNamingRole(
        IReadOnlyList<CodeEdgeFact> edges,
        CodeSymbolFact symbol,
        string conceptId,
        string evidence,
        List<string> failures) =>
        ExpectRole(edges, symbol, conceptId, 0.70, "spring_naming", evidence, failures);

    private static void ExpectRole(
        IReadOnlyList<CodeEdgeFact> edges,
        CodeSymbolFact symbol,
        string conceptId,
        double confidence,
        string resolver,
        string evidence,
        List<string> failures,
        string kind = SpringRole)
    {
        if (symbol.SymbolId.Length == 0)
        {
            failures.Add($"missing source symbol for {conceptId}");
            return;
        }

        var edge = edges.FirstOrDefault(candidate => candidate.FromId == symbol.SymbolId
            && candidate.ToId == conceptId
            && candidate.Kind == kind);
        if (edge is null)
        {
            failures.Add($"missing {kind} {symbol.Name} -> {conceptId}");
            return;
        }

        if (edge.FileId.Length == 0 || edge.Line <= 0)
        {
            failures.Add($"{kind} {symbol.Name} -> {conceptId} should carry file and line provenance");
        }

        if (edge.Resolver != resolver)
        {
            failures.Add($"{kind} {symbol.Name} -> {conceptId} should use resolver={resolver}");
        }

        if (Math.Abs(edge.Confidence - confidence) > 1e-9)
        {
            failures.Add($"{kind} {symbol.Name} -> {conceptId} should have confidence {confidence:0.00}");
        }

        if (!edge.Evidence.Contains(evidence, StringComparison.Ordinal))
        {
            failures.Add($"{kind} {symbol.Name} -> {conceptId} should preserve evidence containing {evidence}");
        }
    }

    private static void ExpectEntry(
        IReadOnlyList<CodeEntryPointFact> entryPoints,
        CodeSymbolFact symbol,
        string kind,
        IReadOnlyList<string> metadataTokens,
        List<string> failures)
    {
        if (symbol.SymbolId.Length == 0)
        {
            failures.Add($"missing source symbol for {kind}");
            return;
        }

        var entry = entryPoints.FirstOrDefault(candidate => candidate.SymbolId == symbol.SymbolId
            && candidate.Kind == kind);
        if (entry is null)
        {
            failures.Add($"missing {kind} entry point for {symbol.Name}");
            return;
        }

        foreach (var token in metadataTokens)
        {
            if (!entry.Metadata.Contains(token, StringComparison.Ordinal))
            {
                failures.Add($"{kind} entry point for {symbol.Name} metadata should contain {token}");
            }
        }
    }

    private static bool HasRole(IReadOnlyList<CodeEdgeFact> edges, CodeSymbolFact symbol, string conceptId) =>
        symbol.SymbolId.Length > 0
        && edges.Any(edge => edge.FromId == symbol.SymbolId
            && edge.ToId == conceptId
            && edge.Kind == SpringRole);

    private static CodeSymbolFact Symbol(IReadOnlyList<CodeSymbolFact> symbols, string name, string kind)
    {
        var matches = symbols.Where(symbol => symbol.Lang == "java"
            && symbol.Name == name
            && symbol.Kind == kind).ToArray();
        return matches.Length == 1
            ? matches[0]
            : new CodeSymbolFact("", "", name, kind);
    }

    private static async Task ExpectNoSpringConceptsWithoutDerivedFactsAsync(string root, Action<bool, string> assert)
    {
        var repoRoot = Path.Combine(root, "java-ordinary-semantic-repo");
        Directory.CreateDirectory(repoRoot);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "Plain.java"), """
        package ordinary;

        public class Plain {
            public String run(String name) {
                return name.trim();
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "Sample.cs"), """
        namespace Ordinary;
        public class Sample { public string Run() => "ok"; }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "app.ts"), """
        export class App { run(name: string): string { return name.trim(); } }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "widget.js"), """
        export function widget(name) { return name.trim(); }
        """);

        var batch = await new RepositoryIndexer().BuildBatchAsync(new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:ordinary-semantic",
            RepositoryName: "ordinary-semantic",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false));
        assert((batch.Batch.Files ?? []).Any(file => file.Language == "java")
                && (batch.Batch.Files ?? []).Any(file => file.Language == "csharp")
                && (batch.Batch.Files ?? []).Any(file => file.Language == "typescript")
                && (batch.Batch.Files ?? []).Any(file => file.Language == "javascript"),
            "ordinary mixed fixture should exercise C#/TS/JS/Java routing");
        assert(!(batch.Batch.Concepts ?? []).Any(concept => concept.ConceptId.StartsWith("spring:", StringComparison.Ordinal)),
            "ordinary Java and non-Java batches should not inject Spring concepts without derived Spring facts");
    }

    private static async Task ExpectPersistenceAndProcessAsync(string root, Action<bool, string> assert)
    {
        var repoRoot = Path.Combine(root, "java-spring-persistence-repo");
        await WriteFixtureAsync(repoRoot);

        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var summary = await new RepositoryIndexer().IndexAsync(om, new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:java-spring-persistence",
            RepositoryName: "java-spring-persistence",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false));
        assert(summary.Symbols > 0 && summary.EntryPoints > 0 && summary.Processes > 0,
            "IndexAsync should persist Spring fixture symbols, entry points, and processes");

        var concepts = await QueryStringsAsync(om, "?[concept_id] := *ck_concept{ concept_id }");
        assert(concepts.Contains("spring:role:controller") && concepts.Contains("spring:role:listener")
                && concepts.Contains("spring:transaction"),
            "IndexAsync should persist queryable Spring concepts into ck_concept");

        var springEdges = await om.Runtime.Store.RunAsync(
            """
            ?[name, kind, to_id, file_id, line, confidence, resolver, evidence] :=
              *ck_edge{ from_id, to_id, kind, file_id, line, confidence, resolver, evidence },
              *ck_symbol{ symbol_id: from_id, name },
              is_in(kind, ["SPRING_ROLE", "SPRING_TRANSACTION"])
            """);
        var springEdgeRows = springEdges.Rows
            .Select(row => (
                Name: AsString(row[0]),
                Kind: AsString(row[1]),
                ToId: AsString(row[2]),
                FileId: AsString(row[3]),
                Line: AsInt(row[4]),
                Confidence: AsDouble(row[5]),
                Resolver: AsString(row[6]),
                Evidence: AsString(row[7])))
            .ToArray();
        assert(springEdgeRows.Any(row => row.Name == "SpringOrderController"
                && row.Kind == SpringRole
                && row.ToId == "spring:role:controller"
                && row.FileId.EndsWith("SpringOrderController.java", StringComparison.Ordinal)
                && row.Line > 0
                && Math.Abs(row.Confidence - 0.99) < 1e-9
                && row.Resolver == "spring_annotation"
                && row.Evidence.Contains("RestController", StringComparison.Ordinal)),
            "ck_edge should persist Spring controller role with metadata/provenance intact");
        assert(springEdgeRows.Any(row => row.Name == "replaceOrder"
                && row.Kind == SpringTransaction
                && row.ToId == "spring:transaction"
                && row.FileId.EndsWith("OrderApplicationService.java", StringComparison.Ordinal)
                && row.Line > 0
                && row.Evidence.Contains("rollbackFor = Throwable.class", StringComparison.Ordinal)),
            "ck_edge should persist Spring transaction facts with evidence and provenance intact");

        var entryRows = await om.Runtime.Store.RunAsync(
            """
            ?[name, kind, metadata] :=
              *ck_entry_point{ symbol_id, kind, metadata },
              *ck_symbol{ symbol_id, name },
              is_in(kind, ["http_route", "kafka_listener", "event_listener"])
            """);
        var entries = entryRows.Rows
            .Select(row => (Name: AsString(row[0]), Kind: AsString(row[1]), Metadata: AsString(row[2])))
            .ToArray();
        assert(entries.Any(row => row.Name == "getOrder"
                && row.Kind == "http_route"
                && row.Metadata.Contains("methods=GET", StringComparison.Ordinal)
                && row.Metadata.Contains("/api/v1/orders/{id}", StringComparison.Ordinal)
                && row.Metadata.Contains("resolver=spring_annotation", StringComparison.Ordinal)
                && row.Metadata.Contains("evidence=@GetMapping", StringComparison.Ordinal)),
            "ck_entry_point should persist Spring http_route metadata and provenance");
        assert(entries.Any(row => row.Name == "onKafkaOrderEvent"
                && row.Kind == "kafka_listener"
                && row.Metadata.Contains("record.order.created", StringComparison.Ordinal)),
            "ck_entry_point should persist Spring Kafka listener metadata");
        assert(entries.Any(row => row.Name == "onOrderCreated"
                && row.Kind == "event_listener"
                && row.Metadata.Contains("OrderCreatedEvent", StringComparison.Ordinal)),
            "ck_entry_point should persist Spring event listener metadata");

        var routeProcessRows = await om.Runtime.Store.RunAsync(
            """
            ?[process_id, process_name, step_count] :=
              *ck_process{ process_id, name: process_name, entry_symbol_id, entry_kind: "http_route", step_count },
              *ck_symbol{ symbol_id: entry_symbol_id, name: "getOrder" }
            """);
        assert(routeProcessRows.Rows.Count == 1 && AsInt(routeProcessRows.Rows[0][2]) >= 3,
            "Spring controller http_route should participate in ck_process with a >=3-step in-repo CALLS chain");
        var routeProcessId = AsString(routeProcessRows.Rows[0][0]);
        var routeSteps = await QueryStringsAsync(om,
            """
            ?[name] :=
              *ck_process_step{ process_id: $process_id, step, symbol_id },
              *ck_symbol{ symbol_id, name }
            """,
            ("process_id", routeProcessId));
        assert(new[] { "getOrder", "loadOrder", "formatOrder" }.All(routeSteps.Contains),
            "Spring http_route process should include the controller route and downstream in-repo service calls");
    }

    private static async Task ExpectIncrementalCleanupAsync(string root, Action<bool, string> assert)
    {
        var repoRoot = Path.Combine(root, "java-spring-incremental-repo");
        await WriteFixtureAsync(repoRoot);
        Git(repoRoot, "init", "-q");
        Git(repoRoot, "add", "-A");
        Git(repoRoot, "commit", "-q", "-m", "initial spring fixture");

        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var indexer = new RepositoryIndexer();
        var request = new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:java-spring-incremental",
            RepositoryName: "java-spring-incremental",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false);

        var first = await indexer.IndexAsync(om, request);
        assert(!first.IncrementalUsed, "Spring incremental fixture bootstrap should take the full path");
        var originalRoutes = await QueryStringsAsync(om, "?[metadata] := *ck_entry_point{ kind: \"http_route\", metadata }");
        assert(originalRoutes.Any(metadata => metadata.Contains("/orders/detail", StringComparison.Ordinal)),
            "precondition: initial Spring route metadata should include the old route");

        var controllerPath = Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "web", "SpringOrderController.java");
        var controllerText = await File.ReadAllTextAsync(controllerPath);
        await File.WriteAllTextAsync(controllerPath, controllerText.Replace(
            "@GetMapping(value = {\"/orders/{id}\", \"/orders/detail\"})",
            "@GetMapping(value = {\"/orders/current\"})",
            StringComparison.Ordinal));
        Git(repoRoot, "add", "-A");
        Git(repoRoot, "commit", "-q", "-m", "change spring route");

        var changed = await indexer.IndexAsync(om, request);
        assert(changed.IncrementalUsed && changed.ChangedFiles == 1 && changed.RemovedFiles == 0,
            $"Spring route modification should use incremental auto (changed={changed.ChangedFiles}, removed={changed.RemovedFiles})");
        var changedRoutes = await QueryStringsAsync(om, "?[metadata] := *ck_entry_point{ kind: \"http_route\", metadata }");
        assert(changedRoutes.Any(metadata => metadata.Contains("/orders/current", StringComparison.Ordinal))
                && !changedRoutes.Any(metadata => metadata.Contains("/orders/detail", StringComparison.Ordinal)),
            "modified Spring route should replace old ck_entry_point metadata without stale route residue");

        var controllerFileId = "file:repo:java-spring-incremental:src/main/java/fixture/spring/web/SpringOrderController.java";
        var controllerSymbolPrefix = $"symbol:{controllerFileId}:";
        File.Delete(controllerPath);
        Git(repoRoot, "add", "-A");
        Git(repoRoot, "commit", "-q", "-m", "remove spring controller");

        var removed = await indexer.IndexAsync(om, request);
        assert(removed.IncrementalUsed && removed.RemovedFiles == 1,
            $"Spring controller deletion should use incremental auto with one removed file (removed={removed.RemovedFiles})");
        var staleControllerEdges = await QueryStringsAsync(om,
            """
            ?[x] := *ck_edge{ from_id, to_id, kind, file_id }, file_id = $file_id, x = concat(from_id, '|', to_id, '|', kind)
            ?[x] := *ck_edge{ from_id, to_id, kind }, starts_with(from_id, $symbol_prefix), x = concat(from_id, '|', to_id, '|', kind)
            ?[x] := *ck_edge{ from_id, to_id, kind }, starts_with(to_id, $symbol_prefix), x = concat(from_id, '|', to_id, '|', kind)
            """,
            ("file_id", controllerFileId), ("symbol_prefix", controllerSymbolPrefix));
        assert(staleControllerEdges.Count == 0,
            $"deleted Spring file should leave no stale ck_edge rows (got {string.Join(", ", staleControllerEdges.Take(3))})");
        var staleControllerEntries = await QueryStringsAsync(om,
            "?[symbol_id] := *ck_entry_point{ symbol_id }, starts_with(symbol_id, $symbol_prefix)",
            ("symbol_prefix", controllerSymbolPrefix));
        assert(staleControllerEntries.Count == 0,
            "deleted Spring file should leave no stale ck_entry_point rows");
        var staleControllerProcesses = await QueryStringsAsync(om,
            """
            ?[symbol_id] := *ck_process{ entry_symbol_id: symbol_id }, starts_with(symbol_id, $symbol_prefix)
            ?[symbol_id] := *ck_process_step{ symbol_id }, starts_with(symbol_id, $symbol_prefix)
            """,
            ("symbol_prefix", controllerSymbolPrefix));
        assert(staleControllerProcesses.Count == 0,
            "deleted Spring file should leave no stale ck_process/ck_process_step references");
    }

    private static async Task ExpectExtractProcessesSwitchAsync(string root, Action<bool, string> assert)
    {
        var repoRoot = Path.Combine(root, "java-spring-process-off-repo");
        await WriteFixtureAsync(repoRoot);

        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var summary = await new RepositoryIndexer().IndexAsync(om, new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:java-spring-process-off",
            RepositoryName: "java-spring-process-off",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false,
            ExtractProcesses: false));
        assert(summary.EntryPoints == 0 && summary.Processes == 0 && summary.DroppedProcesses == 0,
            "ExtractProcesses=false should keep Spring entry point/process counters at zero");
        var entryPoints = await QueryStringsAsync(om, "?[symbol_id] := *ck_entry_point{ symbol_id }");
        assert(entryPoints.Count == 0,
            "ExtractProcesses=false should persist no Spring ck_entry_point rows");
        var roleEdges = await QueryStringsAsync(om, "?[from_id] := *ck_edge{ from_id, kind: \"SPRING_ROLE\" }");
        var transactionEdges = await QueryStringsAsync(om, "?[from_id] := *ck_edge{ from_id, kind: \"SPRING_TRANSACTION\" }");
        assert(roleEdges.Count > 0 && transactionEdges.Count > 0,
            "ExtractProcesses=false should still persist Spring role and transaction batch facts");
    }

    private static async Task<HashSet<string>> QueryStringsAsync(CozoOm om, string query, params (string Key, object? Value)[] parameters)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            dict[key] = value;
        }

        var result = await om.Runtime.Store.RunAsync(query, dict);
        return result.Rows
            .Select(row => AsString(row[0]))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();

    private static int AsInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : int.Parse(AsString(element), System.Globalization.CultureInfo.InvariantCulture);

    private static double AsDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? value
            : double.Parse(AsString(element), System.Globalization.CultureInfo.InvariantCulture);

    private static void Git(string workDir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("user.name=Test");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("user.email=test@example.com");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start git");
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdErr}");
        }
    }

    private static async Task WriteFixtureAsync(string repoRoot)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "web"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "client"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "service"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "config"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "data"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "mapper"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "events"));

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "web", "SpringOrderController.java"), """
        package fixture.spring.web;

        import fixture.spring.data.OrderCreateDTO;
        import fixture.spring.service.OrderApplicationService;
        import org.springframework.http.ResponseEntity;
        import org.springframework.web.bind.annotation.GetMapping;
        import org.springframework.web.bind.annotation.PostMapping;
        import org.springframework.web.bind.annotation.RequestMapping;
        import org.springframework.web.bind.annotation.RequestMethod;
        import org.springframework.web.bind.annotation.RestController;

        @RestController
        @RequestMapping(path = {"/api/v1"})
        public class SpringOrderController {
            private final OrderApplicationService service;

            public SpringOrderController(OrderApplicationService service) {
                this.service = service;
            }

            @GetMapping(value = {"/orders/{id}", "/orders/detail"})
            public ResponseEntity<String> getOrder(String id) {
                return ResponseEntity.ok(service.loadOrder(id));
            }

            @RequestMapping(value = "/orders/search", method = RequestMethod.GET)
            public ResponseEntity<String> searchOrders(String q) {
                return ResponseEntity.ok(q);
            }

            @PostMapping("/orders")
            public ResponseEntity<String> createOrder(OrderCreateDTO dto) {
                return ResponseEntity.ok(dto.id());
            }

            @RequestMapping("/orders/any")
            public ResponseEntity<String> anyOrder() {
                return ResponseEntity.ok(service.loadOrder("any"));
            }

            @RequestMapping(path = {"/bulk", "/batch"}, method = {RequestMethod.POST, RequestMethod.PUT})
            public ResponseEntity<String> bulkUpdate() {
                return ResponseEntity.ok("bulk");
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "web", "MvcStatusController.java"), """
        package fixture.spring.web;

        import org.springframework.stereotype.Controller;
        import org.springframework.web.bind.annotation.RequestMapping;

        @Controller
        @RequestMapping("/status")
        public class MvcStatusController {
            public String index() {
                return "ok";
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "client", "RemoteOrderClient.java"), """
        package fixture.spring.client;

        import org.springframework.cloud.openfeign.FeignClient;
        import org.springframework.web.bind.annotation.GetMapping;

        @FeignClient(name = "remote-order")
        public interface RemoteOrderClient {
            @GetMapping("/remote/orders/{id}")
            String remoteOrder(String id);
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "service", "OrderApplicationService.java"), """
        package fixture.spring.service;

        import org.springframework.stereotype.Service;
        import org.springframework.transaction.annotation.Propagation;
        import org.springframework.transaction.annotation.Transactional;

        @Service
        public class OrderApplicationService {
            public String loadOrder(String id) {
                return formatOrder(id);
            }

            private String formatOrder(String id) {
                return normalizeOrder(id);
            }

            private String normalizeOrder(String id) {
                return id.trim();
            }

            @Transactional(rollbackFor = Throwable.class, propagation = Propagation.REQUIRES_NEW)
            public void replaceOrder(String id) {
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "service", "RecordEnricher.java"), """
        package fixture.spring.service;

        import org.springframework.stereotype.Component;

        @Component
        public class RecordEnricher {
            public String enrich(String id) {
                return id;
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "config", "RecordSpringConfiguration.java"), """
        package fixture.spring.config;

        import org.springframework.context.annotation.Configuration;

        @Configuration
        public class RecordSpringConfiguration {
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "data", "OrderJpaRepository.java"), """
        package fixture.spring.data;

        import org.springframework.stereotype.Repository;

        @Repository
        public class OrderJpaRepository {
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "mapper", "OrderMyBatisMapper.java"), """
        package fixture.spring.mapper;

        import org.apache.ibatis.annotations.Mapper;

        @Mapper
        public interface OrderMyBatisMapper {
            String selectName(String id);
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "mapper", "OrderViewMapper.java"), """
        package fixture.spring.mapper;

        import org.mapstruct.Mapper;

        @Mapper
        public interface OrderViewMapper {
            String toView(String id);
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "mapper", "WildcardMyBatisMapper.java"), """
        package fixture.spring.mapper;

        import org.apache.ibatis.annotations.*;

        @Mapper
        public interface WildcardMyBatisMapper {
            String selectName(String id);
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "mapper", "WildcardMapStructMapper.java"), """
        package fixture.spring.mapper;

        import org.mapstruct.*;

        @Mapper
        public interface WildcardMapStructMapper {
            String toView(String id);
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "mapper", "AmbiguousWildcardMapper.java"), """
        package fixture.spring.mapper;

        import org.apache.ibatis.annotations.*;
        import org.mapstruct.*;

        @Mapper
        public interface AmbiguousWildcardMapper {
            String map(String id);
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "data", "OrderEntity.java"), """
        package fixture.spring.data;

        import jakarta.persistence.Entity;

        @Entity
        public class OrderEntity {
            private String id;
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "data", "OrderCreateDTO.java"), """
        package fixture.spring.data;

        public class OrderCreateDTO {
            private final String id;

            public OrderCreateDTO(String id) {
                this.id = id;
            }

            public String id() {
                return id;
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "data", "OrderSummaryVO.java"), """
        package fixture.spring.data;

        public class OrderSummaryVO {
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "events", "OrderCommandHandler.java"), """
        package fixture.spring.events;

        public class OrderCommandHandler {
            public void handle(String id) {
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "spring", "events", "OrderDomainListener.java"), """
        package fixture.spring.events;

        import org.springframework.context.event.EventListener;
        import org.springframework.kafka.annotation.KafkaListener;

        public class OrderDomainListener {
            @KafkaListener(
                topics = {
                    "record.order.created",
                    "record.order.updated"
                },
                containerFactory = "recordKafkaListenerFactory"
            )
            public void onKafkaOrderEvent(String message) {
            }

            @EventListener
            public void onOrderCreated(OrderCreatedEvent event) {
            }
        }

        class OrderCreatedEvent {
        }
        """);
    }
}
