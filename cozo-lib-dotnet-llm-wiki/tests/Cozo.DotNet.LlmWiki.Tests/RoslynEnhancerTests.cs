namespace Cozo.DotNet.LlmWiki.Tests;

// Inside the namespace scope so the alias wins over the sibling RoslynEnhancer *namespace*.
using Cozo.DotNet.LlmWiki.RoslynEnhancer;
using RoslynEnhancer = Cozo.DotNet.LlmWiki.RoslynEnhancer.RoslynEnhancer;

/// <summary>
/// Fixed-sample tests for the RoslynEnhancer capsule (roslyn-csharp-resolution track T1.1,
/// delta cases: semantic-call-edge / candidate-degrades / repo-external-not-edged).
/// Covers: cross-file semantic hit 1.0, explicit + implicit object creation, overload
/// disambiguation, candidate degradation 0.6 + CandidateReason (stable CS0121 ambiguity),
/// BCL external counted-only, nameof(...) excluded, caller attribution for methods /
/// property accessors / top-level statements, and RoslynSymbolInfo sym_key equivalence.
/// </summary>
internal static class RoslynEnhancerTests
{
    private const string AlphaSource = """
        namespace Fixture.App;

        public class Alpha
        {
            public void Run()
            {
                var beta = new Beta();
                Beta other = new();
                beta.Ping();
                other.Ping(42);
                Console.WriteLine("hello");
                var name = nameof(Beta);
                Ambiguous(1, 2);
            }

            public string Tag => Helper();

            private static string Helper() => "tag";

            private static void Ambiguous(int a, double b) { }

            private static void Ambiguous(double a, int b) { }
        }
        """;

    private const string BetaSource = """
        namespace Fixture.App;

        public class Beta
        {
            public void Ping()
            {
            }

            public void Ping(int value)
            {
            }
        }
        """;

    private const string MainSource = """
        using Fixture.App;

        var alpha = new Alpha();
        alpha.Run();
        """;

