using System.Text.RegularExpressions;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// Engineering fractal generation suite (add-llm-wiki-engineering-fractal track T1.1, delta
/// behavior://llm-wiki-pipeline/requirements/engineering-fractal): cases root-structure /
/// six-categories / manifest-blocks / controlled-frontmatter, plus incremental coverage of the
/// new page set (a second unchanged build skips everything). Pure structure layer — no LLM,
/// pinned communities/processes/diagnostics, assertions follow analysis/fixtures.md [P] items.
/// </summary>
internal static class FractalWikiEngineeringFractalTests
{
    /// <summary>Slim manifest block regex (fixtures E-D2).</summary>
    private static readonly Regex SlimManifestPattern = new(
        @"^> 目录职责 · holds: .+ · excludes: .+ · tier: (stable|dated)( · ⬆from: .+)?( · ⬇to: .+)?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Categories the generator emits for this fixture: default-six subset, 缺哪类就不建 (E-C1) —
    /// examples has no real worked samples yet, so its directory is not created at all
    /// (decisions #2, superseding the placeholder page of decisions #1).
    /// </summary>
    private static readonly string[] EmittedCategories =
        ["overview", "howto", "rules", "reference", "troubleshooting"];

    private static readonly HashSet<string> AllowedDocRoles = new(StringComparer.Ordinal)
    {
        "canonical", "derived", "guide", "rules", "howto", "example",
        "reference", "troubleshooting", "compat", "legacy",
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "active", "draft", "compat", "legacy", "deprecated",
    };

    private static readonly HashSet<string> AllowedFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "knowledge_system", "knowledge_plane", "doc_role", "status", "last_verified", "canonical_source",
    };

