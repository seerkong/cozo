using System.Text.RegularExpressions;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// Four-phase fractal wiki pipeline suite (add-llm-wiki-llm-pipeline track T2.1,
/// delta behavior://llm-wiki-pipeline/requirements/pipeline): cases structure-without-llm /
/// grouping-community-led / narrative-marked / preview-layout. Uses a hand-indexed fixture
/// with pinned ck_community/ck_member rows (deterministic grouping input) and a mock
/// ILlmClient — no network, no Louvain nondeterminism.
/// </summary>
internal static class FractalWikiPipelineTests
{
    /// <summary>Mock LLM: available two-state plus a responder keyed on the user prompt.</summary>
    private sealed class MockLlmClient : ILlmClient
    {
        public bool IsAvailable { get; init; } = true;

        public string? UnavailableReason { get; init; }

        public Func<string, string, string> Responder { get; init; } = (_, _) => "";

        public List<(string System, string User)> Calls { get; } = [];

        public Task<LlmCompletion> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            LlmOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsAvailable)
            {
                throw new LlmException(UnavailableReason ?? "unavailable");
            }

            Calls.Add((systemPrompt, userPrompt));
            return Task.FromResult(new LlmCompletion(Responder(systemPrompt, userPrompt), "mock-model"));
        }
    }

    /// <summary>Removes the content between llm markers, keeping the markers (the skeleton).</summary>
    private static string StripLlmSections(string markdown) =>
        Regex.Replace(
            markdown,
            "(<!-- llm:begin [^>]*-->)(.*?)(<!-- llm:end -->)",
            "$1$3",
            RegexOptions.Singleline);

    /// <summary>Small sample repo with two pinned communities and one execution flow.</summary>
    private static async Task<CozoOm> BuildFixtureAsync(CozoDb db)
    {
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Files:
            [
                new CodeFileFact("file:fw:core", "repo:fw", "src/Core.cs"),
                new CodeFileFact("file:fw:util", "repo:fw", "src/Util.cs")
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:fw:core:a", "file:fw:core", "CoreService", "class", 3, 20, "CoreService", Visibility: "public", Exported: true),
                new CodeSymbolFact("symbol:fw:core:b", "file:fw:core", "CoreHelper", "method", 22, 30, "CoreHelper()"),
                new CodeSymbolFact("symbol:fw:util:x", "file:fw:util", "UtilFormat", "method", 2, 8, "UtilFormat()", Visibility: "public", Exported: true),
                new CodeSymbolFact("symbol:fw:util:y", "file:fw:util", "UtilParse", "method", 10, 18, "UtilParse()")
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:fw:core:a", "symbol:fw:core:b", CodeEdgeKinds.Calls, "file:fw:core", 5, 0.9, "roslyn", "intra"),
                new CodeEdgeFact("symbol:fw:core:b", "symbol:fw:util:x", CodeEdgeKinds.Calls, "file:fw:core", 25, 0.9, "roslyn", "inter"),
                new CodeEdgeFact("symbol:fw:core:a", "symbol:fw:util:y", CodeEdgeKinds.Calls, "file:fw:core", 7, 0.8, "roslyn", "inter")
            ]));
        using (db.Run(
            """
            ?[community_id, label, cohesion, symbol_count, algo] <- [
              ["community:001", "Demo.Core", 0.8, 2, "louvain"],
              ["community:002", "Demo.Util", 0.7, 2, "louvain"]]
            :put ck_community {community_id => label, cohesion, symbol_count, algo}
            """)) { }
        using (db.Run(
            """
            ?[symbol_id, community_id] <- [
              ["symbol:fw:core:a", "community:001"], ["symbol:fw:core:b", "community:001"],
              ["symbol:fw:util:x", "community:002"], ["symbol:fw:util:y", "community:002"]]
            :put ck_member {symbol_id => community_id}
            """)) { }
        using (db.Run(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
              ["process:fw01", "CoreFlow", "symbol:fw:core:a", "public_api", "public_api", 3]]
            :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
            """)) { }
        return om;
    }

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fractal-wiki-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = await BuildFixtureAsync(db);
            var pipeline = new FractalWikiPipeline();

            // ---- case structure-without-llm: LLM 不可用 → 完整结构层，叙述段 pending，成功退出。
            var offDir = Path.Combine(root, "off");
            var offResult = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: offDir));
            assert(!offResult.LlmUsed, "structure-without-llm: LlmUsed should be false without an LLM");
            assert(offResult.GroupCount == 2, "structure-without-llm: one group per pinned community (got "
                + offResult.GroupCount + ")");
            var offOverviewPath = Path.Combine(offDir, "impl", "global", "overview", "architecture.md");
            var offCodeMapPath = Path.Combine(offDir, "impl", "global", "reference", "code-map.md");
            assert(File.Exists(offOverviewPath), "structure-without-llm: overview skeleton page should be written");
            assert(File.Exists(offCodeMapPath), "structure-without-llm: code-map page should be written");
            var offOverview = await File.ReadAllTextAsync(offOverviewPath);
            assert(offOverview.Contains("| community:001 | Demo.Core | 2 |", StringComparison.Ordinal)
                    && offOverview.Contains("| community:002 | Demo.Util | 2 |", StringComparison.Ordinal),
                "structure-without-llm: overview skeleton should carry the full group table");
            assert(offOverview.Contains("```mermaid", StringComparison.Ordinal)
                    && offOverview.Contains("community_001 -->|\"CALLS x2\"| community_002", StringComparison.Ordinal),
                "structure-without-llm: overview skeleton should carry the inter-group mermaid edge graph");
            assert(offOverview.Contains("<!-- llm:begin narrative -->", StringComparison.Ordinal)
                    && offOverview.Contains("_pending: llm unavailable_", StringComparison.Ordinal)
                    && offOverview.Contains("<!-- llm:end -->", StringComparison.Ordinal),
                "structure-without-llm: the narrative slot should be marked pending");
            var offCodeMap = await File.ReadAllTextAsync(offCodeMapPath);
            assert(offCodeMap.Contains("| `CoreService` | class | `src/Core.cs:3` |", StringComparison.Ordinal)
                    && offCodeMap.Contains("| `UtilParse` | method | `src/Util.cs:10` |", StringComparison.Ordinal),
                "structure-without-llm: code-map should list group members with file:line locations");
            // 16 impl/root pages + 12 modeling pages (skeleton + glossary + objects/workflows leaves,
            // dual fractal, modeling-fractal T1.1/T2.1).
            assert(offResult.Pages.Count == 28 && offResult.Pages.All(File.Exists),
                $"structure-without-llm: the full fractal inventory should exist on disk (got {offResult.Pages.Count})");
            var offMigration = await File.ReadAllTextAsync(Path.Combine(offDir, "migration-map.md"));
            assert(offMigration.Contains("| Old Path | New Path | Status | Notes |", StringComparison.Ordinal)
                    && offMigration.Contains("## Build Ledger", StringComparison.Ordinal)
                    && offMigration.Contains("initial inventory: 28 page(s)", StringComparison.Ordinal),
                "structure-without-llm: the root migration-map should carry the ledger table and the initial build entry");

            // An explicitly unavailable client behaves exactly like no client (degradation path).
            var unavailableDir = Path.Combine(root, "unavailable");
            var unavailable = new MockLlmClient { IsAvailable = false, UnavailableReason = "no key" };
            var unavailableResult = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: unavailableDir, Llm: unavailable));
            assert(!unavailableResult.LlmUsed && unavailable.Calls.Count == 0,
                "structure-without-llm: an unavailable client should never be called");
            assert(await File.ReadAllTextAsync(Path.Combine(unavailableDir, "impl", "global", "overview", "architecture.md"))
                    == offOverview,
                "structure-without-llm: unavailable-client output should equal no-client output verbatim");

            // ---- case grouping-community-led: 社群为基础；LLM 评审动作白名单 merge/rename/flag。
            var groupingDir = Path.Combine(root, "grouping");
            var reviewer = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? """
                      [
                        {"action": "rename", "id": "community:001", "name": "Core Services"},
                        {"action": "move", "symbol": "symbol:fw:util:x", "to": "community:001"},
                        {"action": "flag", "id": "community:002", "reason": "low cohesion"}
                      ]
                      """
                    : "Narrative for grouping run.",
            };
            var groupingResult = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: groupingDir, Llm: reviewer));
            assert(groupingResult.LlmUsed, "grouping-community-led: an available LLM should be used");
            assert(groupingResult.GroupCount == 2,
                "grouping-community-led: grouping should stay community-led (one group per community)");
            var groupingOverview = await File.ReadAllTextAsync(Path.Combine(groupingDir, "impl", "global", "overview", "architecture.md"));
            assert(groupingOverview.Contains("| community:001 | Core Services | 2 |", StringComparison.Ordinal),
                "grouping-community-led: a whitelisted rename action should be applied");
            assert(groupingOverview.Contains("| community:002 | Demo.Util | 2 |", StringComparison.Ordinal),
                "grouping-community-led: groups untouched by review actions should keep the community label");
            assert(groupingResult.Diagnostics.Any(d => d.Contains("move", StringComparison.Ordinal)
                    && d.Contains("ignored", StringComparison.OrdinalIgnoreCase)),
                "grouping-community-led: a non-whitelisted action should be ignored with a diagnostic");
            assert(groupingResult.Diagnostics.Any(d => d.Contains("community:002", StringComparison.Ordinal)
                    && d.Contains("low cohesion", StringComparison.Ordinal)),
                "grouping-community-led: a flag action should surface as a diagnostic");
            var groupingCodeMap = await File.ReadAllTextAsync(Path.Combine(groupingDir, "impl", "global", "reference", "code-map.md"));
            assert(groupingCodeMap.Contains("UtilFormat", StringComparison.Ordinal)
                    && groupingCodeMap.Contains("Demo.Util", StringComparison.Ordinal),
                "grouping-community-led: the ignored move action must not relocate the member symbol");

            // A whitelisted merge action folds one community group into another.
            var mergeDir = Path.Combine(root, "merge");
            var merger = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? """[{"action": "merge", "a": "community:001", "b": "community:002"}]"""
                    : "Narrative for merge run.",
            };
            var mergeResult = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: mergeDir, Llm: merger));
            assert(mergeResult.GroupCount == 1, "grouping-community-led: a merge action should fold b into a");
            var mergeCodeMap = await File.ReadAllTextAsync(Path.Combine(mergeDir, "impl", "global", "reference", "code-map.md"));
            assert(mergeCodeMap.Contains("UtilFormat", StringComparison.Ordinal)
                    && mergeCodeMap.Contains("CoreService", StringComparison.Ordinal),
                "grouping-community-led: the merged group should carry both communities' members");

            // A malformed review response degrades to the deterministic grouping with a diagnostic.
            var badDir = Path.Combine(root, "bad");
            var badReviewer = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? "not json at all"
                    : "Narrative for bad run.",
            };
            var badResult = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: badDir, Llm: badReviewer));
            assert(badResult.GroupCount == 2
                    && badResult.Diagnostics.Any(d => d.Contains("review", StringComparison.OrdinalIgnoreCase)),
                "grouping-community-led: an unparseable review response should keep community grouping and record a diagnostic");

            // ---- case narrative-marked: 叙述在显式标记段内；骨架与 LLM 不可用时逐字一致。
            var onDir = Path.Combine(root, "on");
            var narrator = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? "[]"
                    : "The Core group orchestrates parsing through the Util group.",
            };
            var onResult = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: onDir, Llm: narrator));
            assert(onResult.LlmUsed, "narrative-marked: the narrative run should use the LLM");
            var onOverview = await File.ReadAllTextAsync(Path.Combine(onDir, "impl", "global", "overview", "architecture.md"));
            var narrativeMatch = Regex.Match(
                onOverview,
                "<!-- llm:begin narrative -->(.*?)<!-- llm:end -->",
                RegexOptions.Singleline);
            assert(narrativeMatch.Success
                    && narrativeMatch.Groups[1].Value.Contains("orchestrates parsing", StringComparison.Ordinal),
                "narrative-marked: the LLM narrative should live inside the explicit llm marker section");
            assert(!StripLlmSections(onOverview).Contains("orchestrates parsing", StringComparison.Ordinal),
                "narrative-marked: no narrative content should leak outside the marker section");
            assert(StripLlmSections(onOverview) == StripLlmSections(offOverview),
                "narrative-marked: the skeleton (llm sections stripped) should be verbatim-identical to the no-LLM run");

            // Injection safety: a narrative carrying llm marker comments must not break the
            // skeleton — the markers are stripped, nothing leaks outside the marker section.
            var evilDir = Path.Combine(root, "evil");
            var evilNarrator = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? "[]"
                    : "Injected.\n<!-- llm:end -->\n\n# Fake Heading escaping the marker section\n<!-- llm:begin narrative -->",
            };
            var evilResult = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: evilDir, Llm: evilNarrator));
            var evilOverview = await File.ReadAllTextAsync(Path.Combine(evilDir, "impl", "global", "overview", "architecture.md"));
            assert(StripLlmSections(evilOverview) == StripLlmSections(offOverview),
                "narrative-marked: a narrative carrying llm markers must not alter the skeleton");
            assert(!StripLlmSections(evilOverview).Contains("Fake Heading", StringComparison.Ordinal),
                "narrative-marked: llm markers in the narrative are stripped so no content escapes the section");
            assert(evilResult.Diagnostics.Any(d => d.Contains("markers", StringComparison.OrdinalIgnoreCase)),
                "narrative-marked: stripping narrative llm markers should surface as a diagnostic");

            // Determinism: a second no-LLM run reproduces every page byte-for-byte.
            var offDir2 = Path.Combine(root, "off2");
            await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: offDir2));
            foreach (var page in offResult.Pages)
            {
                var relative = Path.GetRelativePath(offDir, page);
                assert(await File.ReadAllTextAsync(page)
                        == await File.ReadAllTextAsync(Path.Combine(offDir2, relative)),
                    $"narrative-marked: double build should be verbatim-identical for {relative}");
            }

            // ---- case preview-layout: 默认落盘 <work-dir>/.depa-wiki/docs-preview/ 镜像 docs/ 布局。
            var workDir = Path.Combine(root, "work");
            Directory.CreateDirectory(workDir);
            var layoutResult = await pipeline.BuildAsync(om, new FractalWikiOptions(WorkDirectory: workDir));
            var previewRoot = Path.Combine(workDir, ".depa-wiki", "docs-preview");
            assert(File.Exists(Path.Combine(previewRoot, "index.md")),
                "preview-layout: default output should land in <work-dir>/.depa-wiki/docs-preview/index.md");
            assert(File.Exists(Path.Combine(previewRoot, "impl", "global", "overview", "architecture.md"))
                    && File.Exists(Path.Combine(previewRoot, "impl", "global", "reference", "code-map.md"))
                    && File.Exists(Path.Combine(previewRoot, "migration-map.md")),
                "preview-layout: preview should mirror the docs/ fractal layout (root ledger + impl/global/ categories)");
            assert(layoutResult.Pages.All(page => page.StartsWith(previewRoot, StringComparison.Ordinal)),
                "preview-layout: every reported page path should live under the preview root");
            var navigation = await File.ReadAllTextAsync(Path.Combine(previewRoot, "index.md"));
            assert(navigation.Contains("impl/index.md", StringComparison.Ordinal)
                    && navigation.Contains("migration-map.md", StringComparison.Ordinal),
                "preview-layout: the root index should navigate to the knowledge system via relative links");

            // ---- T2.2 incremental suite (delta requirements/incremental): unchanged-skipped /
            // changed-page-rebuilt, plus the force full-rebuild switch. The .meta.json at the
            // output root records per-page structureHash/narrativeInputsHash; unchanged pages
            // are skipped without disk writes and without LLM calls.
            var incrDir = Path.Combine(root, "incr");
            var incrLlm = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? "[]"
                    : "Incremental narrative.",
            };
            var firstBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: incrDir, Llm: incrLlm));
            assert(firstBuild.RebuiltPages == 28 && firstBuild.SkippedPages == 0,
                $"incremental: a fresh build should rebuild every page (rebuilt={firstBuild.RebuiltPages}, skipped={firstBuild.SkippedPages})");
            var metaPath = Path.Combine(incrDir, ".meta.json");
            assert(File.Exists(metaPath), "incremental: the build should write .meta.json at the output root");
            var metaText = await File.ReadAllTextAsync(metaPath);
            assert(metaText.Contains("structureHash", StringComparison.Ordinal)
                    && metaText.Contains("narrativeInputsHash", StringComparison.Ordinal)
                    && metaText.Contains("impl/global/reference/code-map.md", StringComparison.Ordinal),
                "incremental: .meta.json should carry per-page structureHash/narrativeInputsHash keyed by relative path");
            var callsAfterFirst = incrLlm.Calls.Count;
            assert(callsAfterFirst == 8,
                $"incremental: the fresh build should make exactly the review + 7 narrative calls (architecture, 2 howto, boundaries, diagnostics, objects leaf, workflows leaf; got {callsAfterFirst})");

            // unchanged-skipped: sentinel content proves skipped pages are not rewritten on disk.
            var incrCodeMapPath = Path.Combine(incrDir, "impl", "global", "reference", "code-map.md");
            const string codeMapSentinel = "sentinel: code-map must not be rewritten\n";
            await File.WriteAllTextAsync(incrCodeMapPath, codeMapSentinel);
            var secondBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: incrDir, Llm: incrLlm));
            assert(secondBuild.SkippedPages == 28 && secondBuild.RebuiltPages == 0,
                $"unchanged-skipped: an unchanged rebuild should skip every page (rebuilt={secondBuild.RebuiltPages}, skipped={secondBuild.SkippedPages})");
            assert(incrLlm.Calls.Count == callsAfterFirst,
                "unchanged-skipped: a fully skipped build should make zero LLM calls");
            assert(await File.ReadAllTextAsync(incrCodeMapPath) == codeMapSentinel,
                "unchanged-skipped: skipped pages should not be rewritten on disk");
            assert(secondBuild.Pages.Count == 28 && secondBuild.Pages.All(File.Exists),
                "unchanged-skipped: the result should still report the full on-disk page inventory");

            // changed-page-rebuilt: re-index a community member with a moved declaration
            // (start_line 3 -> 4). Only the impl code-map/api-surface skeletons plus the member's
            // modeling context code-map, objects leaf and workflows leaf (file:line facts) depend
            // on symbol locations, so exactly those five pages rebuild; everything else stays
            // skipped. The two rebuilding leaves carry narrative slots, so their narratives are
            // refreshed (two calls); pages whose inputs did not change make no narrative call.
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Symbols: [new CodeSymbolFact("symbol:fw:core:a", "file:fw:core", "CoreService", "class", 4, 21, "CoreService", Visibility: "public", Exported: true)]));
            var incrOverviewPath = Path.Combine(incrDir, "impl", "global", "overview", "architecture.md");
            const string overviewSentinel = "sentinel: overview must not be rewritten\n";
            await File.WriteAllTextAsync(incrOverviewPath, overviewSentinel);
            var narrativeCallsBeforeThird = incrLlm.Calls.Count(c => c.System.Contains("narrative", StringComparison.OrdinalIgnoreCase));
            var thirdBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: incrDir, Llm: incrLlm));
            assert(thirdBuild.RebuiltPages == 5 && thirdBuild.SkippedPages == 23,
                $"changed-page-rebuilt: only the location-dependent pages should rebuild (rebuilt={thirdBuild.RebuiltPages}, skipped={thirdBuild.SkippedPages})");
            var rebuiltCodeMap = await File.ReadAllTextAsync(incrCodeMapPath);
            assert(rebuiltCodeMap.Contains("`src/Core.cs:4`", StringComparison.Ordinal),
                "changed-page-rebuilt: the rebuilt code-map should carry the new symbol location");
            assert(await File.ReadAllTextAsync(incrOverviewPath) == overviewSentinel,
                "changed-page-rebuilt: pages with unchanged inputs should stay untouched on disk");
            assert(incrLlm.Calls.Count(c => c.System.Contains("narrative", StringComparison.OrdinalIgnoreCase)) == narrativeCallsBeforeThird + 2,
                "changed-page-rebuilt: only the two rebuilding leaf pages should refresh their narratives");

            // force: a full rebuild regardless of matching hashes.
            var forceBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: incrDir, Llm: incrLlm, Force: true));
            assert(forceBuild.RebuiltPages == 28 && forceBuild.SkippedPages == 0,
                $"force: Force=true should rebuild every page (rebuilt={forceBuild.RebuiltPages}, skipped={forceBuild.SkippedPages})");
            assert((await File.ReadAllTextAsync(incrOverviewPath)).Contains("# Architecture", StringComparison.Ordinal),
                "force: the forced rebuild should regenerate the overwritten architecture page");

            // ---- narrative-pending backfill (design §1.5): a degraded build marks the
            // narrative slot pending in .meta.json; the next build with an available LLM
            // rebuilds exactly the pending page (structure hash unchanged) to backfill the
            // narrative and clears the flag; non-pending pages stay skipped.
            var pendDir = Path.Combine(root, "pend");
            var degradedBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: pendDir));
            assert(degradedBuild.RebuiltPages == 28 && !degradedBuild.LlmUsed,
                "narrative-pending: the degraded fresh build should rebuild every page without an LLM");
            var pendMetaPath = Path.Combine(pendDir, ".meta.json");
            assert((await File.ReadAllTextAsync(pendMetaPath)).Contains("\"narrativePending\": true", StringComparison.Ordinal),
                "narrative-pending: the degraded build should record narrativePending=true in .meta.json");
            var pendOverviewPath = Path.Combine(pendDir, "impl", "global", "overview", "architecture.md");
            assert((await File.ReadAllTextAsync(pendOverviewPath)).Contains("_pending: llm unavailable_", StringComparison.Ordinal),
                "narrative-pending: the degraded architecture page should carry the pending narrative slot");

            // A second degraded build must NOT rebuild the pending pages (no LLM to backfill with).
            var stillDegraded = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: pendDir));
            assert(stillDegraded.SkippedPages == 28 && stillDegraded.RebuiltPages == 0,
                "narrative-pending: without an LLM a pending page should stay skipped, not thrash");

            var pendCodeMapPath = Path.Combine(pendDir, "impl", "global", "reference", "code-map.md");
            const string pendSentinel = "sentinel: non-pending pages must stay skipped\n";
            await File.WriteAllTextAsync(pendCodeMapPath, pendSentinel);
            var backfillLlm = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? "[]"
                    : "Backfilled narrative after the LLM came back.",
            };
            var backfillBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: pendDir, Llm: backfillLlm));
            assert(backfillBuild.RebuiltPages == 7 && backfillBuild.SkippedPages == 21,
                $"narrative-pending: only the pending narrative pages should rebuild for the backfill (rebuilt={backfillBuild.RebuiltPages}, skipped={backfillBuild.SkippedPages})");
            assert((await File.ReadAllTextAsync(pendOverviewPath)).Contains("Backfilled narrative after the LLM came back.", StringComparison.Ordinal),
                "narrative-pending: the backfill build should fill the narrative slot");
            assert(await File.ReadAllTextAsync(pendCodeMapPath) == pendSentinel,
                "narrative-pending: non-pending pages should stay untouched by the backfill build");
            assert(!(await File.ReadAllTextAsync(pendMetaPath)).Contains("\"narrativePending\": true", StringComparison.Ordinal),
                "narrative-pending: a successful backfill should clear narrativePending in .meta.json");
            var afterBackfill = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: pendDir, Llm: backfillLlm));
            assert(afterBackfill.SkippedPages == 28 && afterBackfill.RebuiltPages == 0,
                "narrative-pending: after the backfill an unchanged LLM build should skip everything again");

            // ---- grouping review gate (design §2 Phase 1.3): Phase 1 writes the
            // .grouping-review.json draft; an approved (human-edited) file is authoritative and
            // skips the LLM review; RequireApprovedGrouping without an approved file degrades
            // to the structure layer with a diagnostic.
            var reviewDir = Path.Combine(root, "reviewgate");
            var reviewLlm = new MockLlmClient
            {
                Responder = (_, user) => user.Contains("merge/rename/flag", StringComparison.Ordinal)
                    ? """[{"action": "rename", "id": "community:001", "name": "LLM Core"}]"""
                    : "Review-gate narrative.",
            };
            await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: reviewDir, Llm: reviewLlm));
            var reviewPath = Path.Combine(reviewDir, ".grouping-review.json");
            assert(File.Exists(reviewPath), "review-gate: phase1 should write the .grouping-review.json draft at the output root");
            var draftText = await File.ReadAllTextAsync(reviewPath);
            assert(draftText.Contains("\"approved\": false", StringComparison.Ordinal),
                "review-gate: the generated draft should carry approved=false");
            assert(draftText.Contains("community:001", StringComparison.Ordinal)
                    && draftText.Contains("LLM Core", StringComparison.Ordinal)
                    && draftText.Contains("symbol:fw:util:x", StringComparison.Ordinal)
                    && draftText.Contains("sourceCommunities", StringComparison.Ordinal),
                "review-gate: the draft should record group id/name/members/sourceCommunities");
            assert(draftText.Contains("llmSuggestedActions", StringComparison.Ordinal)
                    && draftText.Contains("\"rename\"", StringComparison.Ordinal),
                "review-gate: the draft should record the raw LLM-suggested review actions");

            // Human approval: edit the file, set approved=true, rename a group. The next build
            // uses the file verbatim (name change → group pages rebuild) and skips the LLM review.
            await File.WriteAllTextAsync(reviewPath, """
                {
                  "schemaVersion": 1,
                  "approved": true,
                  "groups": [
                    { "id": "community:001", "name": "Human Core", "members": ["symbol:fw:core:a", "symbol:fw:core:b"], "sourceCommunities": ["community:001"] },
                    { "id": "community:002", "name": "Demo.Util", "members": ["symbol:fw:util:x", "symbol:fw:util:y"], "sourceCommunities": ["community:002"] }
                  ]
                }
                """);
            var reviewCallsBeforeApproved = reviewLlm.Calls.Count(c => c.User.Contains("merge/rename/flag", StringComparison.Ordinal));
            var approvedBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: reviewDir, Llm: reviewLlm));
            var approvedOverview = await File.ReadAllTextAsync(Path.Combine(reviewDir, "impl", "global", "overview", "architecture.md"));
            assert(approvedOverview.Contains("| community:001 | Human Core | 2 |", StringComparison.Ordinal),
                "review-gate: an approved review file should be authoritative for the grouping");
            assert(reviewLlm.Calls.Count(c => c.User.Contains("merge/rename/flag", StringComparison.Ordinal)) == reviewCallsBeforeApproved,
                "review-gate: an approved review file should skip the LLM grouping review");
            assert(approvedBuild.Diagnostics.Any(d => d.Contains("approved", StringComparison.OrdinalIgnoreCase)
                    && d.Contains("skipped", StringComparison.OrdinalIgnoreCase)),
                "review-gate: using the approved file should surface as a diagnostic");
            assert((await File.ReadAllTextAsync(reviewPath)).Contains("Human Core", StringComparison.Ordinal)
                    && (await File.ReadAllTextAsync(reviewPath)).Contains("\"approved\": true", StringComparison.Ordinal),
                "review-gate: the approved file must never be overwritten by the build");
            assert(approvedOverview.Contains("Review-gate narrative.", StringComparison.Ordinal),
                "review-gate: the narrative layer still runs under an approved grouping");

            // RequireApprovedGrouping without an approved file: structure layer only + diagnostic,
            // but the review draft is still written so a human can approve it.
            var gateDir = Path.Combine(root, "requiregate");
            var gateLlm = new MockLlmClient { Responder = (_, _) => "should never be called" };
            var gateBuild = await pipeline.BuildAsync(om, new FractalWikiOptions(
                OutputDirectory: gateDir, Llm: gateLlm, RequireApprovedGrouping: true));
            assert(!gateBuild.LlmUsed && gateLlm.Calls.Count == 0,
                "require-approved: without an approved file no LLM call may happen");
            assert(gateBuild.Diagnostics.Any(d => d.Contains("RequireApprovedGrouping", StringComparison.Ordinal)),
                "require-approved: the degraded build should carry a RequireApprovedGrouping diagnostic");
            assert((await File.ReadAllTextAsync(Path.Combine(gateDir, "impl", "global", "overview", "architecture.md")))
                    .Contains("_pending: llm unavailable_", StringComparison.Ordinal),
                "require-approved: the structure layer should still be emitted with a pending narrative slot");
            assert(File.Exists(Path.Combine(gateDir, ".grouping-review.json")),
                "require-approved: the review draft should still be written for a human to approve");
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
