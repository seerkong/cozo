using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Wiki;

/// <summary>
/// Options for <see cref="FractalWikiPipeline.BuildAsync"/>.
/// </summary>
/// <param name="OutputDirectory">Explicit output root. Default: <c>{WorkDirectory}/.depa-wiki/docs-preview</c> (design §4 preview area).</param>
/// <param name="WorkDirectory">Work directory the default preview root is derived from. Default: current directory.</param>
/// <param name="Llm">LLM backend. Null or <see cref="ILlmClient.IsAvailable"/>=false degrades to the pure structure layer (normal path, not an error).</param>
/// <param name="MinGroupSize">Communities with fewer symbol members are not promoted to groups (counted in diagnostics).</param>
/// <param name="Force">Skip the page-level incremental cache and rebuild every page (T2.2, design §3).</param>
/// <param name="RequireApprovedGrouping">When true, a build without an approved
/// <c>.grouping-review.json</c> at the output root emits the structure layer only (no LLM calls)
/// plus a diagnostic — the human-approval gate of design §2 Phase 1.3. Default false: CLI/CI
/// builds are never blocked; the review draft is written either way.</param>
/// <param name="MaxModelingContexts">Modeling context budget (add-llm-wiki-modeling-fractal
/// design §1): contexts beyond the cap (member count desc, name asc) are dropped with a counted
/// diagnostic — never folded into a misc container context (track decision #2).</param>
/// <param name="MaxObjectsPerContext">objects leaf budget per context (modeling-fractal design §2):
/// public types beyond the cap (weighted degree desc, name asc) get no leaf page, with a counted
/// diagnostic and a truncation note on objects/index.md.</param>
/// <param name="MaxWorkflowsPerContext">workflows leaf budget per context (modeling-fractal
/// design §2): flows beyond the cap (step count desc, process id asc) get no leaf page, with a
/// counted diagnostic and a truncation note on workflows/index.md.</param>
public sealed record FractalWikiOptions(
    string? OutputDirectory = null,
    string? WorkDirectory = null,
    ILlmClient? Llm = null,
    int MinGroupSize = 1,
    bool Force = false,
    bool RequireApprovedGrouping = false,
    int MaxModelingContexts = 24,
    int MaxObjectsPerContext = 30,
    int MaxWorkflowsPerContext = 10);

/// <summary>
/// Result of one fractal wiki build.
/// </summary>
/// <param name="Pages">Absolute paths of every page of the wiki after this build (rebuilt or skipped in place), in canonical order.</param>
/// <param name="GroupCount">Final group count after the (optional) LLM review actions.</param>
/// <param name="LlmUsed">Whether at least one LLM call succeeded during the build.</param>
/// <param name="Diagnostics">Non-fatal findings: ignored review actions, flag reasons, degraded narrative slots, dropped small communities.</param>
/// <param name="SkippedPages">Pages whose deterministic input hashes matched the .meta cache (no write, no LLM call).</param>
/// <param name="RebuiltPages">Pages regenerated and written this build.</param>
public sealed record FractalWikiResult(
    IReadOnlyList<string> Pages,
    int GroupCount,
    bool LlmUsed,
    IReadOnlyList<string> Diagnostics,
    int SkippedPages = 0,
    int RebuiltPages = 0);

/// <summary>
/// Four-phase fractal wiki pipeline (add-llm-wiki-llm-pipeline track T2.1, design §2, extended by
/// add-llm-wiki-engineering-fractal track T1.1): Phase 0 deterministic graph collection → Phase 1
/// community-led grouping with a bounded LLM review (merge/rename/flag only) → Phase 2 engineering
/// fractal pages as deterministic skeletons with explicitly marked LLM narrative sections →
/// Phase 3 navigation indexes + migration ledger. The emitted tree follows
/// std/skill/docs-engineering-fractal: root index.md + migration-map.md + impl/global/ with the
/// default six categories, 目录职责 manifest blocks per folder-manifest.md and controlled
/// frontmatter per model-driven-docs.md. The structure layer is fully reproducible: without an
/// LLM the same input yields identical pages (modulo the last_verified date), and with an LLM
/// only the content inside <c>&lt;!-- llm:begin … --&gt;…&lt;!-- llm:end --&gt;</c> differs.
/// The legacy template <see cref="WikiCompiler"/> stays untouched.
/// </summary>
public sealed class FractalWikiPipeline
{
    private const string PendingNarrative = "_pending: llm unavailable_";

    /// <summary>howto topic budget (design §1): at most this many working-with-&lt;group&gt; pages.</summary>
    private const int HowtoTopicBudget = 10;

    private const string ReviewSystemPrompt =
        "You review a deterministic module grouping of a code repository. The grouping was " +
        "produced by graph community detection and is authoritative; you only suggest bounded " +
        "adjustments. Respond with a JSON array of action objects and nothing else. Allowed " +
        "actions: {\"action\":\"merge\",\"a\":\"<groupId>\",\"b\":\"<groupId>\"} folds group b into a; " +
        "{\"action\":\"rename\",\"id\":\"<groupId>\",\"name\":\"<new name>\"} improves a group name; " +
        "{\"action\":\"flag\",\"id\":\"<groupId>\",\"reason\":\"<why>\"} marks a group for human review. " +
        "Never move individual symbols or files. An empty array [] means no changes.";

    private const string NarrativeSystemPrompt =
        "You write the narrative section of a code wiki page. The structured facts " +
        "(tables, graphs, step lists) are already rendered on the page — do not repeat them " +
        "as tables or lists. Write 2-4 plain sentences answering the page's question. " +
        "No meta-commentary, no invented APIs, no headings, no code fences.";

    /// <summary>Runs the four phases and writes the fractal pages. Never throws for LLM trouble: degraded slots stay pending with a diagnostic.</summary>
    public async Task<FractalWikiResult> BuildAsync(
        CozoOm om,
        FractalWikiOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        options ??= new FractalWikiOptions();
        var diagnostics = new List<string>();
        var llm = options.Llm is { IsAvailable: true } available ? available : null;
        if (options.Llm is { IsAvailable: false, UnavailableReason: { Length: > 0 } reason })
        {
            diagnostics.Add($"llm: unavailable ({reason}); structure layer only");
        }

        var llmUsed = false;

        // Phase 0: deterministic graph collection (pure queries, no LLM).
        var snapshot = await CollectSnapshotAsync(om, cancellationToken);

        var workDirectory = options.WorkDirectory ?? Directory.GetCurrentDirectory();
        var outputRoot = Path.GetFullPath(options.OutputDirectory
            ?? Path.Combine(workDirectory, ".depa-wiki", "docs-preview"));

        // Phase 1: community-led grouping.
        var groups = BuildGroups(snapshot, options.MinGroupSize, diagnostics);

        // Review gate (design §2 Phase 1.3): an approved .grouping-review.json is authoritative —
        // its groups replace the deterministic grouping and the LLM review is skipped. Applied
        // BEFORE the hash computation because the approved file is itself a deterministic input:
        // editing it invalidates the group pages. RequireApprovedGrouping=true without an
        // approved file degrades the whole build to the structure layer (no LLM calls).
        var review = FractalWikiGroupingReview.TryLoad(outputRoot, diagnostics);
        var approvedGrouping = review is { Approved: true };
        if (approvedGrouping)
        {
            groups = BuildApprovedGroups(review!);
            diagnostics.Add($"phase1: approved {FractalWikiGroupingReview.FileName} in effect ({groups.Count} group(s)); llm grouping review skipped");
        }
        else if (options.RequireApprovedGrouping)
        {
            diagnostics.Add($"phase1: RequireApprovedGrouping is set but no approved {FractalWikiGroupingReview.FileName} exists at the output root; structure layer only");
            llm = null;
        }

        // Deterministic page plan (T1.1, design §1): the inventory (which pages exist) and the
        // invalidation hashes are computed from this pre-LLM-review deterministic state (ED-1):
        // the LLM review is bounded post-processing, so it must not participate in cache keys —
        // otherwise a skipped build could never be LLM-free. howto topics (top-N groups) and
        // their skeleton facts are snapshotted here so a later merge/rename cannot move pages.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var howtoTopics = BuildHowtoTopics(snapshot, groups);
        // Modeling contexts (add-llm-wiki-modeling-fractal T1.1, design §1): discovered from the
        // same pre-LLM-review deterministic state as the howto topics — an approved
        // .grouping-review.json flows in (it replaced `groups` above), the bounded LLM review
        // does not (post-hash processing, ED-1).
        var modelingContexts = BuildModelingContexts(
            snapshot, groups, options.MaxModelingContexts,
            options.MaxObjectsPerContext, options.MaxWorkflowsPerContext, diagnostics);
        var memberContext = BuildMemberContextMap(modelingContexts);
        var inventory = BuildInventory(howtoTopics, modelingContexts);
        var pageInputs = ComputePageInputs(snapshot, groups, AggregateGroupEdges(snapshot, groups), howtoTopics, modelingContexts, memberContext, inventory);

        // Incremental decision: Force (or a missing/corrupt meta) rebuilds everything;
        // otherwise a page is skipped when both hashes match and the file still exists —
        // unless its narrative is still pending and an LLM is available this build, in which
        // case the page rebuilds to backfill the narrative (design §1.5/§3.2 step 1).
        // priorMeta is kept separately: the migration ledger diffs against it even under Force.
        var priorMeta = FractalWikiMeta.TryLoad(outputRoot);
        var meta = options.Force ? null : priorMeta;
        var rebuild = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relative in inventory)
        {
            var cached = meta?.Pages.GetValueOrDefault(relative);
            var upToDate = cached is not null
                && string.Equals(cached.StructureHash, pageInputs[relative].StructureHash, StringComparison.Ordinal)
                && string.Equals(cached.NarrativeInputsHash, pageInputs[relative].NarrativeInputsHash, StringComparison.Ordinal)
                && File.Exists(PagePath(outputRoot, relative));
            if (upToDate && cached!.NarrativePending && llm is not null)
            {
                diagnostics.Add($"incremental: {relative} narrative pending and llm available, rebuilding to backfill the narrative");
                upToDate = false;
            }

            if (!upToDate)
            {
                rebuild.Add(relative);
            }
        }

        if (rebuild.Count < inventory.Count)
        {
            diagnostics.Add($"incremental: {inventory.Count - rebuild.Count} unchanged page(s) skipped, {rebuild.Count} rebuilt");
        }

        // Bounded LLM review (merge/rename/flag): only when a group-dependent page rebuilds
        // and no approved review file governs the grouping.
        var groupPagesRebuild = rebuild.Contains(ArchitecturePage)
            || rebuild.Contains(CodeMapPage)
            || rebuild.Contains(ApiSurfacePage);
        IReadOnlyList<JsonObject> reviewSuggestions = [];
        if (!approvedGrouping && llm is not null && groups.Count > 0 && groupPagesRebuild)
        {
            var (reviewSucceeded, suggestions) = await ReviewGroupsAsync(llm, snapshot, groups, diagnostics, cancellationToken);
            llmUsed |= reviewSucceeded;
            reviewSuggestions = suggestions;
        }

        // Review draft: rewritten from the final grouping whenever Phase 1 actually reran
        // (or the file is missing), never when an approved file is in effect.
        if (!approvedGrouping && (groupPagesRebuild || !File.Exists(Path.Combine(outputRoot, FractalWikiGroupingReview.FileName))))
        {
            await FractalWikiGroupingReview.SaveDraftAsync(outputRoot, groups, reviewSuggestions, cancellationToken);
        }

        // Group-level edge aggregation for the skeletons (post-review names for rendering).
        var groupEdges = AggregateGroupEdges(snapshot, groups);

