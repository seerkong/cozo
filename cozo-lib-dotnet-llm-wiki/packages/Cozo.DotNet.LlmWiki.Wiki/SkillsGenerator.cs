using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Wiki;

/// <summary>
/// Options for <see cref="SkillsGenerator.GenerateAsync"/> (add-llm-wiki-skills-generation design §2-3).
/// </summary>
/// <param name="TargetDirectory">Directory the skill folders are written into (usually <c>.claude/skills</c>).</param>
/// <param name="MaxSkills">Per-context skill budget — reuses the modeling-context cap (design风险: 大仓 context 多).
/// The three generic workflow skills ride on top of this cap.</param>
/// <param name="BudgetPerSkill">Character budget per SKILL.md; oversized skills are truncated at a line
/// boundary with an explicit annotation (deterministic-bounded case).</param>
internal sealed record SkillsOptions(
    string TargetDirectory,
    int MaxSkills = 24,
    int BudgetPerSkill = 4000);

/// <summary>
/// Result of one skills generation run.
/// </summary>
/// <param name="Generated">Skill directory names written this run (context skills name-asc, then the workflow skills).</param>
/// <param name="Cleaned">Own-prefix (<c>depa-wiki-</c>) directories removed because their context no longer exists (sync semantics).</param>
/// <param name="Truncated">Skills whose rendered content exceeded <see cref="SkillsOptions.BudgetPerSkill"/> and were cut.</param>
/// <param name="Diagnostics">Non-fatal findings forwarded from context discovery (dropped contexts etc.).</param>
internal sealed record SkillsResult(
    IReadOnlyList<string> Generated,
    IReadOnlyList<string> Cleaned,
    int Truncated,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Deterministic Claude Code skills generator (add-llm-wiki-skills-generation track T1.1,
/// delta behavior://llm-wiki-tools/requirements/skills-generation): renders one
/// <c>depa-wiki-&lt;context-slug&gt;/SKILL.md</c> per modeling context (reusing the pipeline's
/// three-level fallback context discovery — #N community labels can never leak) plus the three
/// generic workflow skills (exploring/impact/depa). Zero LLM: same graph in, byte-identical
/// skills out. Own-prefix discipline lives here so every caller gets it: only
/// <c>depa-wiki-*</c> directories are ever written or removed, and own directories whose
/// context disappeared are synced away on generate; user directories are never touched.
/// The CLI subcommands (skills generate/clean/status) wire this up in P2.
/// </summary>
internal sealed class SkillsGenerator
{
    /// <summary>Ownership marker: the generator only writes and removes directories with this name prefix.</summary>
    public const string OwnPrefix = "depa-wiki-";

    private const int KeySymbolBudget = 8;
    private const int DependencyBudget = 3;
    private const int ProcessBudget = 3;

    /// <summary>Slugs of the generic workflow skills — reserved so a same-named context cannot collide.</summary>
    private static readonly string[] WorkflowSlugs = ["exploring", "impact", "depa"];

    /// <summary>The shared 26-tool matrix as exposed by LlmWikiToolRunner (listed statically: the Wiki package must not depend on Tools).</summary>
    private static readonly string[] ToolMatrix =
    [
        "index_repo", "build_wiki", "symbol_context", "impact_of_change", "docs_for_code",
        "explain_relation", "parser_status", "parse_file", "index_embeddings", "semantic_search",
        "overview_graph", "query_named", "trace", "check", "detect_changes",
        "depa_conformance", "fact_grade_map", "health_score",
        "ontology_investigation_overview", "find_business_terms", "list_use_case_slices",
        "get_use_case_slice", "find_semantic_patterns", "get_semantic_evidence", "inspect_ontology_subject",
        "run_business_ontology_agent",
    ];

    /// <summary>Runs context discovery and writes the skill set into the target directory (sync semantics).</summary>
    public async Task<SkillsResult> GenerateAsync(
        CozoOm om,
        SkillsOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<string>();
        var snapshot = await FractalWikiPipeline.CollectSnapshotAsync(om, cancellationToken);
        var groups = FractalWikiPipeline.BuildGroups(snapshot, minGroupSize: 1, diagnostics);
        var contexts = FractalWikiPipeline.BuildModelingContexts(
            snapshot, groups, options.MaxSkills,
            maxObjectsPerContext: int.MaxValue, maxWorkflowsPerContext: int.MaxValue, diagnostics);

        var memberContext = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var context in contexts)
        {
            foreach (var member in context.Members)
            {
                memberContext[member] = context.Name;
            }
        }

        var dependencies = await CollectContextDependenciesAsync(om, memberContext, cancellationToken);

        var target = Path.GetFullPath(options.TargetDirectory);
        Directory.CreateDirectory(target);

        var generated = new List<string>();
        var truncated = 0;
        async Task WriteSkillAsync(string skillName, string content)
        {
            var (bounded, wasTruncated) = ApplyBudget(content, options.BudgetPerSkill);
            if (wasTruncated)
            {
                truncated++;
            }

            var directory = Path.Combine(target, skillName);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "SKILL.md"), bounded,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            generated.Add(skillName);
        }

        foreach (var context in contexts)
        {
            var slug = WorkflowSlugs.Contains(context.Slug, StringComparer.Ordinal)
                ? context.Slug + "-context"
                : context.Slug;
            await WriteSkillAsync(OwnPrefix + slug, RenderContextSkill(snapshot, context, slug, dependencies));
        }

        await WriteSkillAsync(OwnPrefix + "exploring", RenderExploringSkill(snapshot, contexts.Count));
        await WriteSkillAsync(OwnPrefix + "impact", RenderImpactSkill(snapshot));
        await WriteSkillAsync(OwnPrefix + "depa", RenderDepaSkill(snapshot));

        // Sync semantics: own-prefix directories not regenerated this run lost their context —
        // remove them. Anything without the prefix is user-owned and never touched.
        var generatedSet = new HashSet<string>(generated, StringComparer.Ordinal);
        var cleaned = new List<string>();
        foreach (var directory in Directory.GetDirectories(target).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(OwnPrefix, StringComparison.Ordinal) && !generatedSet.Contains(name))
            {
                Directory.Delete(directory, recursive: true);
                cleaned.Add(name);
            }
        }

        return new SkillsResult(generated, cleaned, truncated, diagnostics);
    }

    // ---- context facts -----------------------------------------------------------------------

    /// <summary>One aggregated cross-context CALLS/IMPORTS dependency of a context.</summary>
    private sealed record ContextDependency(string Context, bool Outgoing, string Other, string Kind, int Count);

    /// <summary>
    /// Aggregates symbol-level CALLS/IMPORTS edges onto the final (budgeted) context set —
    /// exact counts, not the community-level approximation (contexts may merge or split
    /// communities). Deterministic order: count desc, outgoing first, other asc, kind asc.
    /// </summary>
    private static async Task<Dictionary<string, IReadOnlyList<ContextDependency>>> CollectContextDependenciesAsync(
        CozoOm om,
        IReadOnlyDictionary<string, string> memberContext,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """?[from_id, to_id, kind] := *ck_edge{ from_id, to_id, kind }, is_in(kind, ["CALLS", "IMPORTS"])""",
            cancellationToken: cancellationToken);
        var counts = new Dictionary<(string From, string To, string Kind), int>();
        foreach (var row in rows.Rows)
        {
            if (memberContext.TryGetValue(AsString(row[0]), out var from)
                && memberContext.TryGetValue(AsString(row[1]), out var to)
                && !string.Equals(from, to, StringComparison.Ordinal))
            {
                var key = (from, to, AsString(row[2]));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var byContext = new Dictionary<string, List<ContextDependency>>(StringComparer.Ordinal);
        void Add(string context, ContextDependency dependency)
        {
            if (!byContext.TryGetValue(context, out var list))
            {
                byContext[context] = list = [];
            }

            list.Add(dependency);
        }

        foreach (var ((from, to, kind), count) in counts)
        {
            Add(from, new ContextDependency(from, Outgoing: true, to, kind, count));
            Add(to, new ContextDependency(to, Outgoing: false, from, kind, count));
        }

        return byContext.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ContextDependency>)kv.Value
                .OrderByDescending(d => d.Count)
                .ThenByDescending(d => d.Outgoing)
                .ThenBy(d => d.Other, StringComparer.Ordinal)
                .ThenBy(d => d.Kind, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    /// <summary>Members ordered by weighted degree desc, name asc, id asc (key-symbols section + tool examples).</summary>
    private static IReadOnlyList<FractalWikiSymbol> RankedMembers(FractalWikiSnapshot snapshot, FractalWikiModelingContext context) =>
        context.Members
            .Select(id => snapshot.Symbols.GetValueOrDefault(id))
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!)
            .OrderByDescending(symbol => snapshot.SymbolDegrees.GetValueOrDefault(symbol.SymbolId))
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Longest common directory prefix of the members' file paths ("" when there is none), with a trailing '/'.</summary>
    private static string MainPathPrefix(FractalWikiSnapshot snapshot, FractalWikiModelingContext context)
    {
        string[]? prefix = null;
        foreach (var member in context.Members)
        {
            if (!snapshot.Symbols.TryGetValue(member, out var symbol))
            {
                continue;
            }

            var path = snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId);
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var directories = segments.Length > 1 ? segments[..^1] : [];
            if (prefix is null)
            {
                prefix = directories;
                continue;
            }

            var common = 0;
            while (common < prefix.Length && common < directories.Length
                && string.Equals(prefix[common], directories[common], StringComparison.Ordinal))
            {
                common++;
            }

            prefix = prefix[..common];
            if (prefix.Length == 0)
            {
                return "";
            }
        }

        return prefix is { Length: > 0 } ? string.Join('/', prefix) + "/" : "";
    }

    private static string Location(FractalWikiSnapshot snapshot, FractalWikiSymbol symbol) =>
        $"{snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId)}:{symbol.StartLine}";

    // ---- rendering (design §2 template) --------------------------------------------------------

    private static string RenderContextSkill(
        FractalWikiSnapshot snapshot,
        FractalWikiModelingContext context,
        string slug,
        IReadOnlyDictionary<string, IReadOnlyList<ContextDependency>> dependencies)
    {
        var ranked = RankedMembers(snapshot, context);
        var pathPrefix = MainPathPrefix(snapshot, context);
        var usage = pathPrefix.Length > 0
            ? $"Use when working on files under {pathPrefix}."
            : "Use when working on the files owned by this context.";
        var fileCount = context.Members
            .Select(id => snapshot.Symbols.GetValueOrDefault(id)?.FileId)
            .Where(fileId => fileId is not null)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var builder = new StringBuilder();
        Line(builder, "---");
        Line(builder, $"name: {OwnPrefix}{slug}");
        Line(builder, $"description: Navigate the {context.Name} area of this repo (key symbols, call structure, processes). {usage}");
        Line(builder, "---");
        Line(builder);
        Line(builder, $"# Navigate {context.Name}");
        Line(builder);
        Line(builder, "## Boundary");
        Line(builder);
        Line(builder, $"- context: {context.Name}");
        Line(builder, $"- members: {context.Members.Count} symbol(s) across {fileCount} file(s)");
        Line(builder, pathPrefix.Length > 0
            ? $"- main path prefix: `{pathPrefix}`"
            : "- main path prefix: (none — members span multiple roots)");
        Line(builder);
        Line(builder, "## Key symbols");
        Line(builder);
        if (ranked.Count == 0)
        {
            Line(builder, "- none recorded");
        }

        foreach (var symbol in ranked.Take(KeySymbolBudget))
        {
            Line(builder, $"- `{symbol.Name}` — {symbol.Kind} ({Location(snapshot, symbol)})");
        }

        Line(builder);
        Line(builder, "## Call structure");
        Line(builder);
        var contextDependencies = dependencies.GetValueOrDefault(context.Name, []);
        if (contextDependencies.Count == 0)
        {
            Line(builder, "- no cross-context dependencies detected");
        }

        foreach (var dependency in contextDependencies.Take(DependencyBudget))
        {
            Line(builder, dependency.Outgoing
                ? $"- -> {dependency.Other} ({dependency.Kind} x{dependency.Count})"
                : $"- <- {dependency.Other} ({dependency.Kind} x{dependency.Count})");
        }

        Line(builder);
        Line(builder, "## Processes");
        Line(builder);
        var flows = context.Workflows
            .OrderByDescending(p => p.StepCount)
            .ThenBy(p => p.ProcessId, StringComparer.Ordinal)
            .Take(ProcessBudget)
            .ToArray();
        if (flows.Length == 0)
        {
            Line(builder, "- none extracted");
        }

        foreach (var flow in flows)
        {
            var entry = snapshot.Symbols.GetValueOrDefault(flow.EntrySymbolId);
            var entryText = entry is null
                ? $"`{flow.EntrySymbolId}`"
                : $"`{entry.Name}` ({Location(snapshot, entry)})";
            Line(builder, $"- {flow.Name} ({flow.ProcessType}, {flow.StepCount} steps) — entry {entryText}");
        }

        Line(builder);
        Line(builder, "## Explore with depa-wiki");
        Line(builder);
        var top = ranked.Count > 0 ? ranked[0] : null;
        var second = ranked.Count > 1 ? ranked[1] : top;
        Line(builder, "- `semantic_search` — find code and docs in this area:");
        Line(builder, $"  `{{ \"query\": \"{context.Name}\", \"limit\": 5 }}`");
        if (top is not null)
        {
            Line(builder, "- `symbol_context` — neighbors, docs and flows of the top symbol:");
            Line(builder, $"  `{{ \"symbolId\": \"{top.SymbolId}\" }}`");
            Line(builder, "- `trace` — follow a call path inside this context:");
            Line(builder, $"  `{{ \"from\": \"{top.Name}\", \"to\": \"{second!.Name}\" }}`");
        }

        return builder.ToString();
    }

    private static string RenderExploringSkill(FractalWikiSnapshot snapshot, int contextCount)
    {
        var builder = new StringBuilder();
        Line(builder, "---");
        Line(builder, "name: depa-wiki-exploring");
        Line(builder, "description: Explore this repository through the depa-wiki code knowledge graph (search, symbol context, overview, docs). Use when orienting in unfamiliar code or locating where something lives.");
        Line(builder, "---");
        Line(builder);
        Line(builder, "# Exploring with depa-wiki");
        Line(builder);
        Line(builder, $"This repository is indexed as a code knowledge graph: {snapshot.SymbolCount} symbols across "
            + $"{snapshot.FileCount} files, {contextCount} context(s), {snapshot.Processes.Count} execution flow(s).");
        Line(builder);
        Line(builder, "## Workflow");
        Line(builder);
        Line(builder, "1. `semantic_search` — hybrid text+vector search: `{ \"query\": \"...\", \"limit\": 5 }`.");
        Line(builder, "2. `symbol_context` — neighbors, docs, flows and community of one symbol: `{ \"symbolId\": \"...\" }`.");
        Line(builder, "3. `overview_graph` — bounded architecture graph: `{ \"maxNodes\": 40, \"maxEdges\": 80 }`.");
        Line(builder, "4. `docs_for_code` / `explain_relation` — documentation and edge evidence for what you found.");
        Line(builder, "5. `query_named` — canned graph queries; `parser_status` / `parse_file` for parser-level detail.");
        Line(builder);
        AppendToolMatrix(builder);
        return builder.ToString();
    }

    private static string RenderImpactSkill(FractalWikiSnapshot snapshot)
    {
        var builder = new StringBuilder();
        Line(builder, "---");
        Line(builder, "name: depa-wiki-impact");
        Line(builder, "description: Assess the blast radius of a change with the depa-wiki graph tools (layered impact, call-path tracing, cycle checks, staleness). Use before refactoring or reviewing a risky change.");
        Line(builder, "---");
        Line(builder);
        Line(builder, "# Impact analysis with depa-wiki");
        Line(builder);
        Line(builder, $"The indexed graph covers {snapshot.SymbolCount} symbols across {snapshot.FileCount} files.");
        Line(builder);
        Line(builder, "## Workflow");
        Line(builder);
        Line(builder, "1. `impact_of_change` — layered caller/callee impact with risk rating: "
            + "`{ \"symbolId\": \"...\", \"direction\": \"up\", \"maxDepth\": 3 }`.");
        Line(builder, "2. `trace` — shortest call path between two symbols: `{ \"from\": \"...\", \"to\": \"...\" }`.");
        Line(builder, "3. `check` — import/call cycle detection: `{ \"cycles\": \"both\" }`.");
        Line(builder, "4. `detect_changes` — staleness between the index and the working tree before trusting results.");
        Line(builder);
        AppendToolMatrix(builder);
        return builder.ToString();
    }

    private static string RenderDepaSkill(FractalWikiSnapshot snapshot)
    {
        var builder = new StringBuilder();
        Line(builder, "---");
        Line(builder, "name: depa-wiki-depa");
        Line(builder, "description: Check DEPA architecture conformance with the depa-wiki tools (external-call whitelist, fact grading, health score). Use when reviewing dependency discipline or architecture drift.");
        Line(builder, "---");
        Line(builder);
        Line(builder, "# DEPA conformance with depa-wiki");
        Line(builder);
        Line(builder, $"The indexed graph covers {snapshot.SymbolCount} symbols across {snapshot.FileCount} files.");
        Line(builder);
        Line(builder, "## Workflow");
        Line(builder);
        Line(builder, "1. `depa_conformance` — external-call whitelist violations and BLOCKED annotations.");
        Line(builder, "2. `fact_grade_map` — fact grading of the knowledge graph layers.");
        Line(builder, "3. `health_score` — the aggregated architecture health score and its inputs.");
        Line(builder, "4. Cross-check hotspots with `symbol_context` and `impact_of_change` before acting.");
        Line(builder);
        AppendToolMatrix(builder);
        return builder.ToString();
    }

    private static void AppendToolMatrix(StringBuilder builder)
    {
        Line(builder, "## Tool matrix (26 tools)");
        Line(builder);
        Line(builder, string.Join(", ", ToolMatrix.Select(tool => $"`{tool}`")));
    }

    // ---- budget --------------------------------------------------------------------------------

    /// <summary>Cuts oversized content at a line boundary and appends an explicit truncation annotation (result never exceeds the budget).</summary>
    private static (string Content, bool Truncated) ApplyBudget(string content, int budget)
    {
        if (content.Length <= budget)
        {
            return (content, false);
        }

        var marker = $"\n> truncated: skill budget {budget} chars\n";
        var allowed = Math.Max(0, budget - marker.Length);
        var cut = content.LastIndexOf('\n', Math.Min(allowed, content.Length - 1));
        var head = cut > 0 ? content[..cut] : content[..allowed];
        return (head + marker, true);
    }

    /// <summary>Deterministic newline discipline: skills use '\n' regardless of platform.</summary>
    private static void Line(StringBuilder builder, string text = "") => builder.Append(text).Append('\n');

    private static string AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
}
