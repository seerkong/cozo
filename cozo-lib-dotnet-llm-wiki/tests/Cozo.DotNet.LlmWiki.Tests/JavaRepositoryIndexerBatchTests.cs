using System.Runtime.InteropServices;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.LlmWiki.SemanticParsing;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class JavaRepositoryIndexerBatchTests
{
    public static async Task RunAsync(string root, Action<bool, string> assert)
    {
        var repoRoot = Path.Combine(root, "java-batch-repo");
        await WriteFixtureAsync(repoRoot);

        var request = new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:java-batch",
            RepositoryName: "java-batch",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false);

        var batch = await new RepositoryIndexer().BuildBatchAsync(request);
        var files = batch.Batch.Files ?? [];
        var symbols = batch.Batch.Symbols ?? [];
        var edges = batch.Batch.Edges ?? [];

        assert(files.Any(file => file.Path == "src/main/java/com/acme/orders/OrderService.java" && file.Language == "java"),
            "Java repo batch should include src/main/java/com/acme/orders/OrderService.java as language=java");
        assert(files.Any(file => file.Path == "src/main/java/com/acme/orders/Workflow.java" && file.Language == "java")
                && files.Any(file => file.Path == "src/main/java/com/acme/shared/Payload.java" && file.Language == "java"),
            "Java repo batch should include multiple Java source files");
        assert(files.Any(file => file.Path == "src/main/csharp/OrderBridge.cs" && file.Language == "csharp")
                && files.Any(file => file.Path == "src/main/ts/app.ts" && file.Language == "typescript")
                && files.Any(file => file.Path == "src/main/js/widget.js" && file.Language == "javascript"),
            "mixed Java repo batch should still include C#/TS/JS files");

        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            var javaFiles = files.Where(file => file.Language == "java").ToArray();
            var javaFileIds = javaFiles.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
            var javaSymbols = symbols.Where(symbol => javaFileIds.Contains(symbol.FileId)).ToArray();
            var javaEdges = edges.Where(edge => javaFileIds.Contains(edge.FileId)).ToArray();
            var orderFile = files.Single(file => file.Path == "src/main/java/com/acme/orders/OrderService.java");
            var orderSymbols = javaSymbols.Where(symbol => symbol.FileId == orderFile.FileId).ToArray();

            assert(javaSymbols.Length > 0 && javaSymbols.All(symbol => symbol.Resolver == "treesitter" && symbol.Lang == "java"),
                "Java files should produce resolver=treesitter symbols through RepositoryIndexer.BuildBatchAsync");
            assert(javaEdges.Any(edge => edge.Resolver == "treesitter" && edge.Kind is CodeEdgeKinds.Contains or CodeEdgeKinds.Imports
                    or CodeEdgeKinds.Extends or CodeEdgeKinds.Implements or CodeEdgeKinds.HasMethod or CodeEdgeKinds.HasProperty),
                "Java files should produce treesitter structural edges in the batch");

            var package = orderSymbols.Single(symbol => symbol.SymKey == "java:com.acme.orders#0");
            var orderService = orderSymbols.Single(symbol => symbol.SymKey == "java:com.acme.orders.OrderService#0");
            var repository = orderSymbols.Single(symbol => symbol.SymKey == "java:com.acme.orders.OrderService.repository#0");
            var place = orderSymbols.Single(symbol => symbol.SymKey == "java:com.acme.orders.OrderService.place#1");
            var nested = orderSymbols.Single(symbol => symbol.SymKey == "java:com.acme.orders.OrderService.NestedWorker#0");

            assert(package.ParentId == "" && orderService.ParentId == package.SymbolId,
                "Java package should be the stable parent for top-level class symbols");
            assert(repository.ParentId == orderService.SymbolId && place.ParentId == orderService.SymbolId && nested.ParentId == orderService.SymbolId,
                "Java field/method/nested class parent ids should point at the declaring class");
            assert(orderService.Name == "OrderService" && orderService.Kind == "class" && orderService.Visibility == "public" && orderService.Exported,
                "Java class symbol should preserve name/kind/visibility/exported facts");
            assert(place.Kind == "method" && place.Visibility == "public" && place.StartLine > orderService.StartLine,
                "Java method symbol should preserve method kind, visibility and source line");

            assert(HasEdge(edges, orderFile.FileId, package.SymbolId, CodeEdgeKinds.Contains, "treesitter"),
                "Java file should CONTAINS its package symbol");
            assert(HasEdge(edges, package.SymbolId, orderService.SymbolId, CodeEdgeKinds.Contains, "treesitter"),
                "Java package should CONTAINS OrderService");
            assert(HasEdge(edges, orderService.SymbolId, place.SymbolId, CodeEdgeKinds.HasMethod, "treesitter"),
                "Java class should HAS_METHOD place");
            assert(HasEdge(edges, orderService.SymbolId, repository.SymbolId, CodeEdgeKinds.HasProperty, "treesitter"),
                "Java class should HAS_PROPERTY repository");
            assert(edges.Any(edge => edge.FromId == orderService.SymbolId && edge.Kind == CodeEdgeKinds.Extends
                    && edge.ToId == "typeref:java:AbstractOrderService" && edge.Resolver == "treesitter"),
                "Java class extends clause should produce EXTENDS relation");
            assert(edges.Any(edge => edge.FromId == orderService.SymbolId && edge.Kind == CodeEdgeKinds.Implements
                    && edge.ToId == "typeref:java:Workflow" && edge.Resolver == "treesitter"),
                "Java class implements clause should produce IMPLEMENTS relation");
            assert(edges.Count(edge => edge.FromId == orderFile.FileId && edge.Kind == CodeEdgeKinds.Imports
                    && edge.Resolver == "treesitter" && edge.ToId.StartsWith("import:", StringComparison.Ordinal)) == 3,
                "Java imports should produce file-level IMPORTS facts in the shared import id space");

            var csharp = symbols.Where(symbol => symbol.FileId == files.Single(file => file.Path == "src/main/csharp/OrderBridge.cs").FileId).ToArray();
            var ts = symbols.Where(symbol => symbol.FileId == files.Single(file => file.Path == "src/main/ts/app.ts").FileId).ToArray();
            var js = symbols.Where(symbol => symbol.FileId == files.Single(file => file.Path == "src/main/js/widget.js").FileId).ToArray();
            assert(csharp.Any(symbol => symbol.SymKey == "csharp:Acme.Bridge.OrderBridge#0" && symbol.Resolver == "treesitter"),
                "C# file should still produce treesitter symbols in the same batch");
            assert(ts.Any(symbol => symbol.SymKey.StartsWith("ts:", StringComparison.Ordinal) && symbol.Resolver == "treesitter"),
                "TS file should still produce treesitter symbols in the same batch");
            assert(js.Any(symbol => symbol.Resolver == "treesitter" && symbol.Lang == "javascript"),
                "JS file should still produce treesitter symbols in the same batch");
        }

        await RunFallbackAssertionsAsync(root, repoRoot, assert);
    }

    private static async Task RunFallbackAssertionsAsync(string root, string repoRoot, Action<bool, string> assert)
    {
        var fakeTreeSitter = Path.Combine(root, OperatingSystem.IsWindows() ? "tree-sitter-java-fail.cmd" : "tree-sitter-java-fail");
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(fakeTreeSitter, """
            @echo off
            if "%1"=="--version" (
              echo tree-sitter 0.24.3
              exit /b 0
            )
            echo java parse failed 1>&2
            exit /b 1
            """);
        }
        else
        {
            await File.WriteAllTextAsync(fakeTreeSitter, """
            #!/usr/bin/env bash
            if [[ "$1" == "--version" ]]; then
              echo "tree-sitter 0.24.3"
              exit 0
            fi
            echo "java parse failed" >&2
            exit 1
            """);
            File.SetUnixFileMode(
                fakeTreeSitter,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var selector = ParserBackendSelector.CreateDefault(
            nativeProbeDirectories: ["/nonexistent-treesitter-native-libs"],
            cliParser: new TreeSitterCliParser(fakeTreeSitter));
        var fallbackBatch = await new RepositoryIndexer(selector).BuildBatchAsync(new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:java-batch-fallback",
            RepositoryName: "java-batch-fallback",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false));
        var files = fallbackBatch.Batch.Files ?? [];
        var edges = fallbackBatch.Batch.Edges ?? [];
        var diagnostics = fallbackBatch.Batch.Diagnostics ?? [];
        var javaFileIds = files.Where(file => file.Language == "java").Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
        var orderFileId = files.Single(file => file.Path == "src/main/java/com/acme/orders/OrderService.java").FileId;

        assert(javaFileIds.Count == 3,
            "fallback batch should still index every Java file when native is unavailable and CLI parsing fails");
        assert(edges.Count(edge => edge.FileId == orderFileId && edge.Kind == CodeEdgeKinds.Imports && edge.Resolver == "regex") == 3,
            "fallback batch should still extract Java import facts through the regex tier");
        assert(diagnostics.Count(diagnostic => diagnostic.Kind == "parser_fallback"
                && javaFileIds.Contains(diagnostic.TargetId)
                && diagnostic.Severity == "warning"
                && diagnostic.Message.Contains("cli parse degraded to regex", StringComparison.Ordinal)) == javaFileIds.Count,
            "fallback batch should emit structured parser_fallback diagnostics for each degraded Java file");
    }

    private static async Task WriteFixtureAsync(string repoRoot)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "com", "acme", "orders"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "com", "acme", "shared"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "csharp"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "ts"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "js"));

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "com", "acme", "orders", "OrderService.java"), """
        package com.acme.orders;

        import java.time.Instant;
        import java.util.List;
        import com.acme.shared.AuditLog;

        public class OrderService extends AbstractOrderService implements Workflow, Auditable {
            private final Repository repository;
            public int count;

            public OrderService(Repository repository) {
                this.repository = repository;
            }

            public OrderDto place(String id) {
                Order order = new Order(id);
                repository.save(order);
                this.count = this.count + 1;
                audit(order.id());
                List<String> markers = List.of(Instant.now().toString());
                return new OrderDto(order.id(), markers.size());
            }

            private void audit(String id) {
                AuditLog.record(id);
            }

            public static class NestedWorker {
                public void touch() {
                    AuditLog.record("nested");
                }
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "com", "acme", "orders", "Workflow.java"), """
        package com.acme.orders;

        public interface Workflow extends BaseWorkflow {
            void start();
        }

        interface Auditable {
            void audit(String id);
        }

        record OrderDto(String id, int markerCount) implements Payload {
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "com", "acme", "shared", "Payload.java"), """
        package com.acme.shared;

        public interface Payload {
        }

        public class AuditLog {
            public static void record(String message) {
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "csharp", "OrderBridge.cs"), """
        namespace Acme.Bridge;

        public class OrderBridge
        {
            public string Run(string id) => id;
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "ts", "app.ts"), """
        export class BatchApp {
            run(name: string): string {
                return name.trim();
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "js", "widget.js"), """
        export class Widget {
            render(name) {
                return name.toString();
            }
        }
        """);
    }

    private static bool HasEdge(
        IReadOnlyList<CodeEdgeFact> edges,
        string from,
        string to,
        string kind,
        string resolver) =>
        edges.Any(edge => edge.FromId == from
            && edge.ToId == to
            && edge.Kind == kind
            && edge.Resolver == resolver
            && Math.Abs(edge.Confidence - 0.9) < 1e-9);
}