    /// <summary>
    /// Fixture with two pinned communities, one execution flow (with steps), public API symbols
    /// and one indexing diagnostic — enough deterministic input for every category page.
    /// </summary>
    internal static async Task<CozoOm> BuildFixtureAsync(CozoDb db)
    {
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Files:
            [
                new CodeFileFact("file:ef:core", "repo:ef", "src/Core.cs"),
                new CodeFileFact("file:ef:util", "repo:ef", "src/Util.cs")
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:ef:core:a", "file:ef:core", "CoreService", "class", 3, 20, "CoreService", Visibility: "public", Exported: true),
                new CodeSymbolFact("symbol:ef:core:b", "file:ef:core", "CoreHelper", "method", 22, 30, "CoreHelper()"),
                new CodeSymbolFact("symbol:ef:util:x", "file:ef:util", "UtilFormat", "method", 2, 8, "UtilFormat()", Visibility: "public", Exported: true),
                new CodeSymbolFact("symbol:ef:util:y", "file:ef:util", "UtilParse", "method", 10, 18, "UtilParse()")
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:ef:core:a", "symbol:ef:core:b", CodeEdgeKinds.Calls, "file:ef:core", 5, 0.9, "roslyn", "intra"),
                new CodeEdgeFact("symbol:ef:core:b", "symbol:ef:util:x", CodeEdgeKinds.Calls, "file:ef:core", 25, 0.9, "roslyn", "inter"),
                new CodeEdgeFact("symbol:ef:core:a", "symbol:ef:util:y", CodeEdgeKinds.Calls, "file:ef:core", 7, 0.8, "roslyn", "inter")
            ],
            Diagnostics:
            [
                new CodeDiagnosticFact("diag:ef:1", "file:ef:util", "unresolved_import", "could not resolve import", "warn")
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
              ["symbol:ef:core:a", "community:001"], ["symbol:ef:core:b", "community:001"],
              ["symbol:ef:util:x", "community:002"], ["symbol:ef:util:y", "community:002"]]
            :put ck_member {symbol_id => community_id}
            """)) { }
        using (db.Run(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
              ["process:ef01", "CoreFlow", "symbol:ef:core:a", "public_api", "public_api", 3]]
            :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
            """)) { }
        using (db.Run(
            """
            ?[process_id, step, symbol_id, via_kind] <- [
              ["process:ef01", 0, "symbol:ef:core:a", ""],
              ["process:ef01", 1, "symbol:ef:core:b", "CALLS"],
              ["process:ef01", 2, "symbol:ef:util:x", "CALLS"]]
            :put ck_process_step {process_id, step => symbol_id, via_kind}
            """)) { }
        return om;
    }

    /// <summary>Splits a page into (frontmatter lines, body); asserts the page starts with frontmatter.</summary>
    private static (IReadOnlyList<string> Frontmatter, string Body)? SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var lines = content[4..end].Split('\n');
        return (lines, content[(end + 5)..]);
    }

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fractal-eng-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = await BuildFixtureAsync(db);
            var pipeline = new FractalWikiPipeline();

            var outDir = Path.Combine(root, "docs-preview");
            var result = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: outDir));

            // ---- case root-structure: index.md、migration-map.md 与 impl/ 递归结构（fixtures E-A）。
            assert(File.Exists(Path.Combine(outDir, "index.md")),
                "root-structure: docs-preview root should carry index.md");
            assert(File.Exists(Path.Combine(outDir, "migration-map.md")),
                "root-structure: docs-preview root should carry migration-map.md (root ledger, E-A1/E-A2)");
            assert(Directory.Exists(Path.Combine(outDir, "impl")),
                "root-structure: docs-preview root should carry the impl/ knowledge system");
            var rootDirs = Directory.GetDirectories(outDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            assert(rootDirs.SequenceEqual(new[] { "impl", "modeling" }),
                "root-structure: the root carries exactly the impl/ and modeling/ knowledge systems (dual fractal, got: "
                + string.Join(", ", rootDirs!) + ")");
            var rootMdFiles = Directory.GetFiles(outDir, "*.md").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            assert(rootMdFiles.SequenceEqual(new[] { "index.md", "migration-map.md" }),
                "root-structure: root layer .md files are exactly index.md + migration-map.md (got: "
                + string.Join(", ", rootMdFiles!) + ")");
            assert(File.Exists(Path.Combine(outDir, "impl", "index.md")),
                "root-structure: impl/index.md (knowledge-system navigation) should exist");
            assert(Directory.Exists(Path.Combine(outDir, "impl", "global"))
                    && File.Exists(Path.Combine(outDir, "impl", "global", "index.md")),
                "root-structure: the default global plane must be emitted under impl/ (E-A3/E-A4)");
            var migration = await File.ReadAllTextAsync(Path.Combine(outDir, "migration-map.md"));
            assert(migration.Contains("| Old Path | New Path | Status | Notes |", StringComparison.Ordinal),
                "root-structure: migration-map.md should carry the ledger table header (E-A2)");
            foreach (Match row in Regex.Matches(migration, @"^\|(?!\s*Old Path)(?![-\s|]+$)[^|]*\|[^|]*\|([^|]*)\|", RegexOptions.Multiline))
            {
                var status = row.Groups[1].Value.Trim();
                assert(status is "migrated" or "absorbed" or "compat" or "deprecated",
                    $"root-structure: migration-map Status values must be in the controlled set (got '{status}')");
            }

            assert(migration.Contains("impl/global/overview/index.md", StringComparison.Ordinal)
                    && migration.Contains("impl/global/overview/architecture.md", StringComparison.Ordinal),
                "root-structure: the G2 overview page move should be recorded as a migrated row");
            assert(migration.Contains("## Build Ledger", StringComparison.Ordinal),
                "root-structure: migration-map should carry the append-style build ledger section");

            // plane 第一层（除 index.md）只含类目目录，无散落叶子（E-B1）。
            var planeDir = Path.Combine(outDir, "impl", "global");
            var strayLeaves = Directory.GetFiles(planeDir, "*.md")
                .Select(Path.GetFileName)
                .Where(name => name != "index.md")
                .ToArray();
            assert(strayLeaves.Length == 0,
                "root-structure: the plane first level must not carry stray leaf .md files (got: "
                + string.Join(", ", strayLeaves!) + ")");

            // ---- case six-categories: 六类目子集 + 各自带职责块的 index.md（fixtures E-C/E-D1）。
            foreach (var category in EmittedCategories)
            {
                var categoryIndex = Path.Combine(planeDir, category, "index.md");
                assert(File.Exists(categoryIndex),
                    $"six-categories: impl/global/{category}/index.md should exist");
                var content = await File.ReadAllTextAsync(categoryIndex);
                assert(content.Contains("目录职责", StringComparison.Ordinal),
                    $"six-categories: {category}/index.md should carry a 目录职责 block (E-D1)");
            }

            var categoryDirs = Directory.GetDirectories(planeDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            assert(categoryDirs.SequenceEqual(EmittedCategories.Order(StringComparer.Ordinal)),
                "six-categories: plane first-level categories must be exactly the emitted default-six subset (got: "
                + string.Join(", ", categoryDirs!) + ")");

            // examples 无真实样例 → 类目不建（E-C1 缺哪类就不建，decisions #2）。
            assert(!Directory.Exists(Path.Combine(planeDir, "examples")),
                "six-categories: examples must not be emitted as an empty placeholder category (E-C1)");

            // 每个已产出类目应有非 index 叶子（E-C1 非空类目）。
            foreach (var category in EmittedCategories)
            {
                var leaves = Directory.GetFiles(Path.Combine(planeDir, category), "*.md", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path) != "index.md")
                    .ToArray();
                assert(leaves.Length > 0,
                    $"six-categories: category {category} must carry at least one non-index leaf");
            }

            // 具体叶子（design §1 页面集）。
            assert(File.Exists(Path.Combine(planeDir, "overview", "architecture.md")),
                "six-categories: overview/architecture.md (migrated overview content) should exist");
            assert(File.Exists(Path.Combine(planeDir, "reference", "code-map.md"))
                    && File.Exists(Path.Combine(planeDir, "reference", "api-surface.md")),
                "six-categories: reference should carry code-map.md and api-surface.md");
            assert(File.Exists(Path.Combine(planeDir, "rules", "boundaries.md")),
                "six-categories: rules/boundaries.md should exist");
            assert(File.Exists(Path.Combine(planeDir, "troubleshooting", "diagnostics.md")),
                "six-categories: troubleshooting/diagnostics.md should exist");
            var howtoLeaves = Directory.GetFiles(Path.Combine(planeDir, "howto"), "working-with-*.md")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(howtoLeaves.Length == 2,
                "six-categories: one working-with-<group>.md per pinned community (got: "
                + string.Join(", ", howtoLeaves!) + ")");
            assert(howtoLeaves.Contains("working-with-demo-core.md") && howtoLeaves.Contains("working-with-demo-util.md"),
                "six-categories: howto topic file names should be slugs of the group names");

            // api-surface：public 符号表（exported/visibility=public），零 LLM，按组分节。
            var apiSurface = await File.ReadAllTextAsync(Path.Combine(planeDir, "reference", "api-surface.md"));
            assert(apiSurface.Contains("CoreService", StringComparison.Ordinal)
                    && apiSurface.Contains("UtilFormat", StringComparison.Ordinal),
                "six-categories: api-surface should list the public symbols");
            assert(!apiSurface.Contains("CoreHelper", StringComparison.Ordinal)
                    && !apiSurface.Contains("UtilParse", StringComparison.Ordinal),
                "six-categories: api-surface must not list non-public symbols");
            assert(!apiSurface.Contains("llm:begin", StringComparison.Ordinal),
                "six-categories: api-surface is a zero-LLM reference page (no narrative slot)");

            // howto/rules/troubleshooting 骨架 + llm 叙述段（结构层 pending）。
            var howtoCore = await File.ReadAllTextAsync(Path.Combine(planeDir, "howto", "working-with-demo-core.md"));
            assert(howtoCore.Contains("CoreFlow", StringComparison.Ordinal),
                "six-categories: the howto skeleton should carry the group's entry processes");
            assert(howtoCore.Contains("<!-- llm:begin narrative -->", StringComparison.Ordinal)
                    && howtoCore.Contains("_pending: llm unavailable_", StringComparison.Ordinal),
                "six-categories: howto pages should carry a marked llm narrative slot (pending without an LLM)");
            var boundaries = await File.ReadAllTextAsync(Path.Combine(planeDir, "rules", "boundaries.md"));
            assert(boundaries.Contains("Import Cycles", StringComparison.Ordinal)
                    && boundaries.Contains("Cross-Group Calls", StringComparison.Ordinal)
                    && boundaries.Contains("<!-- llm:begin narrative -->", StringComparison.Ordinal),
                "six-categories: boundaries skeleton should carry import cycles + cross-group call table + llm slot");
            assert(boundaries.Contains("CALLS", StringComparison.Ordinal) && boundaries.Contains("| 2 |", StringComparison.Ordinal),
                "six-categories: the cross-group call table should carry the pinned inter-community edge counts");
            var diagnosticsPage = await File.ReadAllTextAsync(Path.Combine(planeDir, "troubleshooting", "diagnostics.md"));
            assert(diagnosticsPage.Contains("unresolved_import", StringComparison.Ordinal)
                    && diagnosticsPage.Contains("warn", StringComparison.Ordinal)
                    && diagnosticsPage.Contains("<!-- llm:begin narrative -->", StringComparison.Ordinal),
                "six-categories: diagnostics skeleton should carry the indexed diagnostic distribution + llm slot");

            // ---- case manifest-blocks: 精简型正则逐一匹配；结构节点用完整型（fixtures E-D2/E-D3）。
            foreach (var category in EmittedCategories)
            {
                var content = await File.ReadAllTextAsync(Path.Combine(planeDir, category, "index.md"));
                assert(SlimManifestPattern.IsMatch(content),
                    $"manifest-blocks: {category}/index.md slim manifest must match the E-D2 regex");
                var parts = SplitFrontmatter(content);
                assert(parts is not null, $"manifest-blocks: {category}/index.md should start with frontmatter");
                var body = parts!.Value.Body.TrimStart('\n');
                var lines = body.Split('\n');
                assert(lines.Length >= 3 && lines[0].StartsWith("# ", StringComparison.Ordinal)
                        && lines[2].StartsWith("> 目录职责 · ", StringComparison.Ordinal),
                    $"manifest-blocks: {category}/index.md manifest block must sit directly under the H1");
            }

            foreach (var structural in new[] { "index.md", Path.Combine("impl", "index.md"), Path.Combine("impl", "global", "index.md") })
            {
                var content = await File.ReadAllTextAsync(Path.Combine(outDir, structural));
                assert(content.Contains("## 目录职责", StringComparison.Ordinal)
                        && content.Contains("**holds**", StringComparison.Ordinal)
                        && content.Contains("**excludes**", StringComparison.Ordinal)
                        && Regex.IsMatch(content, @"\*\*tier\*\*：`(stable|dated)`")
                        && content.Contains("**promotes_from**", StringComparison.Ordinal)
                        && content.Contains("**promotes_to**", StringComparison.Ordinal),
                    $"manifest-blocks: structural node {structural} must carry the full manifest section (E-D3)");
            }

            // ---- case controlled-frontmatter: 每页 frontmatter 受控字段、合法值域、无数组（fixtures E-E）。
            var allPages = Directory.GetFiles(outDir, "*.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(allPages.Length == result.Pages.Count,
                $"controlled-frontmatter: on-disk page count should match the reported inventory ({allPages.Length} vs {result.Pages.Count})");
            foreach (var page in allPages)
            {
                var relative = Path.GetRelativePath(outDir, page).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("modeling/", StringComparison.Ordinal))
                {
                    // The modeling knowledge system has its own controlled schema (context field,
                    // knowledge_plane=domain) — covered by FractalWikiModelingFractalTests.
                    continue;
                }

                var content = await File.ReadAllTextAsync(page);
                var parts = SplitFrontmatter(content);
                assert(parts is not null, $"controlled-frontmatter: {relative} must start with a frontmatter block");
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in parts!.Value.Frontmatter)
                {
                    assert(!line.StartsWith(" ", StringComparison.Ordinal) && !line.StartsWith("-", StringComparison.Ordinal),
                        $"controlled-frontmatter: {relative} frontmatter must not carry multi-line/array values (line '{line}')");
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    assert(colon > 0, $"controlled-frontmatter: {relative} frontmatter line '{line}' should be key: value");
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    assert(AllowedFrontmatterKeys.Contains(key),
                        $"controlled-frontmatter: {relative} carries a non-controlled frontmatter key '{key}'");
                    assert(!value.StartsWith("[", StringComparison.Ordinal),
                        $"controlled-frontmatter: {relative} field {key} must not be a YAML array");
                    fields[key] = value;
                }

                assert(fields.ContainsKey("knowledge_plane") && fields.ContainsKey("doc_role")
                        && fields.ContainsKey("status") && fields.ContainsKey("last_verified"),
                    $"controlled-frontmatter: {relative} must carry the four fixed fields (E-E1)");
                assert(AllowedDocRoles.Contains(fields["doc_role"]),
                    $"controlled-frontmatter: {relative} doc_role '{fields["doc_role"]}' outside the controlled domain");
                assert(AllowedStatuses.Contains(fields["status"]),
                    $"controlled-frontmatter: {relative} status '{fields["status"]}' outside the controlled domain");
                assert(Regex.IsMatch(fields["last_verified"], @"^\d{4}-\d{2}-\d{2}$"),
                    $"controlled-frontmatter: {relative} last_verified '{fields["last_verified"]}' must be YYYY-MM-DD");
                assert(!fields.ContainsKey("context") && !fields.ContainsKey("derived_from"),
                    $"controlled-frontmatter: {relative} must not carry modeling-only fields (E-E4)");
                assert(fields["knowledge_plane"] == "global",
                    $"controlled-frontmatter: {relative} knowledge_plane should be global (single-plane MVP, E-E5)");

                // doc_role 与类目一致（E-E6）：index → guide；类目叶子 → 类目角色。
                var segments = relative.Split('/');
                var isIndex = segments[^1] == "index.md";
                var expectedRole = relative switch
                {
                    "migration-map.md" => "reference",
                    _ when isIndex => "guide",
                    _ when segments.Length >= 4 && segments[0] == "impl" => segments[2] switch
                    {
                        "overview" => "guide",
                        "howto" => "howto",
                        "rules" => "rules",
                        "examples" => "example",
                        "reference" => "reference",
                        "troubleshooting" => "troubleshooting",
                        _ => "guide",
                    },
                    _ => "guide",
                };
                assert(fields["doc_role"] == expectedRole,
                    $"controlled-frontmatter: {relative} doc_role should be {expectedRole} (got {fields["doc_role"]})");
            }

            // migration-map 附加 knowledge_system: impl（E-A2 模板）。
            var migrationParts = SplitFrontmatter(migration);
            assert(migrationParts is not null
                    && migrationParts.Value.Frontmatter.Contains("knowledge_system: impl"),
                "controlled-frontmatter: migration-map.md should carry knowledge_system: impl (E-A2)");

            // ---- 增量哈希覆盖新页面：未变更二跑全 skip，.meta.json 覆盖全部页面。
            assert(result.RebuiltPages == result.Pages.Count && result.SkippedPages == 0,
                $"incremental: the fresh build should rebuild the full inventory (rebuilt={result.RebuiltPages}/{result.Pages.Count})");
            var metaText = await File.ReadAllTextAsync(Path.Combine(outDir, ".meta.json"));
            foreach (var page in allPages)
            {
                var relative = Path.GetRelativePath(outDir, page).Replace(Path.DirectorySeparatorChar, '/');
                assert(metaText.Contains($"\"{relative}\"", StringComparison.Ordinal),
                    $"incremental: .meta.json must cover the new page {relative}");
            }

            var second = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: outDir));
            assert(second.SkippedPages == result.Pages.Count && second.RebuiltPages == 0,
                $"incremental: an unchanged second build must skip every page (rebuilt={second.RebuiltPages}, skipped={second.SkippedPages})");
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