        // Phase 2: per-page narratives — one bounded call per rebuilding page with a narrative
        // slot; failures leave the slot pending with a diagnostic (never fatal).
        var narratives = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in inventory)
        {
            var prompt = pageInputs[relative].NarrativePrompt;
            if (prompt.Length == 0)
            {
                continue;
            }

            narratives[relative] = PendingNarrative;
            if (llm is null || !rebuild.Contains(relative))
            {
                continue;
            }

            try
            {
                var completion = await llm.CompleteAsync(
                    NarrativeSystemPrompt, prompt, new LlmOptions(Temperature: 0), cancellationToken);
                var text = SanitizeNarrative(completion.Text, diagnostics);
                if (text.Length > 0)
                {
                    narratives[relative] = text;
                    llmUsed = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add($"phase2: {relative} narrative llm call failed, slot stays pending: {ex.Message}");
            }
        }

        // Migration ledger (append-style): previous ledger lines are carried over from the
        // on-disk file; a new dated entry is appended only when the inventory changed against
        // the prior meta (or on the very first build of this output root).
        var previousLedger = LoadMigrationLedger(PagePath(outputRoot, MigrationMapPage));
        var ledgerEntry = BuildLedgerEntry(today, priorMeta, inventory);

        // Phase 3: render + write. Renderers run lazily: skipped pages are neither rendered
        // nor written. Skeleton renderers use the post-review groups (rename/merge visible),
        // matching the pre-review hashes by design (bounded post-processing, ED-1).
        var renderers = BuildRenderers(
            snapshot, groups, groupEdges, howtoTopics, modelingContexts, memberContext, narratives, previousLedger, ledgerEntry, today);

        var pages = new List<string>(inventory.Count);
        foreach (var relative in inventory)
        {
            var path = PagePath(outputRoot, relative);
            pages.Add(path);
            if (!rebuild.Contains(relative))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, renderers[relative](), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        }

        // Refresh the meta: rebuilt pages get a new generatedAt, skipped pages keep theirs;
        // fromCommit records the work-directory HEAD (null outside a git repo). Pages that
        // fell out of the inventory (path migrations) simply drop out of the meta — their
        // cache entries invalidate naturally because the meta is keyed by relative path.
        var generatedAt = DateTimeOffset.UtcNow.ToString("O");
        var newMeta = new FractalWikiMeta { FromCommit = TryGetHeadCommit(workDirectory) };
        foreach (var relative in inventory)
        {
            // narrativePending (design §1.5): rebuilt pages with a narrative slot record whether
            // the slot is still the pending placeholder; skipped pages keep their cached state.
            var hasNarrativeSlot = pageInputs[relative].NarrativeInputsHash.Length > 0;
            newMeta.Pages[relative] = new FractalWikiPageMeta
            {
                StructureHash = pageInputs[relative].StructureHash,
                NarrativeInputsHash = pageInputs[relative].NarrativeInputsHash,
                GeneratedAt = rebuild.Contains(relative)
                    ? generatedAt
                    : meta?.Pages.GetValueOrDefault(relative)?.GeneratedAt ?? generatedAt,
                NarrativePending = rebuild.Contains(relative)
                    ? hasNarrativeSlot && string.Equals(narratives.GetValueOrDefault(relative), PendingNarrative, StringComparison.Ordinal)
                    : meta?.Pages.GetValueOrDefault(relative)?.NarrativePending ?? false,
            };
        }

        await newMeta.SaveAsync(outputRoot, cancellationToken);
        return new FractalWikiResult(
            pages,
            groups.Count,
            llmUsed,
            diagnostics,
            SkippedPages: inventory.Count - rebuild.Count,
            RebuiltPages: rebuild.Count);
    }

    // ---- page inventory (T1.1, design §1) --------------------------------------------------

    private const string RootIndexPage = "index.md";
    private const string MigrationMapPage = "migration-map.md";
    private const string ImplIndexPage = "impl/index.md";
    private const string PlaneIndexPage = "impl/global/index.md";
    private const string OverviewIndexPage = "impl/global/overview/index.md";
    private const string ArchitecturePage = "impl/global/overview/architecture.md";
    private const string HowtoIndexPage = "impl/global/howto/index.md";
    private const string RulesIndexPage = "impl/global/rules/index.md";
    private const string BoundariesPage = "impl/global/rules/boundaries.md";
    private const string ReferenceIndexPage = "impl/global/reference/index.md";
    private const string CodeMapPage = "impl/global/reference/code-map.md";
    private const string ApiSurfacePage = "impl/global/reference/api-surface.md";
    private const string TroubleshootingIndexPage = "impl/global/troubleshooting/index.md";
    private const string DiagnosticsPage = "impl/global/troubleshooting/diagnostics.md";
    private const string ModelingIndexPage = "modeling/index.md";
    private const string ModelingDomainIndexPage = "modeling/domain/index.md";
    private const string ModelingGlossaryPage = "modeling/domain/glossary.md";
    private const string ModelingContextsIndexPage = "modeling/domain/contexts/index.md";

    private static string ContextPage(FractalWikiModelingContext context, string leaf) =>
        $"modeling/domain/contexts/{context.Slug}/{leaf}";

    /// <summary>Path migrations of the generator's own page set (G2 → engineering fractal), rendered as migration-map rows.</summary>
    private static readonly (string Old, string New, string Status, string Notes)[] MigrationRows =
    [
        ("impl/global/overview/index.md", "impl/global/overview/architecture.md", "migrated", "G2 overview content moved to a topic leaf; the overview index is navigation-only"),
        ("impl/global/migration-map.md", "migration-map.md", "migrated", "migration ledger promoted to the docs root (E-A1)"),
    ];

    private static string HowtoTopicPage(FractalWikiHowtoTopic topic) =>
        $"impl/global/howto/working-with-{topic.Slug}.md";

    /// <summary>
    /// Canonical page inventory: default-six subset only, 缺哪类就不建 (E-C1) — howto is omitted
    /// when it has no topics and examples is not emitted at all until real worked examples exist
    /// (an empty placeholder category would fail E-C1; track decisions #2 supersedes the
    /// placeholder page of decisions #1). The modeling skeleton (T1.1 of the modeling-fractal
    /// track) rides the same inventory: modeling/domain/contexts hierarchy plus per-context
    /// index/code-map and the objects/workflows category indexes — a category directory is only
    /// created when it has content (empty-category rule); policies is never built (no evidence
    /// source, track decision #1). objects/workflows leaf pages are P2.
    /// </summary>
    private static List<string> BuildInventory(
        IReadOnlyList<FractalWikiHowtoTopic> howtoTopics,
        IReadOnlyList<FractalWikiModelingContext> modelingContexts)
    {
        var inventory = new List<string>
        {
            RootIndexPage, MigrationMapPage, ImplIndexPage, PlaneIndexPage,
            OverviewIndexPage, ArchitecturePage,
        };
        if (howtoTopics.Count > 0)
        {
            inventory.Add(HowtoIndexPage);
            inventory.AddRange(howtoTopics.Select(HowtoTopicPage));
        }

        inventory.AddRange([RulesIndexPage, BoundariesPage,
            ReferenceIndexPage, CodeMapPage, ApiSurfacePage, TroubleshootingIndexPage, DiagnosticsPage]);

        inventory.AddRange([ModelingIndexPage, ModelingDomainIndexPage, ModelingGlossaryPage, ModelingContextsIndexPage]);
        foreach (var context in modelingContexts)
        {
            inventory.Add(ContextPage(context, "index.md"));
            inventory.Add(ContextPage(context, "code-map.md"));
            if (context.ObjectTypeIds.Count > 0)
            {
                inventory.Add(ContextPage(context, "objects/index.md"));
                inventory.AddRange(context.ObjectPages.Select(p => ContextPage(context, $"objects/{p.Slug}.md")));
            }

            if (context.Workflows.Count > 0)
            {
                inventory.Add(ContextPage(context, "workflows/index.md"));
                inventory.AddRange(context.WorkflowPages.Select(p => ContextPage(context, $"workflows/{p.Slug}.md")));
            }
        }

        return inventory;
    }

    // ---- modeling context discovery (modeling-fractal T1.1, design §1) ----------------------

    /// <summary>Symbol kinds treated as types for the objects category and the level-3 name anchor.</summary>
    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "enum", "record", "type", "trait",
    };

    /// <summary>
    /// Deterministic context discovery with the three-level naming fallback (design §1), all
    /// inputs pre-LLM-review: (1) the longest common dot-separated namespace prefix of the
    /// community members' qualified names (sym_key minus lang/arity); (2) a prefix-less
    /// (cross-root) community is split by top-level namespace segment and merged into the
    /// matching contexts; (3) members without any namespace fall back to the name of their
    /// highest-weighted-degree type. Same-name contexts across communities merge; names never
    /// carry the #N degenerate community-label form because labels are never consulted.
    /// Contexts beyond <paramref name="maxContexts"/> are dropped with a counted diagnostic,
    /// ranked by importance: public-API member count desc → total member count desc → name asc
    /// (fix-wiki-fractal-entry-and-context-ranking decision #2) — no misc container context.
    /// </summary>
    internal static List<FractalWikiModelingContext> BuildModelingContexts(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups,
        int maxContexts,
        int maxObjectsPerContext,
        int maxWorkflowsPerContext,
        List<string> diagnostics)
    {
        var membersByName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string name, IEnumerable<string> members)
        {
            if (!membersByName.TryGetValue(name, out var set))
            {
                membersByName[name] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            foreach (var member in members)
            {
                set.Add(member);
            }
        }

        foreach (var group in groups.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            var members = group.Members.Order(StringComparer.Ordinal).Distinct().ToArray();
            if (members.Length == 0)
            {
                continue;
            }

            // Level 1: longest common dot-separated namespace prefix of the qualified names.
            var prefix = CommonNamespacePrefix(members.Select(m => NamespaceSegments(snapshot, m)));
            if (prefix.Length > 0)
            {
                Add(prefix, members);
                continue;
            }

            // Level 2: cross-root community — split by top-level namespace segment.
            var leftovers = new List<string>();
            foreach (var member in members)
            {
                var segments = NamespaceSegments(snapshot, member);
                if (segments.Length > 0)
                {
                    Add(segments[0], [member]);
                }
                else
                {
                    leftovers.Add(member);
                }
            }

            // Level 3: namespace-less members anchor on the highest-weighted-degree type name.
            if (leftovers.Count > 0)
            {
                Add(AnchorTypeName(snapshot, leftovers), leftovers);
            }
        }

        // Budget: drop beyond MaxModelingContexts with a count, ranked by importance
        // (fix-wiki-fractal-entry-and-context-ranking decision #2): public-API member count
        // desc → total member count desc → name asc. Public-API density stands in for outward
        // importance, so all-internal demo micro-communities sink below core namespaces.
        var publicIds = new HashSet<string>(snapshot.PublicApi.Select(s => s.SymbolId), StringComparer.Ordinal);
        var ordered = membersByName
            .OrderByDescending(kv => kv.Value.Count(publicIds.Contains))
            .ThenByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count > maxContexts)
        {
            var dropped = ordered.Skip(maxContexts).Select(kv => kv.Key).ToArray();
            diagnostics.Add($"modeling: {dropped.Length} context(s) beyond MaxModelingContexts={maxContexts} dropped "
                + "(ranked by public-api member count desc, member count desc, name asc; no misc container): "
                + string.Join(", ", dropped));
            ordered.RemoveRange(maxContexts, ordered.Count - maxContexts);
        }

        var usedSlugs = new HashSet<string>(StringComparer.Ordinal);
        var contexts = new List<FractalWikiModelingContext>(ordered.Count);
        foreach (var (name, memberSet) in ordered.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var slug = FractalDocFormat.Slug(name);
            if (!usedSlugs.Add(slug))
            {
                var suffix = 2;
                while (!usedSlugs.Add($"{slug}-{suffix}"))
                {
                    suffix++;
                }

                slug = $"{slug}-{suffix}";
            }

            var members = memberSet.ToArray();
            var objectTypes = members
                .Where(m => publicIds.Contains(m)
                    && snapshot.Symbols.TryGetValue(m, out var symbol)
                    && TypeKinds.Contains(symbol.Kind))
                .ToArray();
            var memberLookup = new HashSet<string>(members, StringComparer.Ordinal);
            var workflows = snapshot.Processes
                .Where(p => memberLookup.Contains(p.EntrySymbolId))
                .OrderBy(p => p.ProcessId, StringComparer.Ordinal)
                .ToArray();

            // objects leaf budget (design §2): weighted degree desc, name asc, id asc; beyond the
            // cap no leaf page is emitted — counted diagnostic + truncation note on the index.
            var pagedTypes = objectTypes
                .OrderByDescending(id => snapshot.SymbolDegrees.GetValueOrDefault(id))
                .ThenBy(id => snapshot.Symbols.TryGetValue(id, out var s) ? s.Name : id, StringComparer.Ordinal)
                .ThenBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var objectOverflow = Math.Max(0, pagedTypes.Length - maxObjectsPerContext);
            if (objectOverflow > 0)
            {
                pagedTypes = pagedTypes[..maxObjectsPerContext];
                diagnostics.Add($"modeling: context {name}: {objectOverflow} object type(s) beyond "
                    + $"MaxObjectsPerContext={maxObjectsPerContext} not paged");
            }

            var leafSlugs = new HashSet<string>(StringComparer.Ordinal) { "index" };
            var objectPages = pagedTypes
                .Select(id => new FractalWikiObjectPage(
                    id, UniqueSlug(leafSlugs, snapshot.Symbols.TryGetValue(id, out var s) ? s.Name : id)))
                .ToArray();

            // workflows leaf budget (design §2): step count desc, process id asc.
            var pagedFlows = workflows
                .OrderByDescending(p => p.StepCount)
                .ThenBy(p => p.ProcessId, StringComparer.Ordinal)
                .ToArray();
            var workflowOverflow = Math.Max(0, pagedFlows.Length - maxWorkflowsPerContext);
            if (workflowOverflow > 0)
            {
                pagedFlows = pagedFlows[..maxWorkflowsPerContext];
                diagnostics.Add($"modeling: context {name}: {workflowOverflow} workflow(s) beyond "
                    + $"MaxWorkflowsPerContext={maxWorkflowsPerContext} not paged");
            }

            var flowSlugs = new HashSet<string>(StringComparer.Ordinal) { "index" };
            var workflowPages = pagedFlows
                .Select(p => new FractalWikiWorkflowPage(p, UniqueSlug(flowSlugs, p.Name)))
                .ToArray();

            contexts.Add(new FractalWikiModelingContext(
                name, slug, members, objectTypes, workflows,
                objectPages, workflowPages, objectOverflow, workflowOverflow));
        }

        return contexts;
    }

    /// <summary>Slug de-duplicated against <paramref name="used"/> with a numeric suffix ('index' is pre-reserved).</summary>
    private static string UniqueSlug(HashSet<string> used, string name)
    {
        var slug = FractalDocFormat.Slug(name);
        if (used.Add(slug))
        {
            return slug;
        }

        var suffix = 2;
        while (!used.Add($"{slug}-{suffix}"))
        {
            suffix++;
        }

        return $"{slug}-{suffix}";
    }

    /// <summary>symbolId → context name over the final (budgeted) context set, for cross-context step annotation.</summary>
    private static Dictionary<string, string> BuildMemberContextMap(IReadOnlyList<FractalWikiModelingContext> contexts)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var context in contexts)
        {
            foreach (var member in context.Members)
            {
                map[member] = context.Name;
            }
        }

        return map;
    }

    /// <summary>
    /// Namespace segments of a member's qualified name: sym_key minus the lang prefix and the
    /// #arity suffix, split on '.', minus the trailing symbol-name segment. Symbols without a
    /// sym_key fall back to their bare name (no namespace).
    /// </summary>
    private static string[] NamespaceSegments(FractalWikiSnapshot snapshot, string symbolId)
    {
        if (!snapshot.Symbols.TryGetValue(symbolId, out var symbol))
        {
            return [];
        }

        var qualified = symbol.SymKey;
        var colon = qualified.IndexOf(':');
        if (colon >= 0)
        {
            qualified = qualified[(colon + 1)..];
        }

        var arity = qualified.LastIndexOf('#');
        if (arity >= 0)
        {
            qualified = qualified[..arity];
        }

        var segments = qualified.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? segments[..^1] : [];
    }

    /// <summary>Longest common prefix of dot-separated namespace segment lists, re-joined with '.'.</summary>
    private static string CommonNamespacePrefix(IEnumerable<string[]> namespaceSegments)
    {
        string[]? prefix = null;
        foreach (var segments in namespaceSegments)
        {
            if (prefix is null)
            {
                prefix = segments;
                continue;
            }

            var common = 0;
            while (common < prefix.Length && common < segments.Length
                && string.Equals(prefix[common], segments[common], StringComparison.Ordinal))
            {
                common++;
            }

            prefix = prefix[..common];
            if (prefix.Length == 0)
            {
                return "";
            }
        }

        return prefix is null ? "" : string.Join('.', prefix);
    }

    /// <summary>
    /// Level-3 name anchor: the highest-weighted-degree member (type kinds preferred, then
    /// CALLS/IMPORTS degree desc, then symbol id asc) names the context — never a community
    /// label, so the #N degenerate form cannot appear.
    /// </summary>
    private static string AnchorTypeName(FractalWikiSnapshot snapshot, IReadOnlyList<string> members)
    {
        var anchor = members
            .OrderByDescending(m => snapshot.Symbols.TryGetValue(m, out var s) && TypeKinds.Contains(s.Kind))
            .ThenByDescending(m => snapshot.SymbolDegrees.GetValueOrDefault(m))
            .ThenBy(m => m, StringComparer.Ordinal)
            .First();
        return snapshot.Symbols.TryGetValue(anchor, out var symbol) ? symbol.Name : anchor;
    }

    /// <summary>
    /// Selects the top-N groups (member count desc, id asc) as howto topics and snapshots
    /// their deterministic facts (name, members, entry processes) so post-hash review actions
    /// cannot move or reshape the pages. Slugs are de-duplicated with a numeric suffix.
    /// </summary>
    private static List<FractalWikiHowtoTopic> BuildHowtoTopics(FractalWikiSnapshot snapshot, IReadOnlyList<FractalWikiGroup> groups)
    {
        var topics = new List<FractalWikiHowtoTopic>();
        var usedSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups
            .OrderByDescending(g => g.Members.Count)
            .ThenBy(g => g.Id, StringComparer.Ordinal)
            .Take(HowtoTopicBudget))
        {
            var slug = FractalDocFormat.Slug(group.Name);
            if (!usedSlugs.Add(slug))
            {
                var suffix = 2;
                while (!usedSlugs.Add($"{slug}-{suffix}"))
                {
                    suffix++;
                }

                slug = $"{slug}-{suffix}";
            }

            var members = group.Members.Order(StringComparer.Ordinal).ToArray();
            var memberSet = new HashSet<string>(members, StringComparer.Ordinal);
            var processes = snapshot.Processes
                .Where(p => memberSet.Contains(p.EntrySymbolId))
                .OrderBy(p => p.ProcessId, StringComparer.Ordinal)
                .ToArray();
            topics.Add(new FractalWikiHowtoTopic(group.Id, group.Name, slug, members, processes));
        }

        return topics;
    }

    // ---- incremental hashes (T2.2 design §3, extended for the fractal page set) ------------

    private static string PagePath(string outputRoot, string relative) =>
        Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>HEAD commit of the work directory via the P1 Git capsule (Core internals; null outside git).</summary>
    private static string? TryGetHeadCommit(string workDirectory)
    {
        try
        {
            return Cozo.DotNet.LlmWiki.Core.GitCliDiffProvider.Default.TryGetHeadCommit(workDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the invalidation hashes of every inventory page from pre-review deterministic
    /// inputs. structureHash serializes exactly the facts the page's skeleton renders (JSON,
    /// ordinal ordering); narrativeInputsHash is the hash of the page's narrative prompt (""
    /// for pages without a narrative slot). Symbol locations only feed the code-map and
    /// api-surface hashes, so a moved-line reindex rebuilds those two pages and nothing else.
    /// The last_verified date and the migration ledger tail are deliberately NOT hashed: a
    /// skipped page keeps its previous date, and the ledger only grows when the inventory
    /// itself changes (which is hashed).
    /// </summary>
    private static IReadOnlyDictionary<string, FractalWikiPageInputs> ComputePageInputs(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups,
        IReadOnlyList<FractalWikiCommunityEdge> groupEdges,
        IReadOnlyList<FractalWikiHowtoTopic> howtoTopics,
        IReadOnlyList<FractalWikiModelingContext> modelingContexts,
        IReadOnlyDictionary<string, string> memberContext,
        IReadOnlyList<string> inventory)
    {
        var edgesJson = new JsonArray();
        foreach (var edge in groupEdges)
        {
            edgesJson.Add(new JsonObject
            {
                ["from"] = edge.FromCommunity,
                ["to"] = edge.ToCommunity,
                ["kind"] = edge.Kind,
                ["count"] = edge.Count,
            });
        }

        var overviewGroupsJson = new JsonArray();
        var codeMapGroupsJson = new JsonArray();
        foreach (var group in Sorted(groups))
        {
            var memberIds = new JsonArray();
            var memberDetails = new JsonArray();
            foreach (var member in group.Members.Order(StringComparer.Ordinal))
            {
                memberIds.Add(member);
                if (snapshot.Symbols.TryGetValue(member, out var symbol))
                {
                    memberDetails.Add(new JsonObject
                    {
                        ["id"] = symbol.SymbolId,
                        ["name"] = symbol.Name,
                        ["kind"] = symbol.Kind,
                        ["path"] = snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId),
                        ["line"] = symbol.StartLine,
                    });
                }
            }

            overviewGroupsJson.Add(new JsonObject { ["id"] = group.Id, ["name"] = group.Name, ["members"] = memberIds });
            codeMapGroupsJson.Add(new JsonObject { ["id"] = group.Id, ["name"] = group.Name, ["members"] = memberDetails });
        }

        var processesJson = new JsonArray();
        foreach (var process in snapshot.Processes)
        {
            processesJson.Add(new JsonObject
            {
                ["id"] = process.ProcessId,
                ["name"] = process.Name,
                ["type"] = process.ProcessType,
                ["steps"] = process.StepCount,
            });
        }

        var overviewStructure = new JsonObject
        {
            ["page"] = "overview",
            ["files"] = snapshot.FileCount,
            ["symbols"] = snapshot.SymbolCount,
            ["groups"] = overviewGroupsJson,
            ["edges"] = edgesJson,
            ["processes"] = processesJson,
        };
        var codeMapStructure = new JsonObject
        {
            ["page"] = "code-map",
            ["groups"] = codeMapGroupsJson,
        };

        // api-surface: public symbols with group assignment, signature and location.
        var memberGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var member in group.Members)
            {
                memberGroup[member] = group.Id;
            }
        }

        var apiJson = new JsonArray();
        foreach (var symbol in snapshot.PublicApi)
        {
            apiJson.Add(new JsonObject
            {
                ["id"] = symbol.SymbolId,
                ["name"] = symbol.Name,
                ["kind"] = symbol.Kind,
                ["signature"] = symbol.Signature,
                ["path"] = snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId),
                ["line"] = symbol.StartLine,
                ["group"] = memberGroup.GetValueOrDefault(symbol.SymbolId, ""),
            });
        }

        // rules/boundaries: import cycles + cross-group calls (pre-review names).
        var cyclesJson = new JsonArray();
        foreach (var cycle in snapshot.ImportCycles)
        {
            cyclesJson.Add(new JsonObject
            {
                ["kind"] = cycle.Kind,
                ["members"] = string.Join("|", cycle.Members),
            });
        }

        var boundariesStructure = new JsonObject
        {
            ["page"] = "boundaries",
            ["cycles"] = cyclesJson,
            ["edges"] = edgesJson.DeepClone(),
            ["groups"] = overviewGroupsJson.DeepClone(),
        };

        // troubleshooting/diagnostics: indexing diagnostic distribution.
        var diagJson = new JsonArray();
        foreach (var count in snapshot.DiagnosticCounts)
        {
            diagJson.Add(new JsonObject { ["kind"] = count.Kind, ["severity"] = count.Severity, ["count"] = count.Count });
        }

        var inputs = new Dictionary<string, FractalWikiPageInputs>(StringComparer.Ordinal)
        {
            [RootIndexPage] = new(FractalWikiMeta.HashText("page:root-index:v3"), ""),
            [MigrationMapPage] = new(FractalWikiMeta.HashText(
                "page:migration-map:v2\n" + string.Join('\n', inventory)), ""),
            [ImplIndexPage] = new(FractalWikiMeta.HashText("page:impl-index:v1"), ""),
            [PlaneIndexPage] = new(FractalWikiMeta.HashText(
                "page:plane-index:v2\nhowto=" + (howtoTopics.Count > 0)), ""),
            [OverviewIndexPage] = new(FractalWikiMeta.HashText("page:overview-index:v1"), ""),
            [RulesIndexPage] = new(FractalWikiMeta.HashText("page:rules-index:v1"), ""),
            [ReferenceIndexPage] = new(FractalWikiMeta.HashText("page:reference-index:v1"), ""),
            [TroubleshootingIndexPage] = new(FractalWikiMeta.HashText("page:troubleshooting-index:v1"), ""),
            [CodeMapPage] = new(FractalWikiMeta.HashText(codeMapStructure.ToJsonString()), ""),
            [ApiSurfacePage] = new(FractalWikiMeta.HashText(
                new JsonObject { ["page"] = "api-surface", ["symbols"] = apiJson }.ToJsonString()), ""),
        };

        inputs[ArchitecturePage] = WithNarrative(
            FractalWikiMeta.HashText(overviewStructure.ToJsonString()),
            BuildNarrativeUserPrompt(snapshot, groups, groupEdges));
        inputs[BoundariesPage] = WithNarrative(
            FractalWikiMeta.HashText(boundariesStructure.ToJsonString()),
            BuildBoundariesNarrativePrompt(snapshot, groups, groupEdges));
        inputs[DiagnosticsPage] = WithNarrative(
            FractalWikiMeta.HashText(new JsonObject { ["page"] = "diagnostics", ["counts"] = diagJson }.ToJsonString()),
            BuildDiagnosticsNarrativePrompt(snapshot));

        if (howtoTopics.Count > 0)
        {
            inputs[HowtoIndexPage] = new(FractalWikiMeta.HashText(
                "page:howto-index:v1\n" + string.Join('\n', howtoTopics.Select(t => t.Slug + "=" + t.GroupName))), "");
            foreach (var topic in howtoTopics)
            {
                var stepsJson = new JsonArray();
                foreach (var process in topic.Processes)
                {
                    stepsJson.Add(new JsonObject
                    {
                        ["id"] = process.ProcessId,
                        ["name"] = process.Name,
                        ["type"] = process.ProcessType,
                        ["steps"] = process.StepCount,
                        ["chain"] = string.Join("|", snapshot.ProcessSteps
                            .GetValueOrDefault(process.ProcessId, []).Select(s => s.SymbolId)),
                    });
                }

                var memberJson = new JsonArray();
                foreach (var member in topic.Members)
                {
                    var symbol = snapshot.Symbols.GetValueOrDefault(member);
                    memberJson.Add(new JsonObject { ["id"] = member, ["name"] = symbol?.Name ?? member, ["kind"] = symbol?.Kind ?? "" });
                }

                var howtoStructure = new JsonObject
                {
                    ["page"] = "howto",
                    ["group"] = topic.GroupId,
                    ["name"] = topic.GroupName,
                    ["members"] = memberJson,
                    ["processes"] = stepsJson,
                };
                inputs[HowtoTopicPage(topic)] = WithNarrative(
                    FractalWikiMeta.HashText(howtoStructure.ToJsonString()),
                    BuildHowtoNarrativePrompt(snapshot, topic));
            }
        }

        // Modeling hashes (modeling-fractal T1.1 skeleton + T2.1 leaves, design §2): the contexts
        // navigation index and the glossary rebuild only when the context set changes; every
        // per-context page hash is bound to exactly the facts its skeleton renders. objects and
        // workflows leaves carry LLM narrative slots (location facts stay out of the prompts).
        inputs[ModelingIndexPage] = new(FractalWikiMeta.HashText("page:modeling-index:v1"), "");
        inputs[ModelingDomainIndexPage] = new(FractalWikiMeta.HashText("page:modeling-domain-index:v2"), "");
        inputs[ModelingGlossaryPage] = new(FractalWikiMeta.HashText(
            "page:modeling-glossary:v1\n"
            + string.Join('\n', modelingContexts.Select(c => $"{c.Slug}={c.Name}|{c.Members.Count}"))), "");
        inputs[ModelingContextsIndexPage] = new(FractalWikiMeta.HashText(
            "page:modeling-contexts-index:v1\n"
            + string.Join('\n', modelingContexts.Select(c => $"{c.Slug}={c.Name}|{c.Members.Count}"))), "");
        foreach (var context in modelingContexts)
        {
            var memberIds = new JsonArray();
            var memberDetails = new JsonArray();
            foreach (var member in context.Members)
            {
                memberIds.Add(member);
                if (snapshot.Symbols.TryGetValue(member, out var symbol))
                {
                    memberDetails.Add(new JsonObject
                    {
                        ["id"] = symbol.SymbolId,
                        ["name"] = symbol.Name,
                        ["kind"] = symbol.Kind,
                        ["path"] = snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId),
                        ["line"] = symbol.StartLine,
                    });
                }
            }

            var neighborsJson = new JsonArray();
            foreach (var other in modelingContexts.Where(c => !ReferenceEquals(c, context)))
            {
                neighborsJson.Add($"{other.Slug}={other.Name}");
            }

            inputs[ContextPage(context, "index.md")] = new(FractalWikiMeta.HashText(new JsonObject
            {
                ["page"] = "modeling-context-index",
                ["name"] = context.Name,
                ["slug"] = context.Slug,
                ["members"] = memberIds,
                ["neighbors"] = neighborsJson,
                ["hasObjects"] = context.ObjectTypeIds.Count > 0,
                ["hasWorkflows"] = context.Workflows.Count > 0,
            }.ToJsonString()), "");
            inputs[ContextPage(context, "code-map.md")] = new(FractalWikiMeta.HashText(new JsonObject
            {
                ["page"] = "modeling-code-map",
                ["name"] = context.Name,
                ["members"] = memberDetails,
                ["objectPages"] = new JsonArray([.. context.ObjectPages.Select(p => (JsonNode)$"{p.TypeId}={p.Slug}")]),
            }.ToJsonString()), "");
            if (context.ObjectTypeIds.Count > 0)
            {
                var typesJson = new JsonArray();
                foreach (var page in context.ObjectPages)
                {
                    var symbol = snapshot.Symbols.GetValueOrDefault(page.TypeId);
                    typesJson.Add(new JsonObject
                    {
                        ["id"] = page.TypeId,
                        ["slug"] = page.Slug,
                        ["name"] = symbol?.Name ?? page.TypeId,
                        ["kind"] = symbol?.Kind ?? "",
                    });
                }

                inputs[ContextPage(context, "objects/index.md")] = new(FractalWikiMeta.HashText(new JsonObject
                {
                    ["page"] = "modeling-objects-index",
                    ["name"] = context.Name,
                    ["types"] = typesJson,
                    ["overflow"] = context.ObjectOverflow,
                }.ToJsonString()), "");
                foreach (var page in context.ObjectPages)
                {
                    inputs[ContextPage(context, $"objects/{page.Slug}.md")] = WithNarrative(
                        FractalWikiMeta.HashText(BuildObjectLeafStructure(snapshot, context, page).ToJsonString()),
                        BuildObjectNarrativePrompt(snapshot, context, page));
                }
            }

            if (context.Workflows.Count > 0)
            {
                var flowsJson = new JsonArray();
                foreach (var page in context.WorkflowPages)
                {
                    flowsJson.Add(new JsonObject
                    {
                        ["id"] = page.Process.ProcessId,
                        ["slug"] = page.Slug,
                        ["name"] = page.Process.Name,
                        ["type"] = page.Process.ProcessType,
                        ["steps"] = page.Process.StepCount,
                    });
                }

                inputs[ContextPage(context, "workflows/index.md")] = new(FractalWikiMeta.HashText(new JsonObject
                {
                    ["page"] = "modeling-workflows-index",
                    ["name"] = context.Name,
                    ["processes"] = flowsJson,
                    ["overflow"] = context.WorkflowOverflow,
                }.ToJsonString()), "");
                foreach (var page in context.WorkflowPages)
                {
                    inputs[ContextPage(context, $"workflows/{page.Slug}.md")] = WithNarrative(
                        FractalWikiMeta.HashText(BuildWorkflowLeafStructure(snapshot, context, memberContext, page).ToJsonString()),
                        BuildWorkflowNarrativePrompt(snapshot, context, memberContext, page));
                }
            }
        }

        return inputs;

        static FractalWikiPageInputs WithNarrative(string structureHash, string prompt) =>
            new(structureHash, FractalWikiMeta.HashText(prompt), prompt);
    }

    // ---- modeling leaf facts (modeling-fractal T2.1, design §2) ------------------------------

    /// <summary>Members of a type: symbols whose parent_id is the type, in declaration order.</summary>
    private static IReadOnlyList<FractalWikiSymbol> TypeMembers(FractalWikiSnapshot snapshot, string typeId) =>
        snapshot.Symbols.Values
            .Where(s => string.Equals(s.ParentId, typeId, StringComparison.Ordinal))
            .OrderBy(s => s.StartLine)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.SymbolId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>EXTENDS/IMPLEMENTS edges leaving a type, canonical order.</summary>
    private static IReadOnlyList<FractalWikiTypeRelation> TypeRelationsOf(FractalWikiSnapshot snapshot, string typeId) =>
        snapshot.TypeRelations
            .Where(r => string.Equals(r.FromId, typeId, StringComparison.Ordinal))
            .ToArray();

    /// <summary>Execution flows whose step chain touches the type or one of its members (≤5, process id asc).</summary>
    private static IReadOnlyList<CodeProcessSummary> FlowsTouchingType(
        FractalWikiSnapshot snapshot, string typeId, IReadOnlyList<FractalWikiSymbol> members)
    {
        var ids = new HashSet<string>(members.Select(m => m.SymbolId), StringComparer.Ordinal) { typeId };
        return snapshot.Processes
            .Where(p => ids.Contains(p.EntrySymbolId)
                || snapshot.ProcessSteps.GetValueOrDefault(p.ProcessId, []).Any(s => ids.Contains(s.SymbolId)))
            .OrderBy(p => p.ProcessId, StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static string SymbolLocation(FractalWikiSnapshot snapshot, FractalWikiSymbol symbol) =>
        $"{snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId)}:{symbol.StartLine}";

    /// <summary>Deterministic facts the objects leaf skeleton renders — exactly its structureHash input.</summary>
    private static JsonObject BuildObjectLeafStructure(
        FractalWikiSnapshot snapshot, FractalWikiModelingContext context, FractalWikiObjectPage page)
    {
        var type = snapshot.Symbols.GetValueOrDefault(page.TypeId);
        var members = TypeMembers(snapshot, page.TypeId);
        var membersJson = new JsonArray();
        foreach (var member in members)
        {
            membersJson.Add(new JsonObject
            {
                ["id"] = member.SymbolId,
                ["name"] = member.Name,
                ["kind"] = member.Kind,
                ["location"] = SymbolLocation(snapshot, member),
            });
        }

        var relationsJson = new JsonArray();
        foreach (var relation in TypeRelationsOf(snapshot, page.TypeId))
        {
            relationsJson.Add(new JsonObject
            {
                ["kind"] = relation.Kind,
                ["target"] = snapshot.Symbols.TryGetValue(relation.ToId, out var t) ? t.Name : relation.ToId,
            });
        }

        var flowsJson = new JsonArray();
        foreach (var flow in FlowsTouchingType(snapshot, page.TypeId, members))
        {
            flowsJson.Add(new JsonObject { ["id"] = flow.ProcessId, ["name"] = flow.Name });
        }

        return new JsonObject
        {
            ["page"] = "modeling-object-leaf",
            ["context"] = context.Name,
            ["slug"] = page.Slug,
            ["id"] = page.TypeId,
            ["name"] = type?.Name ?? page.TypeId,
            ["kind"] = type?.Kind ?? "",
            ["signature"] = type?.Signature ?? "",
            ["location"] = type is null ? "" : SymbolLocation(snapshot, type),
            ["members"] = membersJson,
            ["relations"] = relationsJson,
            ["flows"] = flowsJson,
        };
    }

    /// <summary>Deterministic facts the workflows leaf skeleton renders — exactly its structureHash input.</summary>
    private static JsonObject BuildWorkflowLeafStructure(
        FractalWikiSnapshot snapshot,
        FractalWikiModelingContext context,
        IReadOnlyDictionary<string, string> memberContext,
        FractalWikiWorkflowPage page)
    {
        var entry = snapshot.Symbols.GetValueOrDefault(page.Process.EntrySymbolId);
        var stepsJson = new JsonArray();
        foreach (var step in snapshot.ProcessSteps.GetValueOrDefault(page.Process.ProcessId, []))
        {
            var symbol = snapshot.Symbols.GetValueOrDefault(step.SymbolId);
            stepsJson.Add(new JsonObject
            {
                ["step"] = step.Step,
                ["id"] = step.SymbolId,
                ["name"] = symbol?.Name ?? step.SymbolId,
                ["via"] = step.ViaKind,
                ["location"] = symbol is null ? "" : SymbolLocation(snapshot, symbol),
                ["crossContext"] = CrossContext(memberContext, context, step.SymbolId),
            });
        }

        return new JsonObject
        {
            ["page"] = "modeling-workflow-leaf",
            ["context"] = context.Name,
            ["slug"] = page.Slug,
            ["id"] = page.Process.ProcessId,
            ["name"] = page.Process.Name,
            ["type"] = page.Process.ProcessType,
            ["entryKind"] = page.Process.EntryKind,
            ["entryName"] = entry?.Name ?? page.Process.EntrySymbolId,
            ["entryLocation"] = entry is null ? "" : SymbolLocation(snapshot, entry),
            ["steps"] = stepsJson,
        };
    }

    /// <summary>Owning context of a step symbol when it differs from the page's context ("" otherwise).</summary>
    private static string CrossContext(
        IReadOnlyDictionary<string, string> memberContext, FractalWikiModelingContext context, string symbolId) =>
        memberContext.TryGetValue(symbolId, out var owner)
            && !string.Equals(owner, context.Name, StringComparison.Ordinal)
            ? owner
            : "";

    /// <summary>Objects leaf narrative prompt: names/kinds only — symbol locations stay out so a moved line never re-calls the LLM.</summary>
    private static string BuildObjectNarrativePrompt(
        FractalWikiSnapshot snapshot, FractalWikiModelingContext context, FractalWikiObjectPage page)
    {
        var type = snapshot.Symbols.GetValueOrDefault(page.TypeId);
        var members = TypeMembers(snapshot, page.TypeId);
        var builder = new StringBuilder();
        builder.Append("Domain object ").Append(type?.Name ?? page.TypeId)
            .Append(" (").Append(type?.Kind ?? "?").Append(") in bounded context \"")
            .Append(context.Name).Append("\".\n\nMembers:\n");
        if (members.Count == 0)
        {
            builder.Append("- none recorded\n");
        }

        foreach (var member in members.Take(15))
        {
            builder.Append("- ").Append(member.Name).Append(" (").Append(member.Kind).Append(")\n");
        }

        builder.Append("\nInheritance:\n");
        var relations = TypeRelationsOf(snapshot, page.TypeId);
        if (relations.Count == 0)
        {
            builder.Append("- none recorded\n");
        }

        foreach (var relation in relations)
        {
            builder.Append("- ").Append(relation.Kind).Append(' ')
                .Append(snapshot.Symbols.TryGetValue(relation.ToId, out var t) ? t.Name : relation.ToId).Append('\n');
        }

        builder.Append("\nExecution flows touching this object:\n");
        var flows = FlowsTouchingType(snapshot, page.TypeId, members);
        if (flows.Count == 0)
        {
            builder.Append("- none extracted\n");
        }

        foreach (var flow in flows)
        {
            builder.Append("- ").Append(flow.Name).Append('\n');
        }

        builder.Append("\nWrite the narrative section of this object's canonical modeling page: " +
            "what the object means in this bounded context and which invariants matter.");
        return builder.ToString();
    }

    /// <summary>Workflows leaf narrative prompt: step names only (no locations, same re-call discipline).</summary>
    private static string BuildWorkflowNarrativePrompt(
        FractalWikiSnapshot snapshot,
        FractalWikiModelingContext context,
        IReadOnlyDictionary<string, string> memberContext,
        FractalWikiWorkflowPage page)
    {
        var builder = new StringBuilder();
        builder.Append("Workflow ").Append(page.Process.Name).Append(" (").Append(page.Process.ProcessType)
            .Append(") entering bounded context \"").Append(context.Name).Append("\".\n\nStep chain:\n");
        var steps = snapshot.ProcessSteps.GetValueOrDefault(page.Process.ProcessId, []);
        if (steps.Count == 0)
        {
            builder.Append("- no step chain recorded\n");
        }

        foreach (var step in steps)
        {
            var name = snapshot.Symbols.TryGetValue(step.SymbolId, out var symbol) ? symbol.Name : step.SymbolId;
            builder.Append("- ").Append(step.Step).Append(": ").Append(name);
            var cross = CrossContext(memberContext, context, step.SymbolId);
            if (cross.Length > 0)
            {
                builder.Append(" (crosses into context ").Append(cross).Append(')');
            }

            builder.Append('\n');
        }

        builder.Append("\nWrite the narrative section of this workflow's canonical modeling page: " +
            "what the flow accomplishes and where it crosses context boundaries.");
        return builder.ToString();
    }

    // ---- Phase 0 -------------------------------------------------------------------------

    internal static async Task<FractalWikiSnapshot> CollectSnapshotAsync(CozoOm om, CancellationToken cancellationToken)
    {
        var communities = await om.ListCommunitiesAsync(cancellationToken);
        var members = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var community in communities)
        {
            members[community.CommunityId] = await om.GetCommunityMembersAsync(community.CommunityId, cancellationToken);
        }

        var symbolRows = await om.Runtime.Store.RunAsync(
            "?[symbol_id, name, kind, file_id, start_line, signature, visibility, exported, sym_key, parent_id] := " +
            "*ck_symbol{ symbol_id, name, kind, file_id, start_line, signature, visibility, exported, sym_key, parent_id }",
            cancellationToken: cancellationToken);
        var symbols = new Dictionary<string, FractalWikiSymbol>(StringComparer.Ordinal);
        var publicApi = new List<FractalWikiPublicSymbol>();
        foreach (var row in symbolRows.Rows)
        {
            var symbolId = AsString(row[0]);
            symbols[symbolId] = new FractalWikiSymbol(
                symbolId, AsString(row[1]), AsString(row[2]), AsString(row[3]), AsInt(row[4]),
                AsString(row[8]), AsString(row[5]), AsString(row[9]));
            // Public API surface (design §1 reference/api-surface.md): exported or visibility=public.
            if (AsBool(row[7]) || string.Equals(AsString(row[6]), "public", StringComparison.Ordinal))
            {
                publicApi.Add(new FractalWikiPublicSymbol(
                    symbolId, AsString(row[1]), AsString(row[2]), AsString(row[5]), AsString(row[3]), AsInt(row[4])));
            }
        }

        publicApi.Sort((a, b) =>
        {
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.SymbolId, b.SymbolId);
        });

        var fileRows = await om.Runtime.Store.RunAsync(
            "?[file_id, path] := *ck_file{ file_id, path }",
            cancellationToken: cancellationToken);
        var filePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in fileRows.Rows)
        {
            filePaths[AsString(row[0])] = AsString(row[1]);
        }

        // Inter-community CALLS/IMPORTS aggregation: endpoints resolved through ck_member.
        var memberCommunity = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (communityId, communityMembers) in members)
        {
            foreach (var member in communityMembers)
            {
                memberCommunity[member] = communityId;
            }
        }

        var edgeRows = await om.Runtime.Store.RunAsync(
            """?[from_id, to_id, kind] := *ck_edge{ from_id, to_id, kind }, is_in(kind, ["CALLS", "IMPORTS"])""",
            cancellationToken: cancellationToken);
        var communityEdgeCounts = new Dictionary<(string From, string To, string Kind), int>();
        // Weighted symbol degree over CALLS/IMPORTS (modeling-fractal design §1 level-3 fallback).
        var symbolDegrees = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasImportEdges = false;
        foreach (var row in edgeRows.Rows)
        {
            hasImportEdges |= string.Equals(AsString(row[2]), CodeEdgeKinds.Imports, StringComparison.Ordinal);
            symbolDegrees[AsString(row[0])] = symbolDegrees.GetValueOrDefault(AsString(row[0])) + 1;
            symbolDegrees[AsString(row[1])] = symbolDegrees.GetValueOrDefault(AsString(row[1])) + 1;
            if (memberCommunity.TryGetValue(AsString(row[0]), out var fromCommunity)
                && memberCommunity.TryGetValue(AsString(row[1]), out var toCommunity)
                && !string.Equals(fromCommunity, toCommunity, StringComparison.Ordinal))
            {
                var key = (fromCommunity, toCommunity, AsString(row[2]));
                communityEdgeCounts[key] = communityEdgeCounts.GetValueOrDefault(key) + 1;
            }
        }

        var communityEdges = communityEdgeCounts
            .Select(kv => new FractalWikiCommunityEdge(kv.Key.From, kv.Key.To, kv.Key.Kind, kv.Value))
            .OrderBy(e => e.FromCommunity, StringComparer.Ordinal)
            .ThenBy(e => e.ToCommunity, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToArray();

        // EXTENDS/IMPLEMENTS relations (modeling-fractal T2.1 objects leaves).
        var relationRows = await om.Runtime.Store.RunAsync(
            """?[from_id, to_id, kind] := *ck_edge{ from_id, to_id, kind }, is_in(kind, ["EXTENDS", "IMPLEMENTS"])""",
            cancellationToken: cancellationToken);
        var typeRelations = relationRows.Rows
            .Select(row => new FractalWikiTypeRelation(AsString(row[0]), AsString(row[1]), AsString(row[2])))
            .Distinct()
            .OrderBy(r => r.FromId, StringComparer.Ordinal)
            .ThenBy(r => r.ToId, StringComparer.Ordinal)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ToArray();

        var processes = await om.ListProcessesAsync(cancellationToken);

        // Execution flow step chains (howto skeletons): ck_process_step ordered by step.
        var stepRows = await om.Runtime.Store.RunAsync(
            "?[process_id, step, symbol_id, via_kind] := *ck_process_step{ process_id, step, symbol_id, via_kind }",
            cancellationToken: cancellationToken);
        var processSteps = new Dictionary<string, IReadOnlyList<FractalWikiProcessStep>>(StringComparer.Ordinal);
        foreach (var group in stepRows.Rows
            .Select(row => (ProcessId: AsString(row[0]), Step: AsInt(row[1]), SymbolId: AsString(row[2]), ViaKind: AsString(row[3])))
            .GroupBy(row => row.ProcessId, StringComparer.Ordinal))
        {
            processSteps[group.Key] = group
                .OrderBy(row => row.Step)
                .Select(row => new FractalWikiProcessStep(row.Step, row.SymbolId, row.ViaKind))
                .ToArray();
        }

        // rules/boundaries inputs: import cycles (deterministic SCC detection, zero LLM).
        // Guard: the cozo StronglyConnectedComponents fixed rule panics natively on an empty
        // input graph, so only run cycle detection when at least one IMPORTS edge exists.
        IReadOnlyList<CodeCycle> importCycles = hasImportEdges
            ? await om.DetectCyclesAsync(CycleKind.Import, cancellationToken)
            : [];

        // troubleshooting/diagnostics inputs: indexing diagnostic distribution.
        var diagRows = await om.Runtime.Store.RunAsync(
            "?[kind, severity, count(diagnostic_id)] := *ck_diagnostic{ diagnostic_id, kind, severity }",
            cancellationToken: cancellationToken);
        var diagnosticCounts = diagRows.Rows
            .Select(row => new FractalWikiDiagnosticCount(AsString(row[0]), AsString(row[1]), AsInt(row[2])))
            .OrderBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.Severity, StringComparer.Ordinal)
            .ToArray();

        return new FractalWikiSnapshot(
            communities, members, symbols, filePaths, communityEdges, processes,
            processSteps, publicApi, importCycles, diagnosticCounts, symbolDegrees,
            typeRelations, filePaths.Count, symbols.Count);
    }

    // ---- Phase 1 -------------------------------------------------------------------------

    internal static List<FractalWikiGroup> BuildGroups(FractalWikiSnapshot snapshot, int minGroupSize, List<string> diagnostics)
    {
        var groups = new List<FractalWikiGroup>();
        foreach (var community in snapshot.Communities)
        {
            var members = snapshot.CommunityMembers.GetValueOrDefault(community.CommunityId, []);
            if (members.Count < minGroupSize)
            {
                diagnostics.Add($"phase1: community {community.CommunityId} below MinGroupSize={minGroupSize}, not promoted to a group");
                continue;
            }

            groups.Add(new FractalWikiGroup(community.CommunityId, community.Label, [.. members]));
        }

        return groups;
    }

    /// <summary>Materializes the groups recorded by an approved .grouping-review.json (design §2 Phase 1.3: "人拍板").</summary>
    private static List<FractalWikiGroup> BuildApprovedGroups(FractalWikiGroupingReview review)
    {
        var groups = new List<FractalWikiGroup>(review.Groups.Count);
        foreach (var entry in review.Groups)
        {
            var group = new FractalWikiGroup(entry.Id, entry.Name, [.. entry.Members.Order(StringComparer.Ordinal)]);
            foreach (var source in entry.SourceCommunities)
            {
                if (!string.Equals(source, entry.Id, StringComparison.Ordinal))
                {
                    group.MergedFrom.Add(source);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// One batched review call; whitelist merge/rename/flag, everything else is ignored with a
    /// diagnostic. Returns whether the call succeeded plus the raw suggested actions (for the
    /// .grouping-review.json draft).
    /// </summary>
    private static async Task<(bool Succeeded, IReadOnlyList<JsonObject> Actions)> ReviewGroupsAsync(
        ILlmClient llm,
        FractalWikiSnapshot snapshot,
        List<FractalWikiGroup> groups,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        string response;
        try
        {
            var completion = await llm.CompleteAsync(
                ReviewSystemPrompt,
                BuildReviewUserPrompt(snapshot, groups),
                new LlmOptions(Temperature: 0),
                cancellationToken);
            response = completion.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add($"phase1: grouping review llm call failed, keeping deterministic grouping: {ex.Message}");
            return (false, []);
        }

        var actions = ParseReviewActions(response, diagnostics);
        foreach (var action in actions)
        {
            ApplyReviewAction(action, groups, diagnostics);
        }

        return (true, actions);
    }

    private static string BuildReviewUserPrompt(FractalWikiSnapshot snapshot, IReadOnlyList<FractalWikiGroup> groups)
    {
        var groupsJson = new JsonArray();
        foreach (var group in groups)
        {
            var samples = new JsonArray();
            foreach (var member in group.Members.Take(8))
            {
                samples.Add(snapshot.Symbols.TryGetValue(member, out var symbol) ? symbol.Name : member);
            }

            groupsJson.Add(new JsonObject
            {
                ["id"] = group.Id,
                ["name"] = group.Name,
                ["symbolCount"] = group.Members.Count,
                ["sampleSymbols"] = samples,
            });
        }

        var edgesJson = new JsonArray();
        foreach (var edge in snapshot.CommunityEdges)
        {
            edgesJson.Add(new JsonObject
            {
                ["from"] = edge.FromCommunity,
                ["to"] = edge.ToCommunity,
                ["kind"] = edge.Kind,
                ["count"] = edge.Count,
            });
        }

        return "Deterministic grouping (community detection) of the repository. Allowed review actions: " +
            "merge/rename/flag only.\n\ngroups:\n" + groupsJson.ToJsonString() +
            "\n\ninterGroupEdges:\n" + edgesJson.ToJsonString() +
            "\n\nRespond with a JSON array of actions ([] for none).";
    }

    private static IReadOnlyList<JsonObject> ParseReviewActions(string response, List<string> diagnostics)
    {
        var start = response.IndexOf('[');
        var end = response.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            diagnostics.Add("phase1: grouping review response carried no JSON array, keeping deterministic grouping");
            return [];
        }

        try
        {
            var node = JsonNode.Parse(response[start..(end + 1)]);
            return node is JsonArray array
                ? array.OfType<JsonObject>().ToArray()
                : [];
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"phase1: grouping review response was not valid JSON, keeping deterministic grouping: {ex.Message}");
            return [];
        }
    }

    private static void ApplyReviewAction(JsonObject action, List<FractalWikiGroup> groups, List<string> diagnostics)
    {
        var kind = action["action"]?.GetValue<string>() ?? "";
        switch (kind)
        {
            case "rename":
            {
                var id = action["id"]?.GetValue<string>() ?? "";
                var name = action["name"]?.GetValue<string>() ?? "";
                var group = groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));
                if (group is null || name.Length == 0)
                {
                    diagnostics.Add($"phase1: ignored rename action for unknown group '{id}'");
                    return;
                }

                group.Name = name;
                return;
            }

            case "merge":
            {
                var a = action["a"]?.GetValue<string>() ?? "";
                var b = action["b"]?.GetValue<string>() ?? "";
                var target = groups.FirstOrDefault(g => string.Equals(g.Id, a, StringComparison.Ordinal));
                var source = groups.FirstOrDefault(g => string.Equals(g.Id, b, StringComparison.Ordinal));
                if (target is null || source is null || ReferenceEquals(target, source))
                {
                    diagnostics.Add($"phase1: ignored merge action for unknown groups '{a}'/'{b}'");
                    return;
                }

                target.Members.AddRange(source.Members);
                target.Members.Sort(StringComparer.Ordinal);
                target.MergedFrom.Add(source.Id);
                groups.Remove(source);
                return;
            }

            case "flag":
            {
                var id = action["id"]?.GetValue<string>() ?? "";
                var reason = action["reason"]?.GetValue<string>() ?? "";
                diagnostics.Add($"phase1: llm flagged group {id} for human review: {reason}");
                return;
            }

            default:
                diagnostics.Add($"phase1: ignored unsupported llm review action '{kind}' (whitelist: merge/rename/flag)");
                return;
        }
    }

    /// <summary>Re-aggregates the community-level edges onto the final (possibly merged) groups.</summary>
    private static IReadOnlyList<FractalWikiCommunityEdge> AggregateGroupEdges(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups)
    {
        var communityGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            communityGroup[group.Id] = group.Id;
            foreach (var merged in group.MergedFrom)
            {
                communityGroup[merged] = group.Id;
            }
        }

        var counts = new Dictionary<(string From, string To, string Kind), int>();
        foreach (var edge in snapshot.CommunityEdges)
        {
            if (communityGroup.TryGetValue(edge.FromCommunity, out var fromGroup)
                && communityGroup.TryGetValue(edge.ToCommunity, out var toGroup)
                && !string.Equals(fromGroup, toGroup, StringComparison.Ordinal))
            {
                var key = (fromGroup, toGroup, edge.Kind);
                counts[key] = counts.GetValueOrDefault(key) + edge.Count;
            }
        }

        return counts
            .Select(kv => new FractalWikiCommunityEdge(kv.Key.From, kv.Key.To, kv.Key.Kind, kv.Value))
            .OrderBy(e => e.FromCommunity, StringComparer.Ordinal)
            .ThenBy(e => e.ToCommunity, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToArray();
    }

    // ---- Phase 2: narrative helpers --------------------------------------------------------

    /// <summary>
    /// Matches the llm section marker comments. LLM output must never carry these: an
    /// unsanitized <c>&lt;!-- llm:end --&gt;</c> inside the narrative would close the marker
    /// section early and leak narrative content into the deterministic skeleton.
    /// </summary>
    private static readonly Regex LlmMarkerPattern = new(
        @"<!--\s*llm:(begin|end)[^>]*-->",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Strips llm marker comments out of LLM-produced narrative text (with a diagnostic when it fired).</summary>
    private static string SanitizeNarrative(string text, List<string> diagnostics)
    {
        var sanitized = LlmMarkerPattern.Replace(text, "").Trim();
        if (!string.Equals(sanitized, text.Trim(), StringComparison.Ordinal))
        {
            diagnostics.Add("phase2: llm narrative carried llm section markers; stripped to protect the skeleton");
        }

        return sanitized;
    }

    private static string BuildNarrativeUserPrompt(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups,
        IReadOnlyList<FractalWikiCommunityEdge> groupEdges)
    {
        var builder = new StringBuilder();
        builder.Append("Repository stats: ").Append(snapshot.FileCount).Append(" files, ")
            .Append(snapshot.SymbolCount).Append(" symbols.\n\nGroups:\n");
        foreach (var group in groups)
        {
            builder.Append("- ").Append(group.Id).Append(" \"").Append(group.Name).Append("\": ")
                .Append(group.Members.Count).Append(" symbols");
            var samples = group.Members
                .Select(m => snapshot.Symbols.TryGetValue(m, out var s) ? s.Name : m)
                .Take(8)
                .ToArray();
            if (samples.Length > 0)
            {
                builder.Append(" (e.g. ").Append(string.Join(", ", samples)).Append(')');
            }

            builder.Append('\n');
        }

        builder.Append("\nInter-group edges:\n");
        foreach (var edge in groupEdges)
        {
            builder.Append("- ").Append(edge.FromCommunity).Append(" -> ").Append(edge.ToCommunity)
                .Append(" (").Append(edge.Kind).Append(" x").Append(edge.Count).Append(")\n");
        }

        builder.Append("\nTop execution flows:\n");
        foreach (var process in snapshot.Processes.Take(10))
        {
            builder.Append("- ").Append(process.Name).Append(" (").Append(process.ProcessType)
                .Append(", ").Append(process.StepCount).Append(" steps)\n");
        }

        builder.Append("\nWrite the narrative section for the global architecture overview page.");
        return builder.ToString();
    }

    private static string BuildHowtoNarrativePrompt(FractalWikiSnapshot snapshot, FractalWikiHowtoTopic topic)
    {
        var builder = new StringBuilder();
        builder.Append("Module group ").Append(topic.GroupId).Append(" \"").Append(topic.GroupName)
            .Append("\" with ").Append(topic.Members.Count).Append(" symbols.\n\nKey symbols:\n");
        foreach (var member in topic.Members.Take(10))
        {
            var symbol = snapshot.Symbols.GetValueOrDefault(member);
            builder.Append("- ").Append(symbol?.Name ?? member).Append(" (").Append(symbol?.Kind ?? "?").Append(")\n");
        }

        builder.Append("\nExecution flows entering this group:\n");
        if (topic.Processes.Count == 0)
        {
            builder.Append("- none extracted\n");
        }

        foreach (var process in topic.Processes)
        {
            builder.Append("- ").Append(process.Name).Append(" (").Append(process.ProcessType)
                .Append(", ").Append(process.StepCount).Append(" steps)\n");
        }

        builder.Append("\nWrite the narrative section of the \"working with this module group\" howto page: " +
            "how a maintainer typically enters, extends and verifies changes in this group.");
        return builder.ToString();
    }

    private static string BuildBoundariesNarrativePrompt(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups,
        IReadOnlyList<FractalWikiCommunityEdge> groupEdges)
    {
        var builder = new StringBuilder();
        builder.Append("Import cycles:\n");
        if (snapshot.ImportCycles.Count == 0)
        {
            builder.Append("- none detected\n");
        }

        foreach (var cycle in snapshot.ImportCycles)
        {
            builder.Append("- ").Append(string.Join(" -> ", cycle.Members
                .Select(m => snapshot.Symbols.TryGetValue(m, out var s) ? s.Name : m))).Append('\n');
        }

        var names = groups.ToDictionary(g => g.Id, g => g.Name, StringComparer.Ordinal);
        builder.Append("\nTop cross-group calls:\n");
        foreach (var edge in groupEdges.OrderByDescending(e => e.Count).Take(10))
        {
            builder.Append("- ").Append(names.GetValueOrDefault(edge.FromCommunity, edge.FromCommunity))
                .Append(" -> ").Append(names.GetValueOrDefault(edge.ToCommunity, edge.ToCommunity))
                .Append(" (").Append(edge.Kind).Append(" x").Append(edge.Count).Append(")\n");
        }

        builder.Append("\nWrite the narrative section of the boundary-rules page: which module " +
            "boundaries the facts above imply and what a maintainer must not cross.");
        return builder.ToString();
    }

    private static string BuildDiagnosticsNarrativePrompt(FractalWikiSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append("Indexing diagnostic distribution:\n");
        if (snapshot.DiagnosticCounts.Count == 0)
        {
            builder.Append("- none recorded\n");
        }

        foreach (var count in snapshot.DiagnosticCounts)
        {
            builder.Append("- ").Append(count.Kind).Append(" / ").Append(count.Severity)
                .Append(": ").Append(count.Count).Append('\n');
        }

        builder.Append("\nWrite the narrative section of the diagnostics troubleshooting page: " +
            "what the distribution suggests and where to look first.");
        return builder.ToString();
    }

    // ---- Phase 3: renderers ----------------------------------------------------------------

    private static Dictionary<string, Func<string>> BuildRenderers(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups,
        IReadOnlyList<FractalWikiCommunityEdge> groupEdges,
        IReadOnlyList<FractalWikiHowtoTopic> howtoTopics,
        IReadOnlyList<FractalWikiModelingContext> modelingContexts,
        IReadOnlyDictionary<string, string> memberContext,
        IReadOnlyDictionary<string, string> narratives,
        IReadOnlyList<string> previousLedger,
        string? ledgerEntry,
        string today)
    {
        var renderers = new Dictionary<string, Func<string>>(StringComparer.Ordinal)
        {
            [RootIndexPage] = () => RenderRootIndex(today),
            [MigrationMapPage] = () => RenderMigrationMap(previousLedger, ledgerEntry, today),
            [ImplIndexPage] = () => RenderImplIndex(today),
            [PlaneIndexPage] = () => RenderPlaneIndex(howtoTopics.Count > 0, today),
            [OverviewIndexPage] = () => RenderCategoryIndex(
                "Overview", "心智模型、架构与组成", "操作步骤（→howto/）与查表（→reference/）",
                [("Architecture", "architecture.md")], today),
            [ArchitecturePage] = () => RenderArchitecture(
                snapshot, groups, groupEdges, narratives.GetValueOrDefault(ArchitecturePage, PendingNarrative), today),
            [RulesIndexPage] = () => RenderCategoryIndex(
                "Rules", "实现约束与边界护栏", "modeling policy 正文（只链接不复制）",
                [("Boundary Rules", "boundaries.md")], today),
            [BoundariesPage] = () => RenderBoundaries(
                snapshot, groups, groupEdges, narratives.GetValueOrDefault(BoundariesPage, PendingNarrative), today),
            [ReferenceIndexPage] = () => RenderCategoryIndex(
                "Reference", "code map 与 public API 查表", "心智模型（→overview/）",
                [("Code Map", "code-map.md"), ("API Surface", "api-surface.md")], today),
            [CodeMapPage] = () => RenderCodeMap(snapshot, groups, today),
            [ApiSurfacePage] = () => RenderApiSurface(snapshot, groups, today),
            [TroubleshootingIndexPage] = () => RenderCategoryIndex(
                "Troubleshooting", "故障模式与索引诊断", "实现约束（→rules/）",
                [("Diagnostics", "diagnostics.md")], today),
            [DiagnosticsPage] = () => RenderDiagnostics(
                snapshot, narratives.GetValueOrDefault(DiagnosticsPage, PendingNarrative), today),
        };

        if (howtoTopics.Count > 0)
        {
            renderers[HowtoIndexPage] = () => RenderCategoryIndex(
                "Howto", "按模块组的可重复维护操作", "架构叙述（→overview/）与约束（→rules/）",
                howtoTopics.Select(t => ($"Working with {t.GroupName}", $"working-with-{t.Slug}.md")).ToArray(), today);
            foreach (var topic in howtoTopics)
            {
                var page = HowtoTopicPage(topic);
                renderers[page] = () => RenderHowtoTopic(
                    snapshot, topic, narratives.GetValueOrDefault(page, PendingNarrative), today);
            }
        }

        renderers[ModelingIndexPage] = () => RenderModelingIndex(today);
        renderers[ModelingDomainIndexPage] = () => RenderModelingDomainIndex(today);
        renderers[ModelingGlossaryPage] = () => RenderModelingGlossary(modelingContexts, today);
        renderers[ModelingContextsIndexPage] = () => RenderModelingContextsIndex(modelingContexts, today);
        foreach (var context in modelingContexts)
        {
            renderers[ContextPage(context, "index.md")] = () => RenderModelingContextIndex(context, modelingContexts, today);
            renderers[ContextPage(context, "code-map.md")] = () => RenderModelingContextCodeMap(snapshot, context, today);
            if (context.ObjectTypeIds.Count > 0)
            {
                renderers[ContextPage(context, "objects/index.md")] = () => RenderModelingObjectsIndex(snapshot, context, today);
                foreach (var objectPage in context.ObjectPages)
                {
                    var relative = ContextPage(context, $"objects/{objectPage.Slug}.md");
                    renderers[relative] = () => RenderModelingObjectLeaf(
                        snapshot, context, objectPage, narratives.GetValueOrDefault(relative, PendingNarrative), today);
                }
            }

            if (context.Workflows.Count > 0)
            {
                renderers[ContextPage(context, "workflows/index.md")] = () => RenderModelingWorkflowsIndex(context, today);
                foreach (var workflowPage in context.WorkflowPages)
                {
                    var relative = ContextPage(context, $"workflows/{workflowPage.Slug}.md");
                    renderers[relative] = () => RenderModelingWorkflowLeaf(
                        snapshot, context, memberContext, workflowPage,
                        narratives.GetValueOrDefault(relative, PendingNarrative), today);
                }
            }
        }

        return renderers;
    }

    private const string PromotesFromDefault = "代码图谱（ck_* 事实）build 再生成";
    private const string PromotesToDefault = "人工审阅后的正式 docs";

    private static string RenderRootIndex(string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, "# Wiki Preview");
        Line(builder);
        builder.Append(FractalDocFormat.FullManifest(
            "生成的 docs 预览根：根导航与迁移台账（镜像 docs/ 布局）",
            "知识正文（→ impl/ 各类目叶子）",
            PromotesFromDefault,
            "人工审阅后并入正式 docs/"));
        Line(builder);
        Line(builder, "## Contents");
        Line(builder);
        Line(builder, "- [Implementation Knowledge](impl/index.md)");
        Line(builder, "- [Modeling Knowledge](modeling/index.md)");
        Line(builder, "- [Migration Map](migration-map.md)");
        return builder.ToString();
    }

    // ---- modeling skeleton renderers (modeling-fractal T1.1) --------------------------------

    private static string RenderModelingIndex(string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, "# Modeling Knowledge");
        Line(builder);
        builder.Append(FractalDocFormat.FullManifest(
            "建模本体知识系统的 plane 目录（docs-modeling-fractal）",
            "实现知识（→ impl/，引用不复制）",
            PromotesFromDefault,
            "人工审阅后的正式 docs/modeling/"));
        Line(builder);
        Line(builder, "## Planes");
        Line(builder);
        Line(builder, "- [domain](domain/index.md) — canonical 业务本体平面");
        return builder.ToString();
    }

    private static string RenderModelingDomainIndex(string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, "# Domain Plane");
        Line(builder);
        builder.Append(FractalDocFormat.FullManifest(
            "canonical 业务本体：contexts 及其类目真源",
            "derived 投影平面（本管线不生成）与实现细节（→ impl/）",
            PromotesFromDefault,
            "人工审阅后的正式 docs/modeling/domain/"));
        Line(builder);
        Line(builder, "## Contexts");
        Line(builder);
        Line(builder, "- [contexts](contexts/index.md) — context 入口表");
        Line(builder, "- [glossary](glossary.md) — 本 plane 术语表");
        return builder.ToString();
    }

    /// <summary>Domain glossary (M-A2 plane-root fixed three): canonical terms = the discovered contexts.</summary>
    private static string RenderModelingGlossary(IReadOnlyList<FractalWikiModelingContext> contexts, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleReference, today));
        Line(builder);
        Line(builder, "# Domain Glossary");
        Line(builder);
        if (contexts.Count == 0)
        {
            Line(builder, "- No terms recorded yet.");
            return builder.ToString();
        }

        Line(builder, "| Term | Context | 说明 |");
        Line(builder, "|---|---|---|");
        foreach (var context in contexts)
        {
            Line(builder, $"| {context.Name} | [{context.Slug}](contexts/{context.Slug}/index.md) "
                + $"| 社群聚合发现的边界单元（{context.Members.Count} 个符号成员） |");
        }

        return builder.ToString();
    }

    private static string RenderModelingContextsIndex(IReadOnlyList<FractalWikiModelingContext> contexts, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, "# Contexts");
        Line(builder);
        builder.Append(FractalDocFormat.FullManifest(
            "domain plane 的 context 边界单元入口表",
            "context 正文与类目真源（→ 各 context 目录）",
            PromotesFromDefault,
            PromotesToDefault));
        Line(builder);
        if (contexts.Count == 0)
        {
            Line(builder, "No contexts discovered. Run community detection before building the wiki.");
            return builder.ToString();
        }

        Line(builder, "| Context | 成员数 | 入口 |");
        Line(builder, "|---|---|---|");
        foreach (var context in contexts)
        {
            Line(builder, $"| {context.Name} | {context.Members.Count} | [{context.Slug}]({context.Slug}/index.md) |");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Context index (docs-modeling-fractal §3 template): full 目录职责 section, mandatory
    /// Boundary, category navigation table (only the categories that exist) and the code-map
    /// link. doc_role=canonical + context frontmatter — the domain truth-source entry.
    /// </summary>
    private static string RenderModelingContextIndex(
        FractalWikiModelingContext context,
        IReadOnlyList<FractalWikiModelingContext> allContexts,
        string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleCanonical, today, context: context.Name));
        Line(builder);
        Line(builder, $"# {context.Name} Context");
        Line(builder);
        builder.Append(FractalDocFormat.FullManifest(
            $"context {context.Name} 的建模真源边界与类目导航",
            "相邻 context 的真源（→ ../）与实现细节（→ impl/）",
            "代码图谱社群聚合（ck_community/ck_member）再生成",
            PromotesToDefault));
        Line(builder);
        Line(builder, "## Boundary");
        Line(builder);
        Line(builder, $"本 context 由社群与命名空间聚合确定性发现，拥有 {context.Members.Count} 个符号成员"
            + $"（其中 public 类型 {context.ObjectTypeIds.Count} 个、入口执行流 {context.Workflows.Count} 条）。");
        Line(builder);
        var neighbors = allContexts.Where(c => !ReferenceEquals(c, context)).ToArray();
        if (neighbors.Length > 0)
        {
            // M-B2: Not Owned Here is mandatory whenever adjacent contexts exist.
            Line(builder, "## Not Owned Here");
            Line(builder);
            foreach (var neighbor in neighbors)
            {
                Line(builder, $"- [{neighbor.Name}](../{neighbor.Slug}/index.md) 的真源归其自身 context");
            }

            Line(builder);
        }

        if (context.ObjectTypeIds.Count > 0 || context.Workflows.Count > 0)
        {
            Line(builder, "| 类目 | 职责 | 何时阅读 |");
            Line(builder, "|---|---|---|");
            if (context.ObjectTypeIds.Count > 0)
            {
                Line(builder, "| [objects](objects/index.md) | 对象（public 类型）真源 | 查类型语义与结构 |");
            }

            if (context.Workflows.Count > 0)
            {
                Line(builder, "| [workflows](workflows/index.md) | 多步执行流真源 | 查流程链与入口 |");
            }

            Line(builder);
        }

        Line(builder, "## Code Map");
        Line(builder);
        Line(builder, "- [code-map.md](code-map.md)");
        return builder.ToString();
    }

    /// <summary>Zero-LLM context code map: member table with file:line locations plus objects leaf links (T2.1).</summary>
    private static string RenderModelingContextCodeMap(FractalWikiSnapshot snapshot, FractalWikiModelingContext context, string today)
    {
        var objectSlugs = context.ObjectPages.ToDictionary(p => p.TypeId, p => p.Slug, StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleReference, today, context: context.Name));
        Line(builder);
        Line(builder, $"# {context.Name} Code Map");
        Line(builder);
        Line(builder, "| Symbol | Kind | Location | Doc |");
        Line(builder, "|---|---|---|---|");
        foreach (var member in context.Members)
        {
            if (!snapshot.Symbols.TryGetValue(member, out var symbol))
            {
                continue;
            }

            var path = snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId);
            var doc = objectSlugs.TryGetValue(member, out var slug)
                ? $"[objects/{slug}.md](objects/{slug}.md)"
                : "—";
            Line(builder, $"| `{symbol.Name}` | {symbol.Kind} | `{path}:{symbol.StartLine}` | {doc} |");
        }

        return builder.ToString();
    }

    /// <summary>objects category index: navigation to the paged type leaves (+ truncation note).</summary>
    private static string RenderModelingObjectsIndex(FractalWikiSnapshot snapshot, FractalWikiModelingContext context, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleGuide, today, context: context.Name));
        Line(builder);
        Line(builder, "# Objects");
        Line(builder);
        Line(builder, FractalDocFormat.SlimManifest(
            $"context {context.Name} 的对象（public 类型）真源",
            "多步流程（→workflows/）与实现细节（→ impl/）",
            "代码图谱（ck_symbol 类型事实）再生成",
            PromotesToDefault));
        Line(builder);
        Line(builder, "| Type | Kind | Page |");
        Line(builder, "|---|---|---|");
        foreach (var page in context.ObjectPages)
        {
            var symbol = snapshot.Symbols.GetValueOrDefault(page.TypeId);
            Line(builder, $"| `{symbol?.Name ?? page.TypeId}` | {symbol?.Kind ?? ""} | [{page.Slug}.md]({page.Slug}.md) |");
        }

        if (context.ObjectOverflow > 0)
        {
            Line(builder);
            Line(builder, $"- 其余 {context.ObjectOverflow} 个类型超出 MaxObjectsPerContext 预算未成页（见 [code-map](../code-map.md)）。");
        }

        return builder.ToString();
    }

    /// <summary>workflows category index: navigation to the paged flow leaves (+ truncation note).</summary>
    private static string RenderModelingWorkflowsIndex(FractalWikiModelingContext context, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleGuide, today, context: context.Name));
        Line(builder);
        Line(builder, "# Workflows");
        Line(builder);
        Line(builder, FractalDocFormat.SlimManifest(
            $"context {context.Name} 的多步执行流真源",
            "对象结构语义（→objects/）与实现细节（→ impl/）",
            "代码图谱（ck_process 步骤链）再生成",
            PromotesToDefault));
        Line(builder);
        Line(builder, "| Process | Name | Type | Steps | Page |");
        Line(builder, "|---|---|---|---|---|");
        foreach (var page in context.WorkflowPages)
        {
            Line(builder, $"| {page.Process.ProcessId} | {page.Process.Name} | {page.Process.ProcessType} "
                + $"| {page.Process.StepCount} | [{page.Slug}.md]({page.Slug}.md) |");
        }

        if (context.WorkflowOverflow > 0)
        {
            Line(builder);
            Line(builder, $"- 其余 {context.WorkflowOverflow} 条执行流超出 MaxWorkflowsPerContext 预算未成页。");
        }

        return builder.ToString();
    }

    /// <summary>
    /// objects leaf (modeling-fractal T2.1, design §2): deterministic skeleton — kind/signature,
    /// member table (name/kind/file:line), EXTENDS/IMPLEMENTS, participating execution flows (≤5)
    /// and the code-map link — plus an explicitly marked LLM narrative slot. doc_role=canonical
    /// (M-E5), context frontmatter (M-E1), no code paths in frontmatter (M-D3).
    /// </summary>
    private static string RenderModelingObjectLeaf(
        FractalWikiSnapshot snapshot,
        FractalWikiModelingContext context,
        FractalWikiObjectPage page,
        string narrative,
        string today)
    {
        var type = snapshot.Symbols.GetValueOrDefault(page.TypeId);
        var name = type?.Name ?? page.TypeId;
        var members = TypeMembers(snapshot, page.TypeId);
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleCanonical, today, context: context.Name));
        Line(builder);
        Line(builder, $"# {name}");
        Line(builder);
        Line(builder, FractalDocFormat.SlimManifest(
            $"对象 {name} 在 context {context.Name} 内的结构与行为真源",
            "跨对象规则与实现细节（→ impl/）",
            "代码图谱（ck_symbol/ck_edge 事实）再生成",
            PromotesToDefault));
        Line(builder);
        Line(builder, "## Facts");
        Line(builder);
        Line(builder, $"- kind: `{type?.Kind ?? "?"}`");
        if (type is { Signature.Length: > 0 })
        {
            Line(builder, $"- signature: `{type.Signature}`");
        }

        if (type is not null)
        {
            Line(builder, $"- declared at: `{SymbolLocation(snapshot, type)}`");
        }

        Line(builder);
        Line(builder, "## Members");
        Line(builder);
        if (members.Count == 0)
        {
            Line(builder, "- No members recorded.");
        }
        else
        {
            Line(builder, "| Member | Kind | Location |");
            Line(builder, "|---|---|---|");
            foreach (var member in members)
            {
                Line(builder, $"| `{member.Name}` | {member.Kind} | `{SymbolLocation(snapshot, member)}` |");
            }
        }

        Line(builder);
        Line(builder, "## Inheritance");
        Line(builder);
        var relations = TypeRelationsOf(snapshot, page.TypeId);
        if (relations.Count == 0)
        {
            Line(builder, "- None recorded.");
        }
        else
        {
            foreach (var relation in relations)
            {
                var target = snapshot.Symbols.GetValueOrDefault(relation.ToId);
                var location = target is null ? "" : $" (`{SymbolLocation(snapshot, target)}`)";
                Line(builder, $"- {relation.Kind} `{target?.Name ?? relation.ToId}`{location}");
            }
        }

        Line(builder);
        Line(builder, "## Execution Flows");
        Line(builder);
        var flows = FlowsTouchingType(snapshot, page.TypeId, members);
        if (flows.Count == 0)
        {
            Line(builder, "- None extracted.");
        }
        else
        {
            foreach (var flow in flows)
            {
                Line(builder, $"- {flow.Name} (`{flow.ProcessId}`)");
            }
        }

        Line(builder);
        Line(builder, "## Code Map");
        Line(builder);
        Line(builder, "- [context code map](../code-map.md)");
        Line(builder);
        AppendNarrativeSection(builder, narrative);
        return builder.ToString();
    }

    /// <summary>
    /// workflows leaf (modeling-fractal T2.1, design §2): entry symbol/kind, the ck_process_step
    /// chain as a table (step/symbol/file:line) with cross-context annotation, the code-map link
    /// and a marked LLM narrative slot. doc_role=canonical (M-E5), context frontmatter (M-E1).
    /// </summary>
    private static string RenderModelingWorkflowLeaf(
        FractalWikiSnapshot snapshot,
        FractalWikiModelingContext context,
        IReadOnlyDictionary<string, string> memberContext,
        FractalWikiWorkflowPage page,
        string narrative,
        string today)
    {
        var entry = snapshot.Symbols.GetValueOrDefault(page.Process.EntrySymbolId);
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("domain", FractalDocFormat.RoleCanonical, today, context: context.Name));
        Line(builder);
        Line(builder, $"# {page.Process.Name}");
        Line(builder);
        Line(builder, FractalDocFormat.SlimManifest(
            $"执行流 {page.Process.Name} 在 context {context.Name} 内的步骤链真源",
            "对象结构语义（→../objects/）与实现细节（→ impl/）",
            "代码图谱（ck_process/ck_process_step 事实）再生成",
            PromotesToDefault));
        Line(builder);
        Line(builder, "## Entry");
        Line(builder);
        var entryLocation = entry is null ? "" : $"，`{SymbolLocation(snapshot, entry)}`";
        Line(builder, $"- `{entry?.Name ?? page.Process.EntrySymbolId}`（{page.Process.EntryKind}{entryLocation}）");
        Line(builder, $"- process type: {page.Process.ProcessType}，{page.Process.StepCount} steps");
        Line(builder);
        Line(builder, "## Steps");
        Line(builder);
        var steps = snapshot.ProcessSteps.GetValueOrDefault(page.Process.ProcessId, []);
        if (steps.Count == 0)
        {
            Line(builder, "- No step chain recorded.");
        }
        else
        {
            Line(builder, "| Step | Symbol | Via | Location | Cross-Context |");
            Line(builder, "|---|---|---|---|---|");
            foreach (var step in steps)
            {
                var symbol = snapshot.Symbols.GetValueOrDefault(step.SymbolId);
                var location = symbol is null ? "" : $"`{SymbolLocation(snapshot, symbol)}`";
                var cross = CrossContext(memberContext, context, step.SymbolId);
                Line(builder, $"| {step.Step} | `{symbol?.Name ?? step.SymbolId}` | {step.ViaKind} "
                    + $"| {location} | {(cross.Length > 0 ? cross : "—")} |");
            }
        }

        Line(builder);
        Line(builder, "## Code Map");
        Line(builder);
        Line(builder, "- [context code map](../code-map.md)");
        Line(builder);
        AppendNarrativeSection(builder, narrative);
        return builder.ToString();
    }

    private static string RenderImplIndex(string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, "# Implementation Knowledge");
        Line(builder);
        builder.Append(FractalDocFormat.FullManifest(
            "实现知识系统的 plane 目录（docs-engineering-fractal）",
            "建模本体（→ docs/modeling/，引用不复制）",
            "各 plane 类目沉淀",
            "正式 docs/impl/"));
        Line(builder);
        Line(builder, "## Planes");
        Line(builder);
        Line(builder, "- [global](global/index.md) — 跨 plane 实现知识");
        return builder.ToString();
    }

    private static string RenderPlaneIndex(bool hasHowto, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, "# Global Plane");
        Line(builder);
        builder.Append(FractalDocFormat.FullManifest(
            "跨 plane 的实现与维护知识（架构、操作、约束、查表、排障）",
            "单一子系统专属知识（→ docs/impl/<plane>/）",
            PromotesFromDefault,
            "人工审阅后的正式 docs/impl/global/"));
        Line(builder);
        Line(builder, "## Categories");
        Line(builder);
        Line(builder, "| 类目 | 说明 |");
        Line(builder, "|---|---|");
        Line(builder, "| [overview](overview/index.md) | 心智模型、架构、组成 |");
        if (hasHowto)
        {
            Line(builder, "| [howto](howto/index.md) | 可重复的维护操作 |");
        }

        Line(builder, "| [rules](rules/index.md) | 实现约束与护栏 |");
        Line(builder, "| [reference](reference/index.md) | code map 与 API 查表 |");
        Line(builder, "| [troubleshooting](troubleshooting/index.md) | 故障模式与诊断 |");
        return builder.ToString();
    }

    /// <summary>Standard category index: slim manifest block directly under the H1 (E-D1/E-D2), then navigation links only (E-F1).</summary>
    private static string RenderCategoryIndex(
        string title,
        string holds,
        string excludes,
        IReadOnlyList<(string Label, string Target)> links,
        string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, $"# {title}");
        Line(builder);
        Line(builder, FractalDocFormat.SlimManifest(holds, excludes, PromotesFromDefault, PromotesToDefault));
        Line(builder);
        foreach (var (label, target) in links)
        {
            Line(builder, $"- [{label}]({target})");
        }

        return builder.ToString();
    }

    private static string RenderArchitecture(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups,
        IReadOnlyList<FractalWikiCommunityEdge> groupEdges,
        string narrative,
        string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleGuide, today));
        Line(builder);
        Line(builder, "# Architecture");
        Line(builder);
        Line(builder, $"{snapshot.FileCount} files, {snapshot.SymbolCount} symbols, {groups.Count} groups, {snapshot.Processes.Count} execution flows.");
        Line(builder);
        Line(builder, "## Groups");
        Line(builder);
        Line(builder, "| Group | Name | Symbols |");
        Line(builder, "|---|---|---|");
        foreach (var group in Sorted(groups))
        {
            Line(builder, $"| {group.Id} | {group.Name} | {group.Members.Count} |");
        }

        Line(builder);
        Line(builder, "## Module Edges");
        Line(builder);
        Line(builder, "```mermaid");
        Line(builder, "graph LR");
        foreach (var group in Sorted(groups))
        {
            Line(builder, $"  {MermaidId(group.Id)}[\"{group.Name}\"]");
        }

        foreach (var edge in groupEdges)
        {
            Line(builder, $"  {MermaidId(edge.FromCommunity)} -->|\"{edge.Kind} x{edge.Count}\"| {MermaidId(edge.ToCommunity)}");
        }

        Line(builder, "```");
        Line(builder);
        Line(builder, "## Execution Flows");
        Line(builder);
        if (snapshot.Processes.Count == 0)
        {
            Line(builder, "- None extracted.");
        }
        else
        {
            Line(builder, "| Process | Name | Type | Steps |");
            Line(builder, "|---|---|---|---|");
            foreach (var process in snapshot.Processes)
            {
                Line(builder, $"| {process.ProcessId} | {process.Name} | {process.ProcessType} | {process.StepCount} |");
            }
        }

        Line(builder);
        AppendNarrativeSection(builder, narrative);
        return builder.ToString();
    }

    /// <summary>Zero-LLM reference page: group → member symbol table with file:line locations.</summary>
    private static string RenderCodeMap(FractalWikiSnapshot snapshot, IReadOnlyList<FractalWikiGroup> groups, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleReference, today));
        Line(builder);
        Line(builder, "# Code Map");
        Line(builder);
        if (groups.Count == 0)
        {
            Line(builder, "No groups detected. Run community detection before building the wiki.");
            return builder.ToString();
        }

        foreach (var group in Sorted(groups))
        {
            Line(builder, $"## {group.Name} (`{group.Id}`)");
            Line(builder);
            Line(builder, "| Symbol | Kind | Location |");
            Line(builder, "|---|---|---|");
            foreach (var member in group.Members.Order(StringComparer.Ordinal))
            {
                if (!snapshot.Symbols.TryGetValue(member, out var symbol))
                {
                    continue;
                }

                var path = snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId);
                Line(builder, $"| `{symbol.Name}` | {symbol.Kind} | `{path}:{symbol.StartLine}` |");
            }

            Line(builder);
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>Zero-LLM reference page: public (exported) symbols by group (design §1 api-surface).</summary>
    private static string RenderApiSurface(FractalWikiSnapshot snapshot, IReadOnlyList<FractalWikiGroup> groups, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleReference, today));
        Line(builder);
        Line(builder, "# API Surface");
        Line(builder);
        Line(builder, "Public (exported) symbols by group; generated from `ck_symbol`, zero narrative.");
        Line(builder);
        if (snapshot.PublicApi.Count == 0)
        {
            Line(builder, "- No public symbols indexed.");
            return builder.ToString();
        }

        var memberGroup = new Dictionary<string, FractalWikiGroup>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var member in group.Members)
            {
                memberGroup[member] = group;
            }
        }

        var sections = snapshot.PublicApi
            .GroupBy(symbol => memberGroup.GetValueOrDefault(symbol.SymbolId))
            .OrderBy(section => section.Key?.Id ?? "￿", StringComparer.Ordinal);
        foreach (var section in sections)
        {
            Line(builder, section.Key is null ? "## Ungrouped" : $"## {section.Key.Name} (`{section.Key.Id}`)");
            Line(builder);
            Line(builder, "| Symbol | Kind | Signature | Location |");
            Line(builder, "|---|---|---|---|");
            foreach (var symbol in section)
            {
                var path = snapshot.FilePaths.GetValueOrDefault(symbol.FileId, symbol.FileId);
                var signature = symbol.Signature.Length > 0 ? $"`{symbol.Signature}`" : "";
                Line(builder, $"| `{symbol.Name}` | {symbol.Kind} | {signature} | `{path}:{symbol.StartLine}` |");
            }

            Line(builder);
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>howto topic page: deterministic skeleton (entry processes, step chains, key symbols) + marked llm narrative.</summary>
    private static string RenderHowtoTopic(
        FractalWikiSnapshot snapshot,
        FractalWikiHowtoTopic topic,
        string narrative,
        string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleHowto, today));
        Line(builder);
        Line(builder, $"# Working with {topic.GroupName}");
        Line(builder);
        Line(builder, "## Entry Points");
        Line(builder);
        if (topic.Processes.Count == 0)
        {
            Line(builder, "- No extracted execution flow enters this group.");
        }
        else
        {
            Line(builder, "| Process | Name | Type | Steps |");
            Line(builder, "|---|---|---|---|");
            foreach (var process in topic.Processes)
            {
                Line(builder, $"| {process.ProcessId} | {process.Name} | {process.ProcessType} | {process.StepCount} |");
            }
        }

        var chains = topic.Processes
            .Select(p => (Process: p, Steps: snapshot.ProcessSteps.GetValueOrDefault(p.ProcessId, [])))
            .Where(pair => pair.Steps.Count > 0)
            .ToArray();
        if (chains.Length > 0)
        {
            Line(builder);
            Line(builder, "## Flow Steps");
            foreach (var (process, steps) in chains)
            {
                Line(builder);
                Line(builder, $"### {process.Name}");
                Line(builder);
                var ordinal = 1;
                foreach (var step in steps)
                {
                    var name = snapshot.Symbols.TryGetValue(step.SymbolId, out var symbol) ? symbol.Name : step.SymbolId;
                    Line(builder, $"{ordinal}. `{name}`");
                    ordinal++;
                }
            }
        }

        Line(builder);
        Line(builder, "## Key Symbols");
        Line(builder);
        Line(builder, "| Symbol | Kind |");
        Line(builder, "|---|---|");
        foreach (var member in topic.Members.Take(10))
        {
            var symbol = snapshot.Symbols.GetValueOrDefault(member);
            Line(builder, $"| `{symbol?.Name ?? member}` | {symbol?.Kind ?? ""} |");
        }

        Line(builder);
        AppendNarrativeSection(builder, narrative);
        return builder.ToString();
    }

    /// <summary>rules/boundaries: import cycles + top cross-group calls skeleton + marked llm narrative.</summary>
    private static string RenderBoundaries(
        FractalWikiSnapshot snapshot,
        IReadOnlyList<FractalWikiGroup> groups,
        IReadOnlyList<FractalWikiCommunityEdge> groupEdges,
        string narrative,
        string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleRules, today));
        Line(builder);
        Line(builder, "# Boundary Rules");
        Line(builder);
        Line(builder, "## Import Cycles");
        Line(builder);
        if (snapshot.ImportCycles.Count == 0)
        {
            Line(builder, "- None detected.");
        }
        else
        {
            var ordinal = 1;
            foreach (var cycle in snapshot.ImportCycles)
            {
                var names = cycle.Members
                    .Select(m => snapshot.Symbols.TryGetValue(m, out var s) ? s.Name : m)
                    .Select(n => $"`{n}`");
                Line(builder, $"{ordinal}. {string.Join(" → ", names)}");
                ordinal++;
            }
        }

        Line(builder);
        Line(builder, "## Top Cross-Group Calls");
        Line(builder);
        if (groupEdges.Count == 0)
        {
            Line(builder, "- None detected.");
        }
        else
        {
            var names = groups.ToDictionary(g => g.Id, g => g.Name, StringComparer.Ordinal);
            Line(builder, "| From | To | Kind | Count |");
            Line(builder, "|---|---|---|---|");
            foreach (var edge in groupEdges.OrderByDescending(e => e.Count)
                .ThenBy(e => e.FromCommunity, StringComparer.Ordinal)
                .ThenBy(e => e.ToCommunity, StringComparer.Ordinal)
                .Take(10))
            {
                Line(builder, $"| {names.GetValueOrDefault(edge.FromCommunity, edge.FromCommunity)} "
                    + $"| {names.GetValueOrDefault(edge.ToCommunity, edge.ToCommunity)} | {edge.Kind} | {edge.Count} |");
            }
        }

        Line(builder);
        AppendNarrativeSection(builder, narrative);
        return builder.ToString();
    }

    /// <summary>troubleshooting/diagnostics: indexing diagnostic distribution skeleton + marked llm narrative.</summary>
    private static string RenderDiagnostics(FractalWikiSnapshot snapshot, string narrative, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleTroubleshooting, today));
        Line(builder);
        Line(builder, "# Diagnostics");
        Line(builder);
        Line(builder, "## Indexing Diagnostic Distribution");
        Line(builder);
        if (snapshot.DiagnosticCounts.Count == 0)
        {
            Line(builder, "- None recorded.");
        }
        else
        {
            Line(builder, "| Kind | Severity | Count |");
            Line(builder, "|---|---|---|");
            foreach (var count in snapshot.DiagnosticCounts)
            {
                Line(builder, $"| {count.Kind} | {count.Severity} | {count.Count} |");
            }
        }

        Line(builder);
        AppendNarrativeSection(builder, narrative);
        return builder.ToString();
    }

    /// <summary>Root migration ledger (E-A2 template): fixed migration rows + append-style build ledger.</summary>
    private static string RenderMigrationMap(IReadOnlyList<string> previousLedger, string? ledgerEntry, string today)
    {
        var builder = new StringBuilder();
        builder.Append(FractalDocFormat.Frontmatter("global", FractalDocFormat.RoleReference, today, knowledgeSystem: "impl"));
        Line(builder);
        Line(builder, "# Documentation Migration Map");
        Line(builder);
        Line(builder, "| Old Path | New Path | Status | Notes |");
        Line(builder, "|----------|----------|--------|-------|");
        foreach (var (oldPath, newPath, status, notes) in MigrationRows)
        {
            Line(builder, $"| {oldPath} | {newPath} | {status} | {notes} |");
        }

        Line(builder);
        Line(builder, "## Build Ledger");
        Line(builder);
        foreach (var line in previousLedger)
        {
            Line(builder, line);
        }

        if (ledgerEntry is not null)
        {
            Line(builder, ledgerEntry);
        }

        return builder.ToString();
    }

    /// <summary>Carries over the previous build-ledger lines from the on-disk migration map (missing/unreadable → empty).</summary>
    private static IReadOnlyList<string> LoadMigrationLedger(string migrationMapPath)
    {
        try
        {
            if (!File.Exists(migrationMapPath))
            {
                return [];
            }

            var lines = File.ReadAllLines(migrationMapPath);
            var start = Array.IndexOf(lines, "## Build Ledger");
            if (start < 0)
            {
                return [];
            }

            return lines.Skip(start + 1)
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Dated ledger entry when the page inventory changed against the prior meta (null when unchanged).</summary>
    private static string? BuildLedgerEntry(string today, FractalWikiMeta? priorMeta, IReadOnlyList<string> inventory)
    {
        if (priorMeta is null)
        {
            return $"- {today}: initial inventory: {inventory.Count} page(s)";
        }

        var previous = new HashSet<string>(priorMeta.Pages.Keys, StringComparer.Ordinal);
        var current = new HashSet<string>(inventory, StringComparer.Ordinal);
        var added = inventory.Where(page => !previous.Contains(page)).ToArray();
        var removed = previous.Where(page => !current.Contains(page)).Order(StringComparer.Ordinal).ToArray();
        if (added.Length == 0 && removed.Length == 0)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (added.Length > 0)
        {
            parts.Add("added: " + string.Join(", ", added));
        }

        if (removed.Length > 0)
        {
            parts.Add("removed: " + string.Join(", ", removed));
        }

        return $"- {today}: {string.Join("; ", parts)}";
    }

    // ---- helpers -------------------------------------------------------------------------

    private static void AppendNarrativeSection(StringBuilder builder, string narrative)
    {
        Line(builder, "## Narrative");
        Line(builder);
        Line(builder, "<!-- llm:begin narrative -->");
        Line(builder, narrative);
        Line(builder, "<!-- llm:end -->");
    }

    private static IEnumerable<FractalWikiGroup> Sorted(IReadOnlyList<FractalWikiGroup> groups) =>
        groups.OrderBy(g => g.Id, StringComparer.Ordinal);

    /// <summary>Deterministic newline discipline: pages use '\n' regardless of platform.</summary>
    private static void Line(StringBuilder builder, string text = "") => builder.Append(text).Append('\n');

    private static string MermaidId(string groupId) =>
        new([.. groupId.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_')]);

    private static string AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();

    private static int AsInt(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static bool AsBool(JsonElement value) =>
        value.ValueKind == JsonValueKind.True;
}

/// <summary>Phase 0 output: the deterministic graph snapshot every later phase reads from.</summary>
internal sealed record FractalWikiSnapshot(
    IReadOnlyList<CodeCommunitySummary> Communities,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CommunityMembers,
    IReadOnlyDictionary<string, FractalWikiSymbol> Symbols,
    IReadOnlyDictionary<string, string> FilePaths,
    IReadOnlyList<FractalWikiCommunityEdge> CommunityEdges,
    IReadOnlyList<CodeProcessSummary> Processes,
    IReadOnlyDictionary<string, IReadOnlyList<FractalWikiProcessStep>> ProcessSteps,
    IReadOnlyList<FractalWikiPublicSymbol> PublicApi,
    IReadOnlyList<CodeCycle> ImportCycles,
    IReadOnlyList<FractalWikiDiagnosticCount> DiagnosticCounts,
    IReadOnlyDictionary<string, int> SymbolDegrees,
    IReadOnlyList<FractalWikiTypeRelation> TypeRelations,
    int FileCount,
    int SymbolCount);

/// <summary>Symbol slice needed by the fractal pages (name, kind, declaration site, qualified sym_key, signature, containment parent).</summary>
internal sealed record FractalWikiSymbol(string SymbolId, string Name, string Kind, string FileId, int StartLine, string SymKey = "", string Signature = "", string ParentId = "");

/// <summary>One EXTENDS/IMPLEMENTS edge (objects leaf inheritance section).</summary>
internal sealed record FractalWikiTypeRelation(string FromId, string ToId, string Kind);

/// <summary>One paged objects leaf of a context: the public type and its de-duplicated file slug.</summary>
internal sealed record FractalWikiObjectPage(string TypeId, string Slug);

/// <summary>One paged workflows leaf of a context: the execution flow and its de-duplicated file slug.</summary>
internal sealed record FractalWikiWorkflowPage(CodeProcessSummary Process, string Slug);

/// <summary>
/// One discovered modeling context (modeling-fractal T1.1): a deterministic snapshot taken
/// BEFORE the bounded LLM review — Name via the three-level fallback (namespace LCP → top-level
/// namespace segment → highest-weighted-degree type name, never a #N community label), Members
/// sorted ordinal, ObjectTypeIds the public types (objects category), Workflows the execution
/// flows entering this context (workflows category). ObjectPages/WorkflowPages are the budgeted
/// leaf subsets (T2.1, degree desc / step count desc) with the per-category overflow counts.
/// </summary>
internal sealed record FractalWikiModelingContext(
    string Name,
    string Slug,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> ObjectTypeIds,
    IReadOnlyList<CodeProcessSummary> Workflows,
    IReadOnlyList<FractalWikiObjectPage> ObjectPages,
    IReadOnlyList<FractalWikiWorkflowPage> WorkflowPages,
    int ObjectOverflow,
    int WorkflowOverflow);

/// <summary>Public API surface row (reference/api-surface.md): exported or visibility=public symbols.</summary>
internal sealed record FractalWikiPublicSymbol(string SymbolId, string Name, string Kind, string Signature, string FileId, int StartLine);

/// <summary>One ck_process_step row of an execution flow (howto skeletons).</summary>
internal sealed record FractalWikiProcessStep(int Step, string SymbolId, string ViaKind);

/// <summary>Aggregated ck_diagnostic distribution row (troubleshooting/diagnostics.md).</summary>
internal sealed record FractalWikiDiagnosticCount(string Kind, string Severity, int Count);

/// <summary>
/// One howto topic (working-with-&lt;slug&gt;.md): a deterministic snapshot of a top-N group taken
/// BEFORE the bounded LLM review, so post-hash merge/rename actions cannot move or reshape the page.
/// </summary>
internal sealed record FractalWikiHowtoTopic(
    string GroupId,
    string GroupName,
    string Slug,
    IReadOnlyList<string> Members,
    IReadOnlyList<CodeProcessSummary> Processes);

/// <summary>Aggregated CALLS/IMPORTS edge between two communities (or final groups).</summary>
internal sealed record FractalWikiCommunityEdge(string FromCommunity, string ToCommunity, string Kind, int Count);

/// <summary>One Phase 1 group: community-led, mutated only by whitelisted review actions.</summary>
internal sealed class FractalWikiGroup(string id, string name, List<string> members)
{
    public string Id { get; } = id;

    public string Name { get; set; } = name;

    public List<string> Members { get; } = members;

    /// <summary>Community ids folded into this group by merge actions (for edge re-aggregation).</summary>
    public List<string> MergedFrom { get; } = [];
}