    public static void Run(Action<bool, string> assert)
    {
        var result = RoslynEnhancer.Analyze(new[]
        {
            ("src/Alpha.cs", AlphaSource),
            ("src/Beta.cs", BetaSource),
            ("src/Main.cs", MainSource),
        });

        var edges = result.Edges;
        const string alphaRun = "M:Fixture.App.Alpha.Run";

        // semantic-call-edge: in-repo semantic hits resolve at confidence 1.0 with semantic evidence.
        assert(edges.Any(e => e.CallerDocId == alphaRun && e.CalleeDocId == "M:Fixture.App.Beta.#ctor"
                && e.Confidence == 1.0 && e.Evidence == "semantic" && e.File == "src/Alpha.cs" && e.Line == 7 && e.CalleeIsInSource),
            "explicit object creation should produce a 1.0 semantic edge to the (synthesized) Beta ctor at line 7");
        assert(edges.Any(e => e.CallerDocId == alphaRun && e.CalleeDocId == "M:Fixture.App.Beta.#ctor" && e.Confidence == 1.0 && e.Line == 8),
            "implicit object creation (target-typed new) should produce a 1.0 semantic edge at line 8");
        assert(edges.Any(e => e.CallerDocId == alphaRun && e.CalleeDocId == "M:Fixture.App.Beta.Ping" && e.Confidence == 1.0 && e.Line == 9),
            "zero-arg overload call should resolve semantically to Ping() at 1.0");
        assert(edges.Any(e => e.CallerDocId == alphaRun && e.CalleeDocId == "M:Fixture.App.Beta.Ping(System.Int32)" && e.Confidence == 1.0 && e.Line == 10),
            "int-arg overload call should resolve semantically to Ping(System.Int32) at 1.0");

        // repo-external-not-edged: BCL Console.WriteLine is counted only, never edged.
        assert(!edges.Any(e => e.File == "src/Alpha.cs" && e.Line == 11),
            "BCL call (Console.WriteLine) must not produce an edge");
        assert(result.ExternalCalls == 1,
            $"exactly one external (BCL) call should be counted (got {result.ExternalCalls})");

        // nameof(...) is not an invocation: no edge, no unknown count.
        assert(!edges.Any(e => e.File == "src/Alpha.cs" && e.Line == 12),
            "nameof(...) must not produce an edge");
        assert(result.UnknownCalls == 0,
            $"nameof must not be counted as an unknown call (got {result.UnknownCalls})");

        // candidate-degrades: CS0121 ambiguous overloads yield CandidateSymbols → 0.6 + CandidateReason evidence.
        var candidate = edges.Single(e => e.File == "src/Alpha.cs" && e.Line == 13);
        assert(candidate.Confidence == 0.6, "ambiguous overload call should degrade to confidence 0.6");
        assert(candidate.Evidence.Contains("OverloadResolutionFailure", StringComparison.Ordinal),
            $"candidate edge evidence should carry the CandidateReason (got '{candidate.Evidence}')");
        assert(candidate.CallerDocId == alphaRun && candidate.CalleeDocId.StartsWith("M:Fixture.App.Alpha.Ambiguous(", StringComparison.Ordinal),
            "candidate edge should pick a deterministic first candidate as callee");

        // caller attribution: expression-bodied property → getter accessor DocId.
        assert(edges.Any(e => e.CallerDocId == "M:Fixture.App.Alpha.get_Tag" && e.CalleeDocId == "M:Fixture.App.Alpha.Helper" && e.Confidence == 1.0),
            "call inside an expression-bodied property should be attributed to the get accessor DocId");

        // caller attribution: top-level statements → the synthesized container method DocId.
        var mainEdges = edges.Where(e => e.File == "src/Main.cs").ToArray();
        assert(mainEdges.Length == 2, $"top-level statements should yield exactly 2 edges (got {mainEdges.Length})");
        assert(mainEdges.All(e => !string.IsNullOrEmpty(e.CallerDocId) && e.CallerDocId.Contains("Main", StringComparison.Ordinal))
                && mainEdges.Select(e => e.CallerDocId).Distinct().Count() == 1,
            "top-level statement calls should share the synthesized entry-point method DocId as caller");
        assert(mainEdges.Any(e => e.CalleeDocId == "M:Fixture.App.Alpha.#ctor" && e.Confidence == 1.0)
                && mainEdges.Any(e => e.CalleeDocId == "M:Fixture.App.Alpha.Run" && e.Confidence == 1.0),
            "top-level statements should edge to Alpha ctor and Alpha.Run at 1.0");

        assert(edges.Count == 8, $"fixture should produce exactly 8 edges (got {edges.Count})");

        // RoslynSymbolInfo: DocId + sym_key equivalence (lang=csharp, namespace-qualified name, arity) + 1-based lines.
        var beta = result.Symbols.Single(s => s.DocId == "T:Fixture.App.Beta");
        assert(beta.SymKey == "csharp:Fixture.App.Beta#0" && beta.Qualified == "Fixture.App.Beta" && beta.Arity == 0,
            "type symbol should carry sym_key csharp:<qualified>#0");
        assert(beta.File == "src/Beta.cs" && beta.StartLine == 3 && beta.EndLine == 12,
            $"Beta type symbol should span lines 3..12 1-based (got {beta.StartLine}..{beta.EndLine})");

        var pingInt = result.Symbols.Single(s => s.DocId == "M:Fixture.App.Beta.Ping(System.Int32)");
        assert(pingInt.SymKey == "csharp:Fixture.App.Beta.Ping#1" && pingInt.Arity == 1,
            "method symbol arity should be the parameter count");
        assert(pingInt.StartLine == 9 && pingInt.EndLine == 11,
            $"Ping(int) should span lines 9..11 1-based (got {pingInt.StartLine}..{pingInt.EndLine})");

        var tag = result.Symbols.Single(s => s.Qualified == "Fixture.App.Alpha.Tag");
        assert(tag.DocId == "P:Fixture.App.Alpha.Tag" && tag.Arity == 0,
            "property symbol should carry the P: DocId with arity 0");

        assert(result.Symbols.All(s => s.SymKey.StartsWith("csharp:", StringComparison.Ordinal)),
            "every symbol sym_key should use the csharp: lang prefix");
    }
}
