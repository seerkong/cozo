using System.Text.RegularExpressions;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Wiki;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// FractalSpecChecker gate suite (add-llm-wiki-engineering-fractal track T2.1.1): the generator's
/// own output must be spec-clean (zero violations against the engineering [P] items of
/// analysis/fixtures.md), and deliberately broken documents must be reported under the RIGHT
/// RuleId (discrimination assertions). Mutations run on fresh copies of the clean build so
/// cases stay independent.
/// </summary>
internal static class FractalSpecCheckerTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fractal-checker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = await FractalWikiEngineeringFractalTests.BuildFixtureAsync(db);
            var pipeline = new FractalWikiPipeline();
            var cleanDir = Path.Combine(root, "clean");
            await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: cleanDir));

            // ---- checker-gate: the generator's own dual-fractal output passes every implemented
            // [P] rule — the full E-* + M-* set (modeling-fractal T2.1).
            var clean = FractalSpecChecker.Check(cleanDir);
            assert(clean.Count == 0,
                "checker-gate: generator output must be spec-clean (got: "
                + string.Join("; ", clean.Select(v => $"{v.RuleId} {v.Path}: {v.Detail}")) + ")");

            // The G3 handover interface is closed: every M-* rule is implemented now.
            assert(FractalSpecChecker.NotImplementedRuleIds.Count == 0,
                "checker-gate: the M-* not-implemented interface must be empty (T2.1 closes it)");

            // The modeling/ subtree is checked for real: a stray frontmatter-less doc is reported.
            var withModeling = Path.Combine(root, "with-modeling");
            CopyTree(cleanDir, withModeling);
            await File.WriteAllTextAsync(Path.Combine(withModeling, "modeling", "domain", "stray.md"), "no frontmatter at all");
            assert(FractalSpecChecker.Check(withModeling).Any(v => v.RuleId == "M-E1"),
                "checker-gate: a frontmatter-less modeling document must be reported (M-E1), not skipped");

            // ---- discrimination: each mutation must surface its fixtures RuleId. -----------------
            var caseId = 0;
            async Task<IReadOnlyList<FractalSpecViolation>> MutateAsync(Func<string, Task> mutate, string? repositoryRoot = null)
            {
                var dir = Path.Combine(root, $"case-{caseId++}");
                CopyTree(cleanDir, dir);
                await mutate(dir);
                return FractalSpecChecker.Check(dir, repositoryRoot);
            }

            void ExpectRule(IReadOnlyList<FractalSpecViolation> violations, string ruleId, string label) =>
                assert(violations.Any(v => v.RuleId == ruleId),
                    $"discrimination: {label} must be reported as {ruleId} (got: "
                    + (violations.Count == 0 ? "<none>" : string.Join("; ", violations.Select(v => v.RuleId + " " + v.Path))) + ")");

            var frontmatter = FractalDocFormat.Frontmatter("global", "guide", "2026-07-05");

            // E-A1: missing root index.md.
            ExpectRule(await MutateAsync(dir =>
            {
                File.Delete(Path.Combine(dir, "index.md"));
                return Task.CompletedTask;
            }), "E-A1", "a missing root index.md");

            // E-A2: migration-map Status outside the controlled set.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "migration-map.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("| migrated |", "| moved |", StringComparison.Ordinal));
            }), "E-A2", "an out-of-domain migration-map Status");

            // E-A3: a plane directory at the docs root (docs/<plane>/ form).
            ExpectRule(await MutateAsync(async dir =>
            {
                Directory.CreateDirectory(Path.Combine(dir, "runtime"));
                await File.WriteAllTextAsync(Path.Combine(dir, "runtime", "index.md"), frontmatter + "\n# Runtime\n");
            }), "E-A3", "a plane directory at the docs root");

            // E-A4: the recommended global plane is missing.
            ExpectRule(await MutateAsync(dir =>
            {
                Directory.Delete(Path.Combine(dir, "impl", "global"), recursive: true);
                return Task.CompletedTask;
            }), "E-A4", "a missing impl/global plane");

            // E-B1: a stray leaf .md at the plane first level.
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, "impl", "global", "stray.md"), frontmatter + "\n# Stray\n")),
                "E-B1", "a stray leaf at the plane first level");

            // E-B2: nesting beyond 类目/主题/叶子.
            ExpectRule(await MutateAsync(async dir =>
            {
                var deep = Path.Combine(dir, "impl", "global", "reference", "topic", "deeper");
                Directory.CreateDirectory(deep);
                await File.WriteAllTextAsync(Path.Combine(dir, "impl", "global", "reference", "topic", "index.md"),
                    frontmatter + "\n# Topic\n\n" + FractalDocFormat.SlimManifest("t", "x", "a", "b") + "\n");
                await File.WriteAllTextAsync(Path.Combine(dir, "impl", "global", "reference", "topic", "leaf.md"),
                    FractalDocFormat.Frontmatter("global", "reference", "2026-07-05") + "\n# Leaf\n");
                await File.WriteAllTextAsync(Path.Combine(deep, "index.md"), frontmatter + "\n# Deep\n");
            }), "E-B2", "a directory below the topic level");

            // E-C1: an empty category (index.md, no leaves).
            var emptyCategory = await MutateAsync(async dir =>
            {
                var category = Path.Combine(dir, "impl", "global", "examples");
                Directory.CreateDirectory(category);
                await File.WriteAllTextAsync(Path.Combine(category, "index.md"),
                    frontmatter + "\n# Examples\n\n" + FractalDocFormat.SlimManifest("样例", "操作（→howto/）", "a", "b") + "\n");
            });
            ExpectRule(emptyCategory, "E-C1", "an empty category directory");

            // E-D1: an index.md without any 目录职责 block.
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, "impl", "global", "rules", "index.md"),
                frontmatter + "\n# Rules\n\n- [Boundary Rules](boundaries.md)\n")),
                "E-D1", "an index.md without a manifest block");

            // E-D2: a malformed slim manifest (missing excludes).
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, "impl", "global", "reference", "index.md"),
                frontmatter + "\n# Reference\n\n> 目录职责 · holds: 查表 · tier: stable\n\n- [Code Map](code-map.md)\n- [API Surface](api-surface.md)\n")),
                "E-D2", "a slim manifest missing excludes");

            // E-D3: a structure node (plane root) downgraded to a slim manifest.
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, "impl", "global", "index.md"),
                frontmatter + "\n# Global Plane\n\n" + FractalDocFormat.SlimManifest("实现知识", "本体（→modeling）", "a", "b")
                + "\n\n- [overview](overview/index.md)\n- [howto](howto/index.md)\n- [rules](rules/index.md)\n- [reference](reference/index.md)\n- [troubleshooting](troubleshooting/index.md)\n")),
                "E-D3", "a structure node without the full manifest section");

            // E-D4: a custom category without a full manifest declaration.
            ExpectRule(await MutateAsync(async dir =>
            {
                var category = Path.Combine(dir, "impl", "global", "playbooks");
                Directory.CreateDirectory(category);
                await File.WriteAllTextAsync(Path.Combine(category, "index.md"),
                    frontmatter + "\n# Playbooks\n\n" + FractalDocFormat.SlimManifest("演练", "操作（→howto/）", "a", "b") + "\n\n- [Drill](drill.md)\n");
                await File.WriteAllTextAsync(Path.Combine(category, "drill.md"),
                    frontmatter + "\n# Drill\n");
            }), "E-D4", "a custom category with only a slim manifest");

            // E-E1: a leaf without frontmatter.
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, "impl", "global", "reference", "code-map.md"), "# Code Map\n\nno frontmatter\n")),
                "E-E1", "a document without frontmatter");

            // E-E2: doc_role outside the controlled domain.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "reference", "code-map.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("doc_role: reference", "doc_role: encyclopedia", StringComparison.Ordinal));
            }), "E-E2", "an out-of-domain doc_role");

            // E-E3: a YAML array field in frontmatter.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "rules", "boundaries.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("status: active\n", "status: active\ntopics:\n  - boundaries\n  - imports\n", StringComparison.Ordinal));
            }), "E-E3", "a frontmatter array field (topics:)");

            // E-E4: a modeling-only field on an impl document.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "troubleshooting", "diagnostics.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("status: active\n", "status: active\nderived_from: modeling/domain/index.md\n", StringComparison.Ordinal));
            }), "E-E4", "derived_from on an impl document");

            // E-E5: knowledge_plane contradicting the plane path segment.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "overview", "architecture.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("knowledge_plane: global", "knowledge_plane: runtime", StringComparison.Ordinal));
            }), "E-E5", "a knowledge_plane mismatching the path");

            // E-E6: a category leaf carrying another category's role.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "rules", "boundaries.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("doc_role: rules", "doc_role: howto", StringComparison.Ordinal));
            }), "E-E6", "a rules/ leaf declared as howto");

            // E-F1: long prose on an index page (index 只导航).
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "overview", "index.md");
                await File.AppendAllTextAsync(path, "\n" + string.Concat(Enumerable.Repeat("这里是一段不该出现在 index 的真源正文。", 30)) + "\n");
            }), "E-F1", "long non-navigation prose on an index page");

            // E-G1: <name>.md and <name>/ coexisting.
            ExpectRule(await MutateAsync(async dir =>
            {
                var folder = Path.Combine(dir, "impl", "global", "overview", "architecture");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "index.md"),
                    frontmatter + "\n# Architecture\n\n" + FractalDocFormat.SlimManifest("架构", "操作（→howto/）", "a", "b") + "\n");
            }), "E-G1", "a sibling same-name file and folder");

            // E-G2: a directory without index.md.
            ExpectRule(await MutateAsync(dir =>
            {
                File.Delete(Path.Combine(dir, "impl", "global", "howto", "index.md"));
                return Task.CompletedTask;
            }), "E-G2", "a directory lacking index.md");

            // E-G3: a migrated row whose New Path does not exist.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "migration-map.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("| migration-map.md |", "| gone/nowhere.md |", StringComparison.Ordinal));
            }), "E-G3", "a migrated row targeting a missing file");

            // E-G4 [P] approximation: mostly single-leaf topic folders (premature splitting).
            ExpectRule(await MutateAsync(async dir =>
            {
                foreach (var topic in new[] { "topic-a", "topic-b" })
                {
                    var folder = Path.Combine(dir, "impl", "global", "reference", topic);
                    Directory.CreateDirectory(folder);
                    await File.WriteAllTextAsync(Path.Combine(folder, "index.md"),
                        frontmatter + $"\n# {topic}\n\n" + FractalDocFormat.SlimManifest("t", "x", "a", "b") + "\n\n- [Leaf](leaf.md)\n");
                    await File.WriteAllTextAsync(Path.Combine(folder, "leaf.md"),
                        FractalDocFormat.Frontmatter("global", "reference", "2026-07-05") + "\n# Leaf\n");
                }
            }), "E-G4", "a plane dominated by single-leaf topic folders");

            // E-H1: a modeling concept link that does not resolve.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "rules", "boundaries.md");
                await File.AppendAllTextAsync(path, "\nSee [Order](../../../modeling/domain/contexts/order/index.md).\n");
            }), "E-H1", "an unresolved modeling concept link");

            // ---- M-* discrimination (modeling-fractal T2.1): one mutation per fixtures rule. ----
            var billingContext = Path.Combine("modeling", "domain", "contexts", "coreservice");
            var domainFrontmatter = "---\nknowledge_plane: domain\ndoc_role: guide\nstatus: active\ncontext: CoreService\nlast_verified: 2026-07-05\n---\n";

            // Builds a minimal well-formed derived plane so a targeted breakage isolates one rule.
            static async Task WriteDerivedPlaneAsync(string dir, string derivedFrom)
            {
                var plane = Path.Combine(dir, "modeling", "runtime");
                var context = Path.Combine(plane, "contexts", "coreservice");
                Directory.CreateDirectory(Path.Combine(context, "objects"));
                var fm = "---\nknowledge_plane: runtime\ndoc_role: guide\nstatus: active\nlast_verified: 2026-07-05\n---\n";
                var full = "\n## 目录职责\n\n- **holds**：h\n- **excludes**：x\n- **tier**：`stable`\n- **promotes_from**：a\n- **promotes_to**：b\n";
                var slim = "\n> 目录职责 · holds: h · excludes: x · tier: stable · ⬆from: a · ⬇to: b\n";
                await File.WriteAllTextAsync(Path.Combine(plane, "index.md"), fm + "\n# Runtime\n" + full + "\n- [contexts](contexts/index.md)\n");
                await File.WriteAllTextAsync(Path.Combine(plane, "glossary.md"),
                    fm + "\n# Runtime Glossary\n\n- CoreService → [domain term](../domain/contexts/coreservice/index.md)\n");
                await File.WriteAllTextAsync(Path.Combine(plane, "contexts", "index.md"),
                    fm + "\n# Contexts\n" + full + "\n- [coreservice](coreservice/index.md)\n");
                var ctxFm = "---\nknowledge_plane: runtime\ndoc_role: guide\nstatus: active\ncontext: CoreService\nlast_verified: 2026-07-05\n---\n";
                await File.WriteAllTextAsync(Path.Combine(context, "index.md"),
                    ctxFm + "\n# CoreService Context\n" + full + "\n## Boundary\n\n- 边界\n\n- [objects](objects/index.md)\n\n## Code Map\n\n- [code-map.md](code-map.md)\n");
                await File.WriteAllTextAsync(Path.Combine(context, "code-map.md"),
                    "---\nknowledge_plane: runtime\ndoc_role: reference\nstatus: active\ncontext: CoreService\n"
                    + "derived_from: modeling/domain/contexts/coreservice/code-map.md\nlast_verified: 2026-07-05\n---\n"
                    + "\n# Code Map\n\n| Symbol | Location |\n|---|---|\n| `CoreService` | `src/Core.cs:3` |\n");
                await File.WriteAllTextAsync(Path.Combine(context, "objects", "index.md"),
                    ctxFm + "\n# Objects\n" + slim + "\n- [coreservice.md](coreservice.md)\n");
                await File.WriteAllTextAsync(Path.Combine(context, "objects", "coreservice.md"),
                    "---\nknowledge_plane: runtime\ndoc_role: derived\nstatus: active\ncontext: CoreService\n"
                    + $"derived_from: {derivedFrom}\nlast_verified: 2026-07-05\n---\n\n# CoreService\n\n投影差异说明。\n");
            }

            // M-A1: the mandatory domain plane is missing.
            ExpectRule(await MutateAsync(dir =>
            {
                Directory.Delete(Path.Combine(dir, "modeling", "domain"), recursive: true);
                return Task.CompletedTask;
            }), "M-A1", "a missing modeling/domain plane");

            // M-A2: the plane root loses one of its fixed three (glossary.md).
            ExpectRule(await MutateAsync(dir =>
            {
                File.Delete(Path.Combine(dir, "modeling", "domain", "glossary.md"));
                return Task.CompletedTask;
            }), "M-A2", "a plane root without glossary.md");

            // M-A3: a derived-plane non-index document without derived_from.
            ExpectRule(await MutateAsync(async dir =>
            {
                await WriteDerivedPlaneAsync(dir, "modeling/domain/contexts/coreservice/objects/coreservice.md");
                var leaf = Path.Combine(dir, "modeling", "runtime", "contexts", "coreservice", "objects", "coreservice.md");
                await File.WriteAllTextAsync(leaf, (await File.ReadAllTextAsync(leaf))
                    .Replace("derived_from: modeling/domain/contexts/coreservice/objects/coreservice.md\n", "", StringComparison.Ordinal));
            }), "M-A3", "a derived-plane leaf without derived_from");

            // M-B1: a context without code-map.md.
            ExpectRule(await MutateAsync(dir =>
            {
                File.Delete(Path.Combine(dir, billingContext, "code-map.md"));
                return Task.CompletedTask;
            }), "M-B1", "a context lacking code-map.md");

            // M-B2: the mandatory Boundary section is dropped from a context index.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, billingContext, "index.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("## Boundary", "## Scope", StringComparison.Ordinal));
            }), "M-B2", "a context index without the Boundary section");

            // M-B2: Not Owned Here dropped while adjacent contexts exist.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, billingContext, "index.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("## Not Owned Here", "## Elsewhere", StringComparison.Ordinal));
            }), "M-B2", "a context index without Not Owned Here despite neighbors");

            // M-B3: a stray leaf .md at the context first level.
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, billingContext, "stray.md"), domainFrontmatter + "\n# Stray\n")),
                "M-B3", "a stray leaf at the context first level");

            // M-B4: more than six first-level categories.
            ExpectRule(await MutateAsync(async dir =>
            {
                for (var i = 0; i < 7; i++)
                {
                    var category = Path.Combine(dir, billingContext, $"extra-{i}");
                    Directory.CreateDirectory(category);
                    await File.WriteAllTextAsync(Path.Combine(category, "index.md"), domainFrontmatter + "\n# Extra\n");
                }
            }), "M-B4", "a context with more than six categories");

            // M-C1: a non-default domain category without a full manifest declaration.
            ExpectRule(await MutateAsync(async dir =>
            {
                var category = Path.Combine(dir, billingContext, "scenes");
                Directory.CreateDirectory(category);
                await File.WriteAllTextAsync(Path.Combine(category, "index.md"),
                    domainFrontmatter + "\n# Scenes\n\n> 目录职责 · holds: 场景 · excludes: 其它 · tier: stable · ⬆from: a · ⬇to: b\n\n- [Scene](scene.md)\n");
                await File.WriteAllTextAsync(Path.Combine(category, "scene.md"),
                    domainFrontmatter.Replace("doc_role: guide", "doc_role: canonical", StringComparison.Ordinal) + "\n# Scene\n\n真源。\n");
            }), "M-C1", "a custom domain category with only a slim manifest");

            // M-C1: an empty category (index without leaves).
            ExpectRule(await MutateAsync(async dir =>
            {
                var leaves = Directory.GetFiles(Path.Combine(dir, billingContext, "objects"))
                    .Where(f => Path.GetFileName(f) != "index.md");
                foreach (var leaf in leaves)
                {
                    File.Delete(leaf);
                }

                await Task.CompletedTask;
            }), "M-C1", "an emptied objects category");

            // M-C2: objects/<name>/ and objects/<name>.md coexisting.
            ExpectRule(await MutateAsync(async dir =>
            {
                var folder = Path.Combine(dir, billingContext, "objects", "coreservice");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "data.md"),
                    domainFrontmatter.Replace("doc_role: guide", "doc_role: canonical", StringComparison.Ordinal) + "\n# Data\n\n结构语义。\n");
            }), "M-C2", "both object forms for the same name");

            // M-C3: nesting below a workflows topic folder.
            ExpectRule(await MutateAsync(async dir =>
            {
                var topic = Path.Combine(dir, billingContext, "workflows", "flows");
                var deeper = Path.Combine(topic, "deeper");
                Directory.CreateDirectory(deeper);
                await File.WriteAllTextAsync(Path.Combine(topic, "index.md"),
                    domainFrontmatter + "\n# Flows\n\n> 目录职责 · holds: h · excludes: x · tier: stable · ⬆from: a · ⬇to: b\n\n- [Leaf](leaf.md)\n");
                await File.WriteAllTextAsync(Path.Combine(topic, "leaf.md"),
                    domainFrontmatter.Replace("doc_role: guide", "doc_role: canonical", StringComparison.Ordinal) + "\n# Leaf\n\n真源。\n");
                await File.WriteAllTextAsync(Path.Combine(deeper, "index.md"), domainFrontmatter + "\n# Deep\n");
            }), "M-C3", "a directory below a workflows topic folder");

            // M-D1: the code-map loses its mapping table.
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, billingContext, "code-map.md"),
                domainFrontmatter.Replace("doc_role: guide", "doc_role: reference", StringComparison.Ordinal) + "\n# Code Map\n\n无映射。\n")),
                "M-D1", "a code-map without a mapping table");

            // M-D1 path slice: with a repository root, a mapped path must exist.
            var fakeRepo = Path.Combine(root, "fake-repo");
            Directory.CreateDirectory(Path.Combine(fakeRepo, "src"));
            await File.WriteAllTextAsync(Path.Combine(fakeRepo, "src", "Core.cs"), "// core");
            await File.WriteAllTextAsync(Path.Combine(fakeRepo, "src", "Util.cs"), "// util");
            assert(FractalSpecChecker.Check(cleanDir, fakeRepo).Count == 0,
                "M-D1: the clean build should resolve every mapped source path against the repository root");
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, billingContext, "code-map.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("`src/Core.cs:", "`src/Gone.cs:", StringComparison.Ordinal));
            }, fakeRepo), "M-D1", "a code-map row mapping a missing source path");

            // M-D2: a modeling index without any 目录职责 block.
            ExpectRule(await MutateAsync(dir => File.WriteAllTextAsync(
                Path.Combine(dir, billingContext, "objects", "index.md"),
                domainFrontmatter + "\n# Objects\n\n- [coreservice.md](coreservice.md)\n")),
                "M-D2", "a modeling category index without a manifest block");

            // M-D3: a code path parked in modeling frontmatter.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, billingContext, "code-map.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("status: active\n", "status: active\nsource: src/Core.cs\n", StringComparison.Ordinal));
            }), "M-D3", "a code path in modeling frontmatter");

            // M-E1: a context-scoped document without the context field.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, billingContext, "code-map.md");
                await File.WriteAllTextAsync(path, Regex.Replace(
                    await File.ReadAllTextAsync(path), "^context: .*\n", "", RegexOptions.Multiline));
            }), "M-E1", "a context-scoped page without context:");

            // M-E2: derived_from on a domain document.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, billingContext, "objects", "coreservice.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("status: active\n", "status: active\nderived_from: modeling/domain/index.md\n", StringComparison.Ordinal));
            }), "M-E2", "derived_from on a domain document");

            // M-E3: a modeling-only field on an impl document.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "impl", "global", "overview", "architecture.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("status: active\n", "status: active\ncontext: CoreService\n", StringComparison.Ordinal));
            }), "M-E3", "context: on an impl document");

            // M-E4: a derived_from target outside/absent from docs/modeling/domain.
            ExpectRule(await MutateAsync(dir =>
                WriteDerivedPlaneAsync(dir, "modeling/domain/contexts/gone/index.md")),
                "M-E4", "a derived_from target that does not exist");

            // M-E5: a domain category leaf that is not canonical.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, billingContext, "objects", "coreservice.md");
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path))
                    .Replace("doc_role: canonical", "doc_role: guide", StringComparison.Ordinal));
            }), "M-E5", "a non-canonical domain objects leaf");

            // M-F1: long prose on a modeling index page.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "modeling", "domain", "contexts", "index.md");
                await File.AppendAllTextAsync(path, "\n" + string.Concat(Enumerable.Repeat("这里是一段不该出现在 index 的真源正文。", 30)) + "\n");
            }), "M-F1", "long non-navigation prose on a modeling index");

            // M-F2: same-name file and folder coexisting in the modeling tree.
            ExpectRule(await MutateAsync(async dir =>
            {
                var folder = Path.Combine(dir, billingContext, "workflows", "coreflow");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "index.md"), domainFrontmatter + "\n# CoreFlow\n\n> 目录职责 · holds: h · excludes: x · tier: stable · ⬆from: a · ⬇to: b\n");
            }), "M-F2", "a sibling same-name file and folder under modeling");

            // M-F3: a domain glossary term referencing derived_from.
            ExpectRule(await MutateAsync(async dir =>
            {
                var path = Path.Combine(dir, "modeling", "domain", "glossary.md");
                await File.AppendAllTextAsync(path, "\n- BadTerm：见 derived_from 来源清单。\n");
            }), "M-F3", "a domain glossary term referencing derived_from");

            // M-F3: a derived glossary term without a resolvable link back to domain.
            ExpectRule(await MutateAsync(async dir =>
            {
                await WriteDerivedPlaneAsync(dir, "modeling/domain/contexts/coreservice/objects/coreservice.md");
                var path = Path.Combine(dir, "modeling", "runtime", "glossary.md");
                await File.AppendAllTextAsync(path, "- Orphan：无回链术语。\n");
            }), "M-F3", "a derived glossary term without a domain backlink");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }
}
