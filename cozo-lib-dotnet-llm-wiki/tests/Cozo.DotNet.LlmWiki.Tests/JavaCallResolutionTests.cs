using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class JavaCallResolutionTests
{
    public static async Task RunAsync(string root, Action<bool, string> assert)
    {
        var repoRoot = Path.Combine(root, "java-call-resolution-repo");
        await WriteFixtureAsync(repoRoot);

        var batch = await new RepositoryIndexer().BuildBatchAsync(new RepositoryIndexRequest(
            repoRoot,
            RepositoryId: "repo:java-call-resolution",
            RepositoryName: "java-call-resolution",
            UseGitIgnore: false,
            EnableRoslynEnhancement: false));
        var files = batch.Batch.Files ?? [];
        var symbols = batch.Batch.Symbols ?? [];
        var edges = batch.Batch.Edges ?? [];
        var diagnostics = batch.Batch.Diagnostics ?? [];

        assert(files.Count(file => file.Language == "java") >= 12,
            "Java call-resolution fixture should index a multi-package Java repository");
        assert(symbols.Any(symbol => symbol.SymKey == "java:fixture.typed.TypedConsumer#0" && symbol.Resolver == "treesitter"),
            "Java fixture sanity: typed consumer symbols should come from the tree-sitter path");

        string SymId(string symKey) => symbols.Single(symbol => symbol.SymKey == symKey).SymbolId;
        string FileId(string path) => files.Single(file => file.Path == path).FileId;

        var typedRunId = SymId("java:fixture.typed.TypedConsumer.run#1");
        var typedCtorId = SymId("java:fixture.typed.TypedConsumer.TypedConsumer#1");
        var workerPrepareId = SymId("java:fixture.typed.Worker.prepare#0");
        var workerFinishId = SymId("java:fixture.typed.Worker.finish#0");
        var typedStatusId = SymId("java:fixture.typed.TypedConsumer.status#0");

        assert(HasEdge(edges, typedCtorId, workerPrepareId, CodeEdgeKinds.Calls, 0.9, "binding:Worker", line: 9),
            "Java constructor parameter declared-type receiver should resolve ctorWorker.prepare() to Worker.prepare at 0.9 with binding evidence");
        assert(HasEdge(edges, typedRunId, workerPrepareId, CodeEdgeKinds.Calls, 0.9, "binding:Worker", line: 13),
            "Java field declared-type receiver should resolve fieldWorker.prepare() to Worker.prepare at 0.9 with binding evidence");
        assert(HasEdge(edges, typedRunId, workerPrepareId, CodeEdgeKinds.Calls, 0.9, "binding:Worker", line: 14),
            "Java method parameter declared-type receiver should resolve parameterWorker.prepare() to Worker.prepare at 0.9 with binding evidence");
        assert(HasEdge(edges, typedRunId, workerFinishId, CodeEdgeKinds.Calls, 0.9, "binding:Worker", line: 16),
            "Java local declared-type receiver should resolve localWorker.finish() to Worker.finish at 0.9 with binding evidence");
        assert(HasEdge(edges, typedRunId, typedStatusId, CodeEdgeKinds.Accesses, 0.9, "write", line: 17)
                && HasEdge(edges, typedRunId, typedStatusId, CodeEdgeKinds.Accesses, 0.9, "read", line: 18),
            "Java field writes and reads should produce ACCESSES edges with access-mode evidence and call-site lines");

        var alphaPickId = SymId("java:fixture.imports.alpha.Service.pick#0");
        var betaPickId = SymId("java:fixture.imports.beta.Service.pick#0");
        var gammaGoId = SymId("java:fixture.imports.gamma.StaticOps.go#0");
        var deltaGoId = SymId("java:fixture.imports.delta.StaticOps.go#0");
        var explicitRunId = SymId("java:fixture.imports.consumer.ExplicitUse.run#0");
        var wildcardRunId = SymId("java:fixture.imports.consumer.WildcardUse.run#0");
        var staticRunId = SymId("java:fixture.imports.consumer.StaticUse.run#0");

        assert(HasEdge(edges, explicitRunId, alphaPickId, CodeEdgeKinds.Calls, minConfidence: 0.7, evidenceContains: "import", line: 7),
            "Java explicit import should disambiguate Service.pick() to fixture.imports.alpha.Service.pick with import evidence");
        assert(!edges.Any(edge => edge.FromId == explicitRunId && edge.ToId == betaPickId && edge.Kind == CodeEdgeKinds.Calls),
            "Java explicit import must not deterministically mis-link Service.pick() to the same-name beta package");
        assert(HasEdge(edges, wildcardRunId, betaPickId, CodeEdgeKinds.Calls, minConfidence: 0.7, evidenceContains: "import", line: 7),
            "Java wildcard import should disambiguate Service.pick() to fixture.imports.beta.Service.pick with import evidence");
        assert(!edges.Any(edge => edge.FromId == wildcardRunId && edge.ToId == alphaPickId && edge.Kind == CodeEdgeKinds.Calls),
            "Java wildcard import must not fall back to the deterministic same-name alpha candidate");
        assert(HasEdge(edges, staticRunId, gammaGoId, CodeEdgeKinds.Calls, minConfidence: 0.7, evidenceContains: "import", line: 7),
            "Java static import should disambiguate go() to fixture.imports.gamma.StaticOps.go with import evidence");
        assert(!edges.Any(edge => edge.FromId == staticRunId && edge.ToId == deltaGoId && edge.Kind == CodeEdgeKinds.Calls),
            "Java static import must not deterministically mis-link go() to the same-name delta static method");

        var childExecuteId = SymId("java:fixture.derived.ChildAction.execute#0");
        var childOverrideId = SymId("java:fixture.derived.ChildAction.overrideMe#0");
        var baseTouchId = SymId("java:fixture.derived.BaseAction.touch#0");
        var baseOverrideId = SymId("java:fixture.derived.BaseAction.overrideMe#0");
        var taskExecuteId = SymId("java:fixture.derived.ActionTask.execute#0");

        assert(HasEdge(edges, childExecuteId, baseTouchId, CodeEdgeKinds.Calls, 0.9, line: 5),
            "Java super.touch() should resolve to the base class member at 0.9 on the call-site line");
        assert(HasEdge(edges, childOverrideId, baseOverrideId, CodeEdgeKinds.MethodOverrides, minConfidence: 0.9),
            "Java subclass method with matching name+arity should produce METHOD_OVERRIDES from child to base member");
        assert(HasEdge(edges, childExecuteId, taskExecuteId, CodeEdgeKinds.MethodImplements, minConfidence: 0.9),
            "Java interface implementation with matching name+arity should produce METHOD_IMPLEMENTS from class method to interface member");

        var unresolvedRunId = SymId("java:fixture.unresolved.ExternalUse.run#0");
        var illegalHiddenId = SymId("java:fixture.secrets.Secret.hidden#0");
        var unresolvedFileId = FileId("src/main/java/fixture/unresolved/ExternalUse.java");
        assert(!edges.Any(edge => edge.FromId == unresolvedRunId && edge.Kind == CodeEdgeKinds.Calls && edge.Line is 5 or 6),
            "Java repo-external and illegal private cross-file candidates should produce no CALLS edges");
        assert(!edges.Any(edge => edge.Kind == CodeEdgeKinds.Calls && edge.ToId == illegalHiddenId),
            "Java illegal private cross-file candidate must stay out of the fallback pool");
        assert(diagnostics.Any(diagnostic => diagnostic.Kind == "call_unresolved" && diagnostic.TargetId == unresolvedFileId),
            "Java unresolved call sites should emit the existing per-file call_unresolved diagnostic");

        assert(edges.Where(edge => edge.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Accesses
                or CodeEdgeKinds.MethodOverrides or CodeEdgeKinds.MethodImplements)
                .All(edge => edge.Resolver == "treesitter"),
            "Java resolved semantic edges should carry resolver=treesitter");
        assert(batch.CallSites == 10 && batch.ResolvedCalls == 8 && batch.UnresolvedCalls == 2,
            $"Java resolver counters should be callSites=10 resolved=8 unresolved=2 (got {batch.CallSites}/{batch.ResolvedCalls}/{batch.UnresolvedCalls})");
    }

    private static bool HasEdge(
        IReadOnlyList<CodeEdgeFact> edges,
        string from,
        string to,
        string kind,
        double? confidence = null,
        string? evidenceContains = null,
        int? line = null,
        double? minConfidence = null) =>
        edges.Any(edge => edge.FromId == from
            && edge.ToId == to
            && edge.Kind == kind
            && (confidence is null || Math.Abs(edge.Confidence - confidence.Value) < 1e-9)
            && (minConfidence is null || edge.Confidence >= minConfidence.Value)
            && (evidenceContains is null || edge.Evidence.Contains(evidenceContains, StringComparison.Ordinal))
            && (line is null || edge.Line == line.Value));

    private static async Task WriteFixtureAsync(string repoRoot)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "typed"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "alpha"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "beta"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "gamma"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "delta"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "consumer"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "derived"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "unresolved"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "main", "java", "fixture", "secrets"));

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "typed", "Worker.java"), """
        package fixture.typed;

        public class Worker {
            public void prepare() {
            }

            public void finish() {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "typed", "TypedConsumer.java"), """
        package fixture.typed;

        public class TypedConsumer {
            private final Worker fieldWorker;
            private int status;

            public TypedConsumer(Worker ctorWorker) {
                this.fieldWorker = ctorWorker;
                ctorWorker.prepare();
            }

            public void run(Worker parameterWorker) {
                fieldWorker.prepare();
                parameterWorker.prepare();
                Worker localWorker = parameterWorker;
                localWorker.finish();
                this.status = 1;
                int current = this.status;
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "alpha", "Service.java"), """
        package fixture.imports.alpha;

        public class Service {
            public static void pick() {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "beta", "Service.java"), """
        package fixture.imports.beta;

        public class Service {
            public static void pick() {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "gamma", "StaticOps.java"), """
        package fixture.imports.gamma;

        public class StaticOps {
            public static void go() {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "delta", "StaticOps.java"), """
        package fixture.imports.delta;

        public class StaticOps {
            public static void go() {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "consumer", "ExplicitUse.java"), """
        package fixture.imports.consumer;

        import fixture.imports.alpha.Service;

        public class ExplicitUse {
            public void run() {
                Service.pick();
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "consumer", "WildcardUse.java"), """
        package fixture.imports.consumer;

        import fixture.imports.beta.*;

        public class WildcardUse {
            public void run() {
                Service.pick();
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "imports", "consumer", "StaticUse.java"), """
        package fixture.imports.consumer;

        import static fixture.imports.gamma.StaticOps.go;

        public class StaticUse {
            public void run() {
                go();
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "derived", "BaseAction.java"), """
        package fixture.derived;

        public class BaseAction {
            public void touch() {
            }

            public void overrideMe() {
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "derived", "ActionTask.java"), """
        package fixture.derived;

        public interface ActionTask {
            void execute();
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "derived", "ChildAction.java"), """
        package fixture.derived;

        public class ChildAction extends BaseAction implements ActionTask {
            public void execute() {
                super.touch();
            }

            public void overrideMe() {
            }
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "unresolved", "ExternalUse.java"), """
        package fixture.unresolved;

        public class ExternalUse {
            public void run() {
                ExternalApi.missing();
                hidden();
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "src", "main", "java", "fixture", "secrets", "Secret.java"), """
        package fixture.secrets;

        public class Secret {
            private static void hidden() {
            }
        }
        """);
    }
}
