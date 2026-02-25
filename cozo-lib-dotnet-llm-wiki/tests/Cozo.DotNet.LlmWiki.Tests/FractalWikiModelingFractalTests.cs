using System.Text.RegularExpressions;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// Modeling fractal skeleton suite (add-llm-wiki-modeling-fractal track T1.1, delta
/// behavior://llm-wiki-pipeline/requirements/modeling-fractal): cases
/// context-discovery-no-degenerate (three-level fallback naming, no #N, double-run
/// deterministic) and incremental-shared (second unchanged build skips every modeling page),
/// plus skeleton structure assertions (modeling/domain/contexts hierarchy, 目录职责 blocks,
/// context frontmatter, doc_role=canonical, empty categories not created). Pure structure
/// layer — no LLM, pinned communities. objects/workflows leaf pages are P2 (T2.1).
/// </summary>
internal static class FractalWikiModelingFractalTests
{
    /// <summary>Slim manifest block regex (fixtures E-D2 shape, reused on the modeling side).</summary>
    private static readonly Regex SlimManifestPattern = new(
        @"^> 目录职责 · holds: .+ · excludes: .+ · tier: (stable|dated)( · ⬆from: .+)?( · ⬇to: .+)?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Degenerate community-label shape that must never leak into context names (#N ban).</summary>
    private static readonly Regex DegenerateNamePattern = new(@"#\d", RegexOptions.CultureInvariant);

    /// <summary>
    /// Fixture exercising all three context-naming fallback levels:
    /// - community m01 + m02 share the qualified-name LCP "Acme.Billing" (same-prefix merge);
    /// - community m03 is "Acme.Shipping" (plain namespace-prefix community);
    /// - community m04 (degenerate label "#4") is cross-root: Zeta.Alpha / Ymir.Beta split by
    ///   top-level namespace segment, plus two namespace-less members (Standalone / HelperThing)
    ///   that fall back to the highest-weighted-degree type name (Standalone: degree 2 beats
    ///   HelperThing: degree 1 despite HelperThing's smaller symbol id).
    /// One process enters Acme.Billing (workflows category); Invoice/Payment are public types
    /// (objects category); every other context has neither category (empty categories not built).
    /// </summary>
    private static async Task<CozoOm> BuildFixtureAsync(CozoDb db)
    {
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Files:
            [
                new CodeFileFact("file:mf:bil", "repo:mf", "src/Billing.cs"),
                new CodeFileFact("file:mf:shp", "repo:mf", "src/Shipping.cs"),
                new CodeFileFact("file:mf:x", "repo:mf", "src/Cross.cs")
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:mf:bil:inv", "file:mf:bil", "Invoice", "class", 3, 20, "Invoice",
                    Visibility: "public", Exported: true, SymKey: "csharp:Acme.Billing.Invoice#0"),
                new CodeSymbolFact("symbol:mf:bil:svc", "file:mf:bil", "InvoiceService", "class", 22, 40, "InvoiceService",
                    SymKey: "csharp:Acme.Billing.InvoiceService#0"),
                new CodeSymbolFact("symbol:mf:bil:pay", "file:mf:bil", "Payment", "class", 42, 60, "Payment",
                    Visibility: "public", Exported: true, SymKey: "csharp:Acme.Billing.Payment#0"),
                new CodeSymbolFact("symbol:mf:bil:ipay", "file:mf:bil", "IPayable", "interface", 62, 70, "IPayable",
                    Visibility: "public", Exported: true, SymKey: "csharp:Acme.Billing.IPayable#0"),
                // Type member (objects leaf member table): parent_id containment, not a community member.
                new CodeSymbolFact("symbol:mf:bil:inv:add", "file:mf:bil", "AddLine", "method", 5, 8, "AddLine(Line)",
                    ParentId: "symbol:mf:bil:inv", SymKey: "csharp:Acme.Billing.Invoice.AddLine#1"),
                new CodeSymbolFact("symbol:mf:shp:parcel", "file:mf:shp", "Parcel", "class", 3, 20, "Parcel",
                    SymKey: "csharp:Acme.Shipping.Parcel#0"),
                new CodeSymbolFact("symbol:mf:shp:router", "file:mf:shp", "Router", "class", 22, 40, "Router",
                    SymKey: "csharp:Acme.Shipping.Router#0"),
                new CodeSymbolFact("symbol:mf:x:za", "file:mf:x", "Alpha", "class", 3, 10, "Alpha",
                    SymKey: "csharp:Zeta.Alpha#0"),
                new CodeSymbolFact("symbol:mf:x:yb", "file:mf:x", "Beta", "class", 12, 20, "Beta",
                    SymKey: "csharp:Ymir.Beta#0"),
                // Namespace-less pair: HelperThing gets the smaller symbol id on purpose so the
                // level-3 pick is decided by weighted degree, not id order.
                new CodeSymbolFact("symbol:mf:x:aa", "file:mf:x", "HelperThing", "class", 22, 30, "HelperThing",
                    SymKey: "csharp:HelperThing#0"),
                new CodeSymbolFact("symbol:mf:x:sa", "file:mf:x", "Standalone", "class", 32, 40, "Standalone",
                    SymKey: "csharp:Standalone#0")
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:mf:bil:svc", "symbol:mf:bil:inv", CodeEdgeKinds.Calls, "file:mf:bil", 25, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:mf:bil:pay", "symbol:mf:bil:inv", CodeEdgeKinds.Calls, "file:mf:bil", 45, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:mf:shp:router", "symbol:mf:shp:parcel", CodeEdgeKinds.Calls, "file:mf:shp", 25, 0.9, "roslyn", ""),
                // Standalone: degree 2 (called by InvoiceService, calls HelperThing); HelperThing: degree 1.
                new CodeEdgeFact("symbol:mf:bil:svc", "symbol:mf:x:sa", CodeEdgeKinds.Calls, "file:mf:bil", 30, 0.9, "roslyn", ""),
                new CodeEdgeFact("symbol:mf:x:sa", "symbol:mf:x:aa", CodeEdgeKinds.Calls, "file:mf:x", 35, 0.9, "roslyn", ""),
                // Inheritance (objects leaf EXTENDS/IMPLEMENTS section); does not feed CALLS/IMPORTS degrees.
                new CodeEdgeFact("symbol:mf:bil:inv", "symbol:mf:bil:ipay", CodeEdgeKinds.Implements, "file:mf:bil", 3, 1.0, "roslyn", "")
            ]));
        using (db.Run(
            """
            ?[community_id, label, cohesion, symbol_count, algo] <- [
              ["community:m01", "Acme.Billing A", 0.8, 2, "louvain"],
              ["community:m02", "Acme.Billing B", 0.8, 1, "louvain"],
              ["community:m03", "Acme.Shipping", 0.7, 2, "louvain"],
              ["community:m04", "#4", 0.4, 4, "louvain"]]
            :put ck_community {community_id => label, cohesion, symbol_count, algo}
            """)) { }
        using (db.Run(
            """
            ?[symbol_id, community_id] <- [
              ["symbol:mf:bil:inv", "community:m01"], ["symbol:mf:bil:svc", "community:m01"],
              ["symbol:mf:bil:ipay", "community:m01"],
              ["symbol:mf:bil:pay", "community:m02"],
              ["symbol:mf:shp:parcel", "community:m03"], ["symbol:mf:shp:router", "community:m03"],
              ["symbol:mf:x:za", "community:m04"], ["symbol:mf:x:yb", "community:m04"],
              ["symbol:mf:x:aa", "community:m04"], ["symbol:mf:x:sa", "community:m04"]]
            :put ck_member {symbol_id => community_id}
            """)) { }
        using (db.Run(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
              ["process:mf01", "BillInvoice", "symbol:mf:bil:svc", "public_api", "public_api", 3]]
            :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
            """)) { }
        using (db.Run(
            """
            ?[process_id, step, symbol_id, via_kind] <- [
              ["process:mf01", 0, "symbol:mf:bil:svc", ""],
              ["process:mf01", 1, "symbol:mf:bil:inv", "CALLS"],
              ["process:mf01", 2, "symbol:mf:x:sa", "CALLS"]]
            :put ck_process_step {process_id, step => symbol_id, via_kind}
            """)) { }
        return om;
    }

    /// <summary>Splits a page into (frontmatter lines, body); null when the page has no frontmatter.</summary>
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

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fractal-mod-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = await BuildFixtureAsync(db);
            var pipeline = new FractalWikiPipeline();

            var outDir = Path.Combine(root, "docs-preview");
            var result = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: outDir));

            // ---- case context-discovery-no-degenerate: 三级兜底命名，禁 #N。
            var contextsDir = Path.Combine(outDir, "modeling", "domain", "contexts");
            assert(Directory.Exists(contextsDir),
                "context-discovery: modeling/domain/contexts/ should exist");
            var contextSlugs = Directory.GetDirectories(contextsDir)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(contextSlugs.SequenceEqual(new[] { "acme-billing", "acme-shipping", "standalone", "ymir", "zeta" }),
                "context-discovery: three-level fallback should yield exactly acme-billing/acme-shipping/standalone/ymir/zeta (got: "
                + string.Join(", ", contextSlugs!) + ")");
            assert(!contextSlugs.Any(slug => DegenerateNamePattern.IsMatch(slug!)),
                "context-discovery: no context slug may carry a #N degenerate form");
            var contextsIndex = await File.ReadAllTextAsync(Path.Combine(contextsDir, "index.md"));
            assert(contextsIndex.Contains("Acme.Billing", StringComparison.Ordinal)
                    && contextsIndex.Contains("Acme.Shipping", StringComparison.Ordinal)
                    && contextsIndex.Contains("Standalone", StringComparison.Ordinal)
                    && contextsIndex.Contains("Ymir", StringComparison.Ordinal)
                    && contextsIndex.Contains("Zeta", StringComparison.Ordinal),
                "context-discovery: contexts/index.md should list every discovered context by name");
            assert(!DegenerateNamePattern.IsMatch(contextsIndex),
                "context-discovery: the degenerate community label '#4' must not leak into contexts/index.md");

            // Same-prefix merge: the two Acme.Billing communities become ONE context carrying all
            // three members (level-3 leftovers Standalone/HelperThing stay in their own context).
            var billingCodeMap = await File.ReadAllTextAsync(Path.Combine(contextsDir, "acme-billing", "code-map.md"));
            assert(billingCodeMap.Contains("`Invoice`", StringComparison.Ordinal)
                    && billingCodeMap.Contains("`InvoiceService`", StringComparison.Ordinal)
                    && billingCodeMap.Contains("`Payment`", StringComparison.Ordinal),
                "context-discovery: same-prefix communities should merge into one Acme.Billing context");
            var standaloneCodeMap = await File.ReadAllTextAsync(Path.Combine(contextsDir, "standalone", "code-map.md"));
            assert(standaloneCodeMap.Contains("`Standalone`", StringComparison.Ordinal)
                    && standaloneCodeMap.Contains("`HelperThing`", StringComparison.Ordinal),
                "context-discovery: level-3 fallback should keep both namespace-less members in the Standalone context");

            // ---- 双跑确定性: a second build into a fresh directory reproduces every modeling
            // page byte-for-byte (the whole discovery + rendering chain is deterministic).
            var outDir2 = Path.Combine(root, "docs-preview-2");
            await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: outDir2));
            var modelingPages = Directory.GetFiles(Path.Combine(outDir, "modeling"), "*.md", SearchOption.AllDirectories)
                .Select(page => Path.GetRelativePath(outDir, page))
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(modelingPages.Length > 0, "determinism: the build should emit modeling pages");
            foreach (var relative in modelingPages)
            {
                var twin = Path.Combine(outDir2, relative);
                assert(File.Exists(twin), $"determinism: double build should emit {relative} in both runs");
                assert(await File.ReadAllTextAsync(Path.Combine(outDir, relative)) == await File.ReadAllTextAsync(twin),
                    $"determinism: double build should be verbatim-identical for {relative}");
            }

            // ---- skeleton structure: modeling/index + domain/index + contexts/index carry the
            // full 目录职责 section (structure nodes, tier stable).
            foreach (var structural in new[]
            {
                Path.Combine("modeling", "index.md"),
                Path.Combine("modeling", "domain", "index.md"),
                Path.Combine("modeling", "domain", "contexts", "index.md"),
            })
            {
                var content = await File.ReadAllTextAsync(Path.Combine(outDir, structural));
                assert(content.Contains("## 目录职责", StringComparison.Ordinal)
                        && content.Contains("**holds**", StringComparison.Ordinal)
                        && content.Contains("**excludes**", StringComparison.Ordinal)
                        && content.Contains("**tier**：`stable`", StringComparison.Ordinal)
                        && content.Contains("**promotes_from**", StringComparison.Ordinal)
                        && content.Contains("**promotes_to**", StringComparison.Ordinal),
                    $"skeleton: structural node {structural} must carry the full 目录职责 section");
            }

            // The modeling first level carries only the domain plane; the plane level carries
            // only contexts/ (no stray leaves besides index.md).
            var modelingDirs = Directory.GetDirectories(Path.Combine(outDir, "modeling"))
                .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            assert(modelingDirs.SequenceEqual(new[] { "domain" }),
                "skeleton: modeling/ should carry exactly the domain plane (got: " + string.Join(", ", modelingDirs!) + ")");

            // ---- per-context skeleton: index.md (full manifest + Boundary + canonical + context
            // frontmatter) and code-map.md (member type table); category indexes only when non-empty.
            var billingIndex = await File.ReadAllTextAsync(Path.Combine(contextsDir, "acme-billing", "index.md"));
            var billingParts = SplitFrontmatter(billingIndex);
            assert(billingParts is not null, "skeleton: context index.md must start with frontmatter");
            var billingFields = FrontmatterFields(billingParts!.Value.Frontmatter);
            assert(billingFields.GetValueOrDefault("knowledge_plane") == "domain",
                "skeleton: context frontmatter knowledge_plane should be domain");
            assert(billingFields.GetValueOrDefault("doc_role") == "canonical",
                "skeleton: context index doc_role should be canonical (domain truth source)");
            assert(billingFields.GetValueOrDefault("context") == "Acme.Billing",
                "skeleton: context frontmatter should carry context: <name> (got '"
                + billingFields.GetValueOrDefault("context") + "')");
            assert(!billingFields.ContainsKey("knowledge_system"),
                "skeleton: knowledge_plane is expressed by the path — no knowledge_system field on modeling pages");
            assert(Regex.IsMatch(billingFields.GetValueOrDefault("last_verified", ""), @"^\d{4}-\d{2}-\d{2}$"),
                "skeleton: context frontmatter last_verified must be YYYY-MM-DD");
            assert(billingIndex.Contains("## 目录职责", StringComparison.Ordinal),
                "skeleton: context index.md should carry the full 目录职责 section");
            assert(billingIndex.Contains("## Boundary", StringComparison.Ordinal),
                "skeleton: context index.md should carry the mandatory Boundary section");
            assert(billingIndex.Contains("objects/index.md", StringComparison.Ordinal)
                    && billingIndex.Contains("workflows/index.md", StringComparison.Ordinal)
                    && billingIndex.Contains("code-map.md", StringComparison.Ordinal),
                "skeleton: context index.md should navigate to its categories and code-map");

            // code-map.md: member table with file:line locations (type listing for T1.1; P2 adds
            // objects leaf links).
            assert(billingCodeMap.Contains("| `Invoice` | class | `src/Billing.cs:3` |", StringComparison.Ordinal),
                "skeleton: context code-map should list members with file:line locations");
            var billingCodeMapFields = FrontmatterFields(SplitFrontmatter(billingCodeMap)!.Value.Frontmatter);
            assert(billingCodeMapFields.GetValueOrDefault("context") == "Acme.Billing"
                    && billingCodeMapFields.GetValueOrDefault("doc_role") == "reference",
                "skeleton: code-map.md should carry context frontmatter and doc_role reference");

            // objects/: public types only (Invoice + Payment; internal InvoiceService excluded).
            var billingObjects = await File.ReadAllTextAsync(Path.Combine(contextsDir, "acme-billing", "objects", "index.md"));
            assert(SlimManifestPattern.IsMatch(billingObjects),
                "skeleton: objects/index.md should carry a slim 目录职责 block");
            assert(billingObjects.Contains("`Invoice`", StringComparison.Ordinal)
                    && billingObjects.Contains("`Payment`", StringComparison.Ordinal),
                "skeleton: objects/index.md should list the context's public types");
            assert(!billingObjects.Contains("InvoiceService", StringComparison.Ordinal),
                "skeleton: objects/index.md must not list non-public types");
            var billingObjectsFields = FrontmatterFields(SplitFrontmatter(billingObjects)!.Value.Frontmatter);
            assert(billingObjectsFields.GetValueOrDefault("context") == "Acme.Billing",
                "skeleton: objects/index.md should carry the context frontmatter field");

            // workflows/: the flow entering this context.
            var billingWorkflows = await File.ReadAllTextAsync(Path.Combine(contextsDir, "acme-billing", "workflows", "index.md"));
            assert(SlimManifestPattern.IsMatch(billingWorkflows),
                "skeleton: workflows/index.md should carry a slim 目录职责 block");
            assert(billingWorkflows.Contains("BillInvoice", StringComparison.Ordinal),
                "skeleton: workflows/index.md should list the flows entering this context");

            // ---- T2.1 objects leaves (delta case objects-leaf): one page per paged public type,
            // pure structure layer (zero LLM), members/inheritance/flows/file:line + narrative slot.
            var billingObjectFiles = Directory.GetFiles(Path.Combine(contextsDir, "acme-billing", "objects"), "*.md")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(billingObjectFiles.SequenceEqual(new[] { "index.md", "invoice.md", "ipayable.md", "payment.md" }),
                "objects-leaf: every public type of the context should get a leaf page (got: "
                + string.Join(", ", billingObjectFiles!) + ")");
            assert(billingObjects.Contains("[invoice.md](invoice.md)", StringComparison.Ordinal)
                    && billingObjects.Contains("[payment.md](payment.md)", StringComparison.Ordinal),
                "objects-leaf: objects/index.md should navigate to the type leaves");
            var invoiceLeaf = await File.ReadAllTextAsync(Path.Combine(contextsDir, "acme-billing", "objects", "invoice.md"));
            var invoiceFields = FrontmatterFields(SplitFrontmatter(invoiceLeaf)!.Value.Frontmatter);
            assert(invoiceFields.GetValueOrDefault("doc_role") == "canonical"
                    && invoiceFields.GetValueOrDefault("context") == "Acme.Billing"
                    && invoiceFields.GetValueOrDefault("knowledge_plane") == "domain",
                "objects-leaf: leaf frontmatter should be canonical + context (domain truth source)");
            assert(invoiceLeaf.Contains("- kind: `class`", StringComparison.Ordinal)
                    && invoiceLeaf.Contains("- signature: `Invoice`", StringComparison.Ordinal)
                    && invoiceLeaf.Contains("- declared at: `src/Billing.cs:3`", StringComparison.Ordinal),
                "objects-leaf: the leaf should carry kind/signature and the file:line declaration site");
            assert(invoiceLeaf.Contains("| `AddLine` | method | `src/Billing.cs:5` |", StringComparison.Ordinal),
                "objects-leaf: the member table should list parent_id members with name/kind/file:line");
            assert(invoiceLeaf.Contains("IMPLEMENTS `IPayable`", StringComparison.Ordinal),
                "objects-leaf: the inheritance section should render EXTENDS/IMPLEMENTS relations");
            assert(invoiceLeaf.Contains("- BillInvoice (`process:mf01`)", StringComparison.Ordinal),
                "objects-leaf: participating execution flows (≤5) should be listed");
            assert(invoiceLeaf.Contains("[context code map](../code-map.md)", StringComparison.Ordinal),
                "objects-leaf: the leaf should link the context code-map");
            assert(invoiceLeaf.Contains("<!-- llm:begin narrative -->", StringComparison.Ordinal)
                    && invoiceLeaf.Contains("_pending: llm unavailable_", StringComparison.Ordinal)
                    && invoiceLeaf.Contains("<!-- llm:end -->", StringComparison.Ordinal),
                "objects-leaf: the narrative slot should be explicitly marked and pending without an LLM");
            assert(SlimManifestPattern.IsMatch(invoiceLeaf),
                "objects-leaf: the leaf should carry a slim 目录职责 block");

            // code-map links the paged objects leaves (design item 3).
            assert(billingCodeMap.Contains("[objects/invoice.md](objects/invoice.md)", StringComparison.Ordinal),
                "objects-leaf: the context code-map should link the type's objects page");

            // ---- T2.1 workflows leaves (delta case workflows-leaf): step chain consistent with
            // ck_process_step, cross-context annotation, entry facts, narrative slot.
            var billInvoiceLeaf = await File.ReadAllTextAsync(Path.Combine(contextsDir, "acme-billing", "workflows", "billinvoice.md"));
            var billInvoiceFields = FrontmatterFields(SplitFrontmatter(billInvoiceLeaf)!.Value.Frontmatter);
            assert(billInvoiceFields.GetValueOrDefault("doc_role") == "canonical"
                    && billInvoiceFields.GetValueOrDefault("context") == "Acme.Billing",
                "workflows-leaf: leaf frontmatter should be canonical + context");
            assert(billingWorkflows.Contains("[billinvoice.md](billinvoice.md)", StringComparison.Ordinal),
                "workflows-leaf: workflows/index.md should navigate to the flow leaves");
            assert(billInvoiceLeaf.Contains("`InvoiceService`（public_api，`src/Billing.cs:22`）", StringComparison.Ordinal),
                "workflows-leaf: the entry section should carry the entry symbol, kind and file:line");
            var step0 = billInvoiceLeaf.IndexOf("| 0 | `InvoiceService` |  | `src/Billing.cs:22` | — |", StringComparison.Ordinal);
            var step1 = billInvoiceLeaf.IndexOf("| 1 | `Invoice` | CALLS | `src/Billing.cs:3` | — |", StringComparison.Ordinal);
            var step2 = billInvoiceLeaf.IndexOf("| 2 | `Standalone` | CALLS | `src/Cross.cs:32` | Standalone |", StringComparison.Ordinal);
            assert(step0 >= 0 && step1 > step0 && step2 > step1,
                "workflows-leaf: the step table should match ck_process_step order with file:line and cross-context annotation");
            assert(billInvoiceLeaf.Contains("<!-- llm:begin narrative -->", StringComparison.Ordinal)
                    && billInvoiceLeaf.Contains("_pending: llm unavailable_", StringComparison.Ordinal),
                "workflows-leaf: the narrative slot should be explicitly marked and pending without an LLM");

            // ---- T2.1 plane-root glossary (M-A2) and Not Owned Here (M-B2).
            var glossary = await File.ReadAllTextAsync(Path.Combine(outDir, "modeling", "domain", "glossary.md"));
            assert(glossary.Contains("[acme-billing](contexts/acme-billing/index.md)", StringComparison.Ordinal)
                    && glossary.Contains("Acme.Shipping", StringComparison.Ordinal),
                "glossary: the domain glossary should list every context as a linked canonical term");
            assert(!glossary.Contains("derived_from", StringComparison.Ordinal),
                "glossary: domain glossary terms must not reference derived_from (M-F3)");
            assert(billingIndex.Contains("## Not Owned Here", StringComparison.Ordinal)
                    && billingIndex.Contains("../acme-shipping/index.md", StringComparison.Ordinal),
                "context index: Not Owned Here should list adjacent contexts (M-B2)");

            // ---- m-rules-pass slice on the fixture output: the dual-fractal build is spec-clean
            // against the FULL checker (E-* + M-*), pure structure layer.
            var fixtureViolations = FractalSpecChecker.Check(outDir);
            assert(fixtureViolations.Count == 0,
                "m-rules: the fixture dual-fractal output must pass the full E-*+M-* checker (got: "
                + string.Join("; ", fixtureViolations.Select(v => $"{v.RuleId} {v.Path}: {v.Detail}")) + ")");

            // Empty categories are not built (G3 decision: 缺哪类就不建); policies is never built.
            assert(!Directory.Exists(Path.Combine(contextsDir, "acme-shipping", "objects")),
                "skeleton: a context without public types must not carry an objects/ category");
            assert(!Directory.Exists(Path.Combine(contextsDir, "acme-shipping", "workflows")),
                "skeleton: a context without entering flows must not carry a workflows/ category");
            assert(!contextSlugs.Any(slug => Directory.Exists(Path.Combine(contextsDir, slug!, "policies"))),
                "skeleton: policies/ is never built (track decision #1: no evidence source)");

            // Every modeling page: controlled frontmatter with knowledge_plane=domain; every
            // context-scoped page carries context:; no page content carries a #N context name.
            foreach (var relative in modelingPages)
            {
                var content = await File.ReadAllTextAsync(Path.Combine(outDir, relative));
                var parts = SplitFrontmatter(content);
                assert(parts is not null, $"skeleton: {relative} must start with a frontmatter block");
                var fields = FrontmatterFields(parts!.Value.Frontmatter);
                assert(fields.GetValueOrDefault("knowledge_plane") == "domain",
                    $"skeleton: {relative} knowledge_plane should be domain");
                assert(fields.GetValueOrDefault("status") == "active",
                    $"skeleton: {relative} status should be active");
                var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
                if (normalized.StartsWith("modeling/domain/contexts/", StringComparison.Ordinal)
                    && normalized != "modeling/domain/contexts/index.md")
                {
                    assert(fields.ContainsKey("context") && !DegenerateNamePattern.IsMatch(fields["context"]),
                        $"skeleton: context-scoped page {relative} must carry a non-degenerate context field");
                }
                else
                {
                    assert(!fields.ContainsKey("context"),
                        $"skeleton: non-context page {relative} must not carry a context field");
                }
            }

            // Root index navigates to the modeling knowledge system next to impl.
            var rootIndex = await File.ReadAllTextAsync(Path.Combine(outDir, "index.md"));
            assert(rootIndex.Contains("modeling/index.md", StringComparison.Ordinal),
                "skeleton: the docs root index should navigate to modeling/index.md");

            // ---- case incremental-shared: modeling pages ride the shared page-level cache —
            // the .meta.json covers them and an unchanged second build skips everything.
            var metaText = await File.ReadAllTextAsync(Path.Combine(outDir, ".meta.json"));
            foreach (var relative in modelingPages)
            {
                var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
                assert(metaText.Contains($"\"{normalized}\"", StringComparison.Ordinal),
                    $"incremental-shared: .meta.json must cover the modeling page {normalized}");
            }

            var second = await pipeline.BuildAsync(om, new FractalWikiOptions(OutputDirectory: outDir));
            assert(second.SkippedPages == result.Pages.Count && second.RebuiltPages == 0,
                $"incremental-shared: an unchanged second build must skip every page (rebuilt={second.RebuiltPages}, skipped={second.SkippedPages})");

            // ---- MaxModelingContexts truncation: drop beyond the cap ranked by importance
            // (public-api member count desc → member count desc → name asc; see
            // FractalWikiContextRankingTests for the ranking suite), count in diagnostics,
            // no misc container context (track decision #2).
            var cappedDir = Path.Combine(root, "capped");
            var capped = await pipeline.BuildAsync(om, new FractalWikiOptions(
                OutputDirectory: cappedDir, MaxModelingContexts: 2));
            var cappedSlugs = Directory.GetDirectories(Path.Combine(cappedDir, "modeling", "domain", "contexts"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(cappedSlugs.SequenceEqual(new[] { "acme-billing", "acme-shipping" }),
                "truncation: MaxModelingContexts=2 should keep the two top-ranked contexts (public-api desc, size desc, name asc; got: "
                + string.Join(", ", cappedSlugs!) + ")");
            assert(capped.Diagnostics.Any(d => d.Contains("modeling", StringComparison.Ordinal)
                    && d.Contains("3", StringComparison.Ordinal)
                    && d.Contains("MaxModelingContexts", StringComparison.Ordinal)),
                "truncation: dropping contexts beyond the cap should surface a counted diagnostic");

            // ---- objects leaf budget: MaxObjectsPerContext=2 keeps the two highest weighted-degree
            // types (Invoice deg 2, Payment deg 1; IPayable deg 0 dropped) with a counted diagnostic
            // and a truncation note on objects/index.md.
            var objCapDir = Path.Combine(root, "obj-capped");
            var objCapped = await pipeline.BuildAsync(om, new FractalWikiOptions(
                OutputDirectory: objCapDir, MaxObjectsPerContext: 2));
            var cappedObjectFiles = Directory.GetFiles(
                    Path.Combine(objCapDir, "modeling", "domain", "contexts", "acme-billing", "objects"), "*.md")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(cappedObjectFiles.SequenceEqual(new[] { "index.md", "invoice.md", "payment.md" }),
                "objects-budget: MaxObjectsPerContext=2 should keep the two highest-degree types (got: "
                + string.Join(", ", cappedObjectFiles!) + ")");
            assert(objCapped.Diagnostics.Any(d => d.Contains("MaxObjectsPerContext", StringComparison.Ordinal)
                    && d.Contains("1 object type(s)", StringComparison.Ordinal)),
                "objects-budget: the dropped type should surface as a counted diagnostic");
            var cappedObjectsIndex = await File.ReadAllTextAsync(Path.Combine(
                objCapDir, "modeling", "domain", "contexts", "acme-billing", "objects", "index.md"));
            assert(cappedObjectsIndex.Contains("其余 1 个类型超出 MaxObjectsPerContext 预算未成页", StringComparison.Ordinal),
                "objects-budget: objects/index.md should carry the truncation count note");

            // workflows leaf budget: a zero cap still keeps the category index but pages no flow.
            var wfCapDir = Path.Combine(root, "wf-capped");
            var wfCapped = await pipeline.BuildAsync(om, new FractalWikiOptions(
                OutputDirectory: wfCapDir, MaxWorkflowsPerContext: 0));
            assert(!File.Exists(Path.Combine(wfCapDir, "modeling", "domain", "contexts", "acme-billing", "workflows", "billinvoice.md")),
                "workflows-budget: flows beyond MaxWorkflowsPerContext must not be paged");
            assert(wfCapped.Diagnostics.Any(d => d.Contains("MaxWorkflowsPerContext", StringComparison.Ordinal)
                    && d.Contains("1 workflow(s)", StringComparison.Ordinal)),
                "workflows-budget: the dropped flow should surface as a counted diagnostic");
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
