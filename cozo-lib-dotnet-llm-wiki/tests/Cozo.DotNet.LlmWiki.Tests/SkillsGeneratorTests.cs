using System.Text.RegularExpressions;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// Skills generation suite (add-llm-wiki-skills-generation track T1.1, delta
/// behavior://llm-wiki-tools/requirements/skills-generation): cases per-context-skill
/// (frontmatter + key symbols file:line + tool examples, no #N degenerate names),
/// workflow-skills (exploring/impact/depa generic skills) and deterministic-bounded
/// (double run byte-identical, MaxSkills + per-skill char budget enforced). The
/// own-prefix discipline (user directories untouched, stale depa-wiki-* directories
/// synced away) is asserted at the generator layer here; the CLI wiring is P2 (T2.1).
/// Zero LLM by design.
/// </summary>
internal static class SkillsGeneratorTests
{
    /// <summary>Degenerate community-label shape that must never leak into skill names or content.</summary>
    private static readonly Regex DegenerateNamePattern = new(@"#\d", RegexOptions.CultureInvariant);

    /// <summary>
    /// Same community fixture shape as the modeling-fractal suite: two Acme.Billing
    /// communities merge by namespace LCP, Acme.Shipping is a plain prefix community, and
    /// the degenerate "#4" community splits into Zeta / Ymir / Standalone via the level-2/3
    /// fallbacks. One process (BillInvoice) enters Acme.Billing; InvoiceService calls the
    /// cross-context Standalone symbol (call-structure section evidence).
    /// </summary>
    private static async Task<CozoOm> BuildFixtureAsync(CozoDb db)
    {
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Files:
            [
                new CodeFileFact("file:sk:bil", "repo:sk", "src/Billing.cs"),
                new CodeFileFact("file:sk:shp", "repo:sk", "src/Shipping.cs"),
                new CodeFileFact("file:sk:x", "repo:sk", "src/Cross.cs")
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:sk:bil:inv", "file:sk:bil", "Invoice", "class", 3, 20, "Invoice",
                    Visibility: "public", Exported: true, SymKey: "csharp:Acme.Billing.Invoice#0"),
                new CodeSymbolFact("symbol:sk:bil:svc", "file:sk:bil", "InvoiceService", "class", 22, 40, "InvoiceService",
                    SymKey: "csharp:Acme.Billing.InvoiceService#0"),
                new CodeSymbolFact("symbol:sk:bil:pay", "file:sk:bil", "Payment", "class", 42, 60, "Payment",
                    Visibility: "public", Exported: true, SymKey: "csharp:Acme.Billing.Payment#0"),
                new CodeSymbolFact("symbol:sk:bil:ipay", "file:sk:bil", "IPayable", "interface", 62, 70, "IPayable",
                    Visibility: "public", Exported: true, SymKey: "csharp:Acme.Billing.IPayable#0"),
                new CodeSymbolFact("symbol:sk:shp:parcel", "file:sk:shp", "Parcel", "class", 3, 20, "Parcel",
                    SymKey: "csharp:Acme.Shipping.Parcel#0"),
                new CodeSymbolFact("symbol:sk:shp:router", "file:sk:shp", "Router", "class", 22, 40, "Router",
                    SymKey: "csharp:Acme.Shipping.Router#0"),
                new CodeSymbolFact("symbol:sk:x:za", "file:sk:x", "Alpha", "class", 3, 10, "Alpha",
                    SymKey: "csharp:Zeta.Alpha#0"),
                new CodeSymbolFact("symbol:sk:x:yb", "file:sk:x", "Beta", "class", 12, 20, "Beta",
                    SymKey: "csharp:Ymir.Beta#0"),
                new CodeSymbolFact("symbol:sk:x:aa", "file:sk:x", "HelperThing", "class", 22, 30, "HelperThing",
                    SymKey: "csharp:HelperThing#0"),
                new CodeSymbolFact("symbol:sk:x:sa", "file:sk:x", "Standalone", "class", 32, 40, "Standalone",
                    SymKey: "csharp:Standalone#0")
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:sk:bil:svc", "symbol:sk:bil:inv", CodeEdgeKinds.Calls, "file:sk:bil", 25, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:sk:bil:pay", "symbol:sk:bil:inv", CodeEdgeKinds.Calls, "file:sk:bil", 45, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:sk:shp:router", "symbol:sk:shp:parcel", CodeEdgeKinds.Calls, "file:sk:shp", 25, 0.9, "roslyn", ""),
                // Cross-context CALLS: Acme.Billing -> Standalone (call-structure top3 evidence).
                new CodeEdgeFact("symbol:sk:bil:svc", "symbol:sk:x:sa", CodeEdgeKinds.Calls, "file:sk:bil", 30, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:sk:x:sa", "symbol:sk:x:aa", CodeEdgeKinds.Calls, "file:sk:x", 35, 0.9, "roslyn", "")
            ]));
        using (db.Run(
            """
            ?[community_id, label, cohesion, symbol_count, algo] <- [
              ["community:s01", "Acme.Billing A", 0.8, 2, "louvain"],
              ["community:s02", "Acme.Billing B", 0.8, 1, "louvain"],
              ["community:s03", "Acme.Shipping", 0.7, 2, "louvain"],
              ["community:s04", "#4", 0.4, 4, "louvain"]]
            :put ck_community {community_id => label, cohesion, symbol_count, algo}
            """)) { }
        using (db.Run(
            """
            ?[symbol_id, community_id] <- [
              ["symbol:sk:bil:inv", "community:s01"], ["symbol:sk:bil:svc", "community:s01"],
              ["symbol:sk:bil:ipay", "community:s01"],
              ["symbol:sk:bil:pay", "community:s02"],
              ["symbol:sk:shp:parcel", "community:s03"], ["symbol:sk:shp:router", "community:s03"],
              ["symbol:sk:x:za", "community:s04"], ["symbol:sk:x:yb", "community:s04"],
              ["symbol:sk:x:aa", "community:s04"], ["symbol:sk:x:sa", "community:s04"]]
            :put ck_member {symbol_id => community_id}
            """)) { }
        using (db.Run(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
              ["process:sk01", "BillInvoice", "symbol:sk:bil:svc", "public_api", "public_api", 3]]
            :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
            """)) { }
        using (db.Run(
            """
            ?[process_id, step, symbol_id, via_kind] <- [
              ["process:sk01", 0, "symbol:sk:bil:svc", ""],
              ["process:sk01", 1, "symbol:sk:bil:inv", "CALLS"],
              ["process:sk01", 2, "symbol:sk:x:sa", "CALLS"]]
            :put ck_process_step {process_id, step => symbol_id, via_kind}
            """)) { }
        return om;
    }

    /// <summary>Splits a SKILL.md into (frontmatter lines, body); null when there is no frontmatter block.</summary>
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

        return (content[4..end].Split('\n'), content[(end + 5)..]);
    }

    private static Dictionary<string, string> FrontmatterFields(IReadOnlyList<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return fields;
    }

    private static string[] SkillDirectories(string target) =>
        Directory.GetDirectories(target)
            .Select(dir => Path.GetFileName(dir)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"skills-gen-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = await BuildFixtureAsync(db);
            var generator = new SkillsGenerator();

            // Target with a pre-existing user skill (never touched) and a stale own-prefix
            // directory (synced away because its context no longer exists).
            var target = Path.Combine(root, "skills");
            Directory.CreateDirectory(Path.Combine(target, "my-notes"));
            const string userSkillContent = "---\nname: my-notes\n---\nhand-written, do not touch\n";
            await File.WriteAllTextAsync(Path.Combine(target, "my-notes", "SKILL.md"), userSkillContent);
            Directory.CreateDirectory(Path.Combine(target, "depa-wiki-obsolete"));
            await File.WriteAllTextAsync(Path.Combine(target, "depa-wiki-obsolete", "SKILL.md"), "stale\n");

            var result = await generator.GenerateAsync(om, new SkillsOptions(target));

            // ---- case per-context-skill: one skill per discovered context, no #N names.
            var expectedContextSkills = new[]
            {
                "depa-wiki-acme-billing", "depa-wiki-acme-shipping", "depa-wiki-standalone",
                "depa-wiki-ymir", "depa-wiki-zeta",
            };
            var expectedWorkflowSkills = new[] { "depa-wiki-depa", "depa-wiki-exploring", "depa-wiki-impact" };
            var dirs = SkillDirectories(target);
            assert(dirs.SequenceEqual(expectedContextSkills
                    .Concat(expectedWorkflowSkills)
                    .Concat(["my-notes"])
                    .Order(StringComparer.Ordinal)),
                "per-context-skill: target should carry one depa-wiki-<slug> skill per context plus the three "
                + "workflow skills and the untouched user directory (got: " + string.Join(", ", dirs) + ")");
            assert(!dirs.Any(dir => DegenerateNamePattern.IsMatch(dir)),
                "per-context-skill: no skill directory may carry a #N degenerate context name");

            var billing = await File.ReadAllTextAsync(Path.Combine(target, "depa-wiki-acme-billing", "SKILL.md"));
            var billingParts = SplitFrontmatter(billing);
            assert(billingParts is not null, "per-context-skill: SKILL.md must start with a frontmatter block");
            var billingFields = FrontmatterFields(billingParts!.Value.Frontmatter);
            assert(billingFields.GetValueOrDefault("name") == "depa-wiki-acme-billing",
                "per-context-skill: frontmatter name should be depa-wiki-<context-slug> (got '"
                + billingFields.GetValueOrDefault("name") + "')");
            var billingDescription = billingFields.GetValueOrDefault("description", "");
            assert(billingDescription.Contains("Acme.Billing", StringComparison.Ordinal)
                    && billingDescription.Contains("src/", StringComparison.Ordinal),
                "per-context-skill: the description should carry the context name and the main path prefix (got '"
                + billingDescription + "')");

            // Key symbols: top-degree first, `name` — kind (path:line).
            assert(billing.Contains("`Invoice` — class (src/Billing.cs:3)", StringComparison.Ordinal)
                    && billing.Contains("`InvoiceService` — class (src/Billing.cs:22)", StringComparison.Ordinal),
                "per-context-skill: the key-symbols section should render `name` — kind (path:line) rows");
            assert(billing.IndexOf("`Invoice` —", StringComparison.Ordinal)
                    < billing.IndexOf("`Payment` —", StringComparison.Ordinal),
                "per-context-skill: key symbols should be ordered by weighted degree desc");

            // Call structure: the cross-context dependency into Standalone.
            assert(billing.Contains("Standalone", StringComparison.Ordinal)
                    && billing.Contains("CALLS", StringComparison.Ordinal),
                "per-context-skill: the call-structure section should surface the cross-context CALLS dependency");

            // Processes: the flow entering this context with its entry symbol.
            assert(billing.Contains("BillInvoice", StringComparison.Ordinal)
                    && billing.Contains("`InvoiceService` (src/Billing.cs:22)", StringComparison.Ordinal),
                "per-context-skill: the processes section should list entering flows with entry file:line");

            // Tool examples with real parameters from this graph.
            assert(billing.Contains("semantic_search", StringComparison.Ordinal)
                    && billing.Contains("\"query\": \"Acme.Billing\"", StringComparison.Ordinal),
                "per-context-skill: the explore section should carry a semantic_search example with the context name");
            assert(billing.Contains("symbol_context", StringComparison.Ordinal)
                    && billing.Contains("\"symbolId\": \"symbol:sk:bil:inv\"", StringComparison.Ordinal),
                "per-context-skill: the explore section should carry a symbol_context example with a real symbol id");
            assert(billing.Contains("\"from\": \"Invoice\"", StringComparison.Ordinal)
                    && billing.Contains("\"to\": \"InvoiceService\"", StringComparison.Ordinal),
                "per-context-skill: the explore section should carry a trace example with real symbol names");

            // No #N degenerate name anywhere in any generated skill.
            foreach (var skill in expectedContextSkills)
            {
                var content = await File.ReadAllTextAsync(Path.Combine(target, skill, "SKILL.md"));
                assert(!DegenerateNamePattern.IsMatch(content),
                    $"per-context-skill: {skill}/SKILL.md must not carry a #N degenerate context name");
            }

            // ---- case workflow-skills: the three generic skills with tool matrix + steps.
            var exploring = await File.ReadAllTextAsync(Path.Combine(target, "depa-wiki-exploring", "SKILL.md"));
            var exploringFields = FrontmatterFields(SplitFrontmatter(exploring)!.Value.Frontmatter);
            assert(exploringFields.GetValueOrDefault("name") == "depa-wiki-exploring",
                "workflow-skills: depa-wiki-exploring should carry its frontmatter name");
            string[] toolMatrix =
            [
                "index_repo", "build_wiki", "symbol_context", "impact_of_change", "docs_for_code",
                "explain_relation", "parser_status", "parse_file", "index_embeddings", "semantic_search",
                "overview_graph", "query_named", "trace", "check", "detect_changes",
                "depa_conformance", "fact_grade_map", "health_score",
            ];
            assert(toolMatrix.All(tool => exploring.Contains($"`{tool}`", StringComparison.Ordinal)),
                "workflow-skills: depa-wiki-exploring should list the full 18-tool matrix");
            var impact = await File.ReadAllTextAsync(Path.Combine(target, "depa-wiki-impact", "SKILL.md"));
            assert(FrontmatterFields(SplitFrontmatter(impact)!.Value.Frontmatter).GetValueOrDefault("name") == "depa-wiki-impact"
                    && impact.Contains("impact_of_change", StringComparison.Ordinal)
                    && impact.Contains("trace", StringComparison.Ordinal)
                    && impact.Contains("check", StringComparison.Ordinal),
                "workflow-skills: depa-wiki-impact should describe the impact workflow tools");
            var depa = await File.ReadAllTextAsync(Path.Combine(target, "depa-wiki-depa", "SKILL.md"));
            assert(FrontmatterFields(SplitFrontmatter(depa)!.Value.Frontmatter).GetValueOrDefault("name") == "depa-wiki-depa"
                    && depa.Contains("depa_conformance", StringComparison.Ordinal)
                    && depa.Contains("fact_grade_map", StringComparison.Ordinal)
                    && depa.Contains("health_score", StringComparison.Ordinal),
                "workflow-skills: depa-wiki-depa should describe the three DEPA tools");
            // A small graph number is interpolated into the generic templates.
            assert(exploring.Contains("10 symbols", StringComparison.Ordinal),
                "workflow-skills: the exploring template should interpolate the graph symbol count");

            // ---- own-prefix discipline at the generator layer: user directory untouched,
            // stale own-prefix directory synced away (CLI wiring is P2).
            assert(await File.ReadAllTextAsync(Path.Combine(target, "my-notes", "SKILL.md")) == userSkillContent,
                "own-prefix: a user skill directory without the depa-wiki- prefix must stay byte-identical");
            assert(!Directory.Exists(Path.Combine(target, "depa-wiki-obsolete")),
                "own-prefix: a stale depa-wiki-* directory whose context disappeared must be cleaned on generate");
            assert(result.Cleaned.SequenceEqual(["depa-wiki-obsolete"]),
                "own-prefix: the result should report exactly the synced-away own-prefix directories (got: "
                + string.Join(", ", result.Cleaned) + ")");
            assert(result.Generated.Order(StringComparer.Ordinal)
                    .SequenceEqual(expectedContextSkills.Concat(expectedWorkflowSkills).Order(StringComparer.Ordinal)),
                "result: Generated should list every written skill (got: " + string.Join(", ", result.Generated) + ")");
            assert(result.Truncated == 0,
                "result: the default 4000-char budget should not truncate the fixture skills");

            // ---- case deterministic-bounded: double run byte-identical + bounded output.
            var target2 = Path.Combine(root, "skills-2");
            await generator.GenerateAsync(om, new SkillsOptions(target2));
            foreach (var skill in expectedContextSkills.Concat(expectedWorkflowSkills))
            {
                var first = await File.ReadAllTextAsync(Path.Combine(target, skill, "SKILL.md"));
                var second = await File.ReadAllTextAsync(Path.Combine(target2, skill, "SKILL.md"));
                assert(first == second,
                    $"deterministic-bounded: double generate should be byte-identical for {skill}/SKILL.md");
                assert(first.Length <= 4000,
                    $"deterministic-bounded: {skill}/SKILL.md should respect the 4000-char default budget");
            }

            // Regenerating into the same directory is idempotent (own skills overwritten in place).
            var again = await generator.GenerateAsync(om, new SkillsOptions(target));
            assert(again.Cleaned.Count == 0 && again.Generated.Count == result.Generated.Count,
                "deterministic-bounded: an idempotent re-generate should rewrite own skills and clean nothing");
            assert(await File.ReadAllTextAsync(Path.Combine(target, "my-notes", "SKILL.md")) == userSkillContent,
                "deterministic-bounded: the user skill directory must survive re-generates untouched");

            // ---- budget enforcement: a tight per-skill budget truncates with an annotation.
            var tightTarget = Path.Combine(root, "skills-tight");
            var tight = await generator.GenerateAsync(om, new SkillsOptions(tightTarget, BudgetPerSkill: 400));
            assert(tight.Truncated > 0,
                "budget: a tight per-skill budget should report truncated skills");
            foreach (var skill in SkillDirectories(tightTarget))
            {
                var content = await File.ReadAllTextAsync(Path.Combine(tightTarget, skill, "SKILL.md"));
                assert(content.Length <= 400,
                    $"budget: {skill}/SKILL.md must not exceed BudgetPerSkill=400 chars (got {content.Length})");
            }

            var tightBilling = await File.ReadAllTextAsync(Path.Combine(tightTarget, "depa-wiki-acme-billing", "SKILL.md"));
            assert(tightBilling.Contains("truncated", StringComparison.Ordinal),
                "budget: a truncated skill should carry an explicit truncation annotation");

            // ---- MaxSkills bound: context skills beyond the cap are dropped (largest first);
            // the three workflow skills always ride on top.
            var cappedTarget = Path.Combine(root, "skills-capped");
            await generator.GenerateAsync(om, new SkillsOptions(cappedTarget, MaxSkills: 2));
            var cappedDirs = SkillDirectories(cappedTarget);
            assert(cappedDirs.SequenceEqual(new[]
                {
                    "depa-wiki-acme-billing", "depa-wiki-acme-shipping",
                    "depa-wiki-depa", "depa-wiki-exploring", "depa-wiki-impact",
                }),
                "bounded: MaxSkills=2 should keep the two largest contexts plus the three workflow skills (got: "
                + string.Join(", ", cappedDirs) + ")");
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
