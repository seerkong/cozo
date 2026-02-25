using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// Modeling context budget ranking suite (fix-wiki-fractal-entry-and-context-ranking track
/// T2.1, delta behavior://dotnet-llm-wiki/requirements/context-budget-ranking): the
/// MaxModelingContexts truncation ranks by importance — public-API member count desc →
/// total member count desc → name asc (Ordinal) — instead of raw size. Cases:
/// public-api-outranks-size (a small context with public API beats a bigger all-internal
/// one at budget 1; the loser is disclosed in diagnostics) and deterministic-tiebreak
/// (equal public/member counts fall back to name asc, stable across repeated builds).
/// Pure structure layer — no LLM, pinned communities.
/// </summary>
internal static class FractalWikiContextRankingTests
{
    /// <summary>
    /// Fixture with four namespace-prefix contexts competing for the budget:
    /// - Acme.Core: 2 members, 1 public (Gateway) — high importance despite small size;
    /// - Demo.Playground: 4 members, all internal (public API = 0) — big but unimportant;
    /// - Alpha.One / Beta.Two: 2 internal members each (public = 0) — exact tie pair, only
    ///   the Ordinal name ascends can separate them.
    /// </summary>
    private static async Task<CozoOm> BuildFixtureAsync(CozoDb db)
    {
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Files:
            [
                new CodeFileFact("file:rk:core", "repo:rk", "src/Core.cs"),
                new CodeFileFact("file:rk:demo", "repo:rk", "demo/Playground.cs"),
                new CodeFileFact("file:rk:tie", "repo:rk", "src/Tie.cs")
            ],
            Symbols:
            [
                // Acme.Core: Gateway is the only public-API symbol in the whole fixture.
                new CodeSymbolFact("symbol:rk:core:gw", "file:rk:core", "Gateway", "class", 3, 20, "Gateway",
                    Visibility: "public", Exported: true, SymKey: "csharp:Acme.Core.Gateway#0"),
                new CodeSymbolFact("symbol:rk:core:helper", "file:rk:core", "CoreHelper", "class", 22, 30, "CoreHelper",
                    SymKey: "csharp:Acme.Core.CoreHelper#0"),
                // Demo.Playground: more members than any other context, zero public API.
                new CodeSymbolFact("symbol:rk:demo:a", "file:rk:demo", "DemoA", "class", 3, 10, "DemoA",
                    SymKey: "csharp:Demo.Playground.DemoA#0"),
                new CodeSymbolFact("symbol:rk:demo:b", "file:rk:demo", "DemoB", "class", 12, 20, "DemoB",
                    SymKey: "csharp:Demo.Playground.DemoB#0"),
                new CodeSymbolFact("symbol:rk:demo:c", "file:rk:demo", "DemoC", "class", 22, 30, "DemoC",
                    SymKey: "csharp:Demo.Playground.DemoC#0"),
                new CodeSymbolFact("symbol:rk:demo:d", "file:rk:demo", "DemoD", "class", 32, 40, "DemoD",
                    SymKey: "csharp:Demo.Playground.DemoD#0"),
                // Tie pair: identical public count (0) and member count (2) — name asc decides.
                new CodeSymbolFact("symbol:rk:tie:a1", "file:rk:tie", "OneA", "class", 3, 10, "OneA",
                    SymKey: "csharp:Alpha.One.OneA#0"),
                new CodeSymbolFact("symbol:rk:tie:a2", "file:rk:tie", "OneB", "class", 12, 20, "OneB",
                    SymKey: "csharp:Alpha.One.OneB#0"),
                new CodeSymbolFact("symbol:rk:tie:b1", "file:rk:tie", "TwoA", "class", 22, 30, "TwoA",
                    SymKey: "csharp:Beta.Two.TwoA#0"),
                new CodeSymbolFact("symbol:rk:tie:b2", "file:rk:tie", "TwoB", "class", 32, 40, "TwoB",
                    SymKey: "csharp:Beta.Two.TwoB#0")
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:rk:core:gw", "symbol:rk:core:helper", CodeEdgeKinds.Calls, "file:rk:core", 5, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:rk:demo:a", "symbol:rk:demo:b", CodeEdgeKinds.Calls, "file:rk:demo", 5, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:rk:demo:c", "symbol:rk:demo:d", CodeEdgeKinds.Calls, "file:rk:demo", 25, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:rk:tie:a1", "symbol:rk:tie:a2", CodeEdgeKinds.Calls, "file:rk:tie", 5, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:rk:tie:b1", "symbol:rk:tie:b2", CodeEdgeKinds.Calls, "file:rk:tie", 25, 0.9, "roslyn", "")
            ]));
        using (db.Run(
            """
            ?[community_id, label, cohesion, symbol_count, algo] <- [
              ["community:rk1", "Acme.Core", 0.8, 2, "louvain"],
              ["community:rk2", "Demo.Playground", 0.8, 4, "louvain"],
              ["community:rk3", "Alpha.One", 0.7, 2, "louvain"],
              ["community:rk4", "Beta.Two", 0.7, 2, "louvain"]]
            :put ck_community {community_id => label, cohesion, symbol_count, algo}
            """)) { }
        using (db.Run(
            """
            ?[symbol_id, community_id] <- [
              ["symbol:rk:core:gw", "community:rk1"], ["symbol:rk:core:helper", "community:rk1"],
              ["symbol:rk:demo:a", "community:rk2"], ["symbol:rk:demo:b", "community:rk2"],
              ["symbol:rk:demo:c", "community:rk2"], ["symbol:rk:demo:d", "community:rk2"],
              ["symbol:rk:tie:a1", "community:rk3"], ["symbol:rk:tie:a2", "community:rk3"],
              ["symbol:rk:tie:b1", "community:rk4"], ["symbol:rk:tie:b2", "community:rk4"]]
            :put ck_member {symbol_id => community_id}
            """)) { }
        return om;
    }

    private static string[] ContextSlugs(string outDir)
        => Directory.GetDirectories(Path.Combine(outDir, "modeling", "domain", "contexts"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fractal-rank-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = await BuildFixtureAsync(db);
            var pipeline = new FractalWikiPipeline();

            // ---- case public-api-outranks-size: budget 1 — Acme.Core (1 public member) must
            // beat Demo.Playground (4 members, 0 public); the loser lands in diagnostics.
            var oneDir = Path.Combine(root, "budget-1");
            var one = await pipeline.BuildAsync(om, new FractalWikiOptions(
                OutputDirectory: oneDir, MaxModelingContexts: 1));
            var oneSlugs = ContextSlugs(oneDir);
            assert(oneSlugs.SequenceEqual(new[] { "acme-core" }),
                "public-api-outranks-size: budget 1 should keep the public-API context Acme.Core, "
                + "not the bigger all-internal Demo.Playground (got: " + string.Join(", ", oneSlugs) + ")");
            var dropDiagnostic = one.Diagnostics.FirstOrDefault(d =>
                d.Contains("MaxModelingContexts", StringComparison.Ordinal));
            assert(dropDiagnostic is not null
                    && dropDiagnostic.Contains("3", StringComparison.Ordinal)
                    && dropDiagnostic.Contains("Demo.Playground", StringComparison.Ordinal),
                "public-api-outranks-size: the drop diagnostic should count 3 dropped contexts and "
                + "name Demo.Playground (got: " + (dropDiagnostic ?? "<none>") + ")");
            assert(dropDiagnostic is not null
                    && dropDiagnostic.Contains("public-api member count desc", StringComparison.Ordinal),
                "public-api-outranks-size: the drop diagnostic should state the ranking basis "
                + "(got: " + (dropDiagnostic ?? "<none>") + ")");

            // ---- case deterministic-tiebreak: budget 3 — after Acme.Core (public) and
            // Demo.Playground (bigger), the exact-tie pair Alpha.One/Beta.Two is separated by
            // Ordinal name asc: Alpha.One in, Beta.Two out; repeated builds are stable.
            var expectedAtThree = new[] { "acme-core", "alpha-one", "demo-playground" };
            var threeDirA = Path.Combine(root, "budget-3-a");
            var threeA = await pipeline.BuildAsync(om, new FractalWikiOptions(
                OutputDirectory: threeDirA, MaxModelingContexts: 3));
            var threeSlugsA = ContextSlugs(threeDirA);
            assert(threeSlugsA.SequenceEqual(expectedAtThree),
                "deterministic-tiebreak: budget 3 should keep acme-core/demo-playground and break the "
                + "Alpha.One/Beta.Two tie by name asc (got: " + string.Join(", ", threeSlugsA) + ")");
            var tieDropA = threeA.Diagnostics.FirstOrDefault(d =>
                d.Contains("MaxModelingContexts", StringComparison.Ordinal));
            assert(tieDropA is not null && tieDropA.Contains("Beta.Two", StringComparison.Ordinal)
                    && !tieDropA.Contains("Alpha.One", StringComparison.Ordinal),
                "deterministic-tiebreak: only Beta.Two should be dropped at budget 3 "
                + "(got: " + (tieDropA ?? "<none>") + ")");

            var threeDirB = Path.Combine(root, "budget-3-b");
            var threeB = await pipeline.BuildAsync(om, new FractalWikiOptions(
                OutputDirectory: threeDirB, MaxModelingContexts: 3));
            assert(ContextSlugs(threeDirB).SequenceEqual(threeSlugsA),
                "deterministic-tiebreak: a repeated build must select the identical context set");
            var tieDropB = threeB.Diagnostics.FirstOrDefault(d =>
                d.Contains("MaxModelingContexts", StringComparison.Ordinal));
            assert(tieDropA == tieDropB,
                "deterministic-tiebreak: the drop diagnostic must be verbatim-stable across builds "
                + "(first: " + (tieDropA ?? "<none>") + "; second: " + (tieDropB ?? "<none>") + ")");
            var contextsIndexA = await File.ReadAllTextAsync(
                Path.Combine(threeDirA, "modeling", "domain", "contexts", "index.md"));
            var contextsIndexB = await File.ReadAllTextAsync(
                Path.Combine(threeDirB, "modeling", "domain", "contexts", "index.md"));
            assert(contextsIndexA == contextsIndexB,
                "deterministic-tiebreak: contexts/index.md must be byte-identical across repeated builds");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
