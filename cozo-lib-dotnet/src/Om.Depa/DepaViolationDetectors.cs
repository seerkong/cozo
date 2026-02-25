using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om.Contracts.Models;

namespace Cozo.DotNet.Om.Depa;

/// <summary>One declared capsule with its internals glob (design §1.1), shared pipeline ↔ detectors.</summary>
internal sealed record DepaCapsuleInfo(string EntityId, string Name, string RootPath, string InternalsGlob);

/// <summary>One ck_external_call summary row (design §4.2), shared pipeline ↔ detectors.</summary>
internal sealed record DepaExternalCall(string CallerId, string TargetKey, int Count, string Category, string FirstFileId, int FirstLine);

/// <summary>Outcome of the ③ detection segment.</summary>
internal sealed record DepaDetectionOutcome(
    IReadOnlyList<DepaRuleFinding> BlockedFindings,
    IReadOnlyDictionary<string, string> Verdicts,
    IReadOnlyList<DepaViolationSummary> Violations);

/// <summary>
/// Step ③ of depa_scan (design §3, track add-llm-wiki-depa-conformance-tools): the eight MVP
/// red-light detectors reading ck_* observations joined with the depa_* judgement layer
/// (loaded back from the OM store, so config, heuristic AND manually upserted annotations all
/// count), materializing depa_violation entities + violates/signal edges.
///
/// Discipline (design §5): each rule checks its inputs first — missing annotations or missing
/// observation tables yield a BLOCKED finding naming the gap, never a guess and never a silent
/// PASS. Every violation carries path:line evidence (hits without a locatable site are not
/// materialized). Violation ids are derived from (rule, subject, evidence key) so repeated
/// scans upsert in place; hits that disappear are expired at scan end together with their
/// violates/signal edges — but only for rules that actually ran (BLOCKED rules keep their
/// prior findings, because "cannot judge" must not fake compliance).
/// </summary>
internal static class DepaViolationDetectors
{
    private const double MaxHeuristicViolationConfidence = 0.8;

    /// <summary>
    /// Ceiling for lexicon-signal rules (V-R1 big-bag word list, V-G1 single-implementation
    /// count): the judgement is a signal, not proof (track expand-depa-detection-rules T2.1).
    /// </summary>
    private const double SignalConfidenceCeiling = 0.7;

    /// <summary>
    /// Ceiling for phenomenon-level rules (V-A1 lock density, V-S4 file-metadata probes):
    /// the observation is a surface phenomenon of the red light, evidence is marked heuristic.
    /// </summary>
    private const double PhenomenonConfidenceCeiling = 0.6;

    /// <summary>V-A1: minimum aggregated threading-call count per symbol before the density signal fires.</summary>
    private const int ThreadingDensityThreshold = 3;

    private static readonly string[] DetectionRuleIds =
    [
        "V-A1", "V-C1", "V-D1", "V-D3", "V-E1", "V-E2", "V-E3", "V-F1", "V-F2", "V-F3",
        "V-G1", "V-L1", "V-L2", "V-L3", "V-L4", "V-L5", "V-L6", "V-P2", "V-R1", "V-S1",
        "V-S2a", "V-S2b", "V-S3", "V-S4",
    ];

    /// <summary>Required fn(runtime, input, config) role coverage checked by V-C1 (capsule-protocol CP1§5).</summary>
    private static readonly string[] RequiredParamRoles = ["runtime", "input", "config"];

    /// <summary>The first seven whitelist categories participate in V-E1 (design §4.1).</summary>
    private static readonly HashSet<string> LeakCategories = new(StringComparer.Ordinal)
    {
        "file_io", "network", "db", "process", "console", "env", "threading",
    };

    private sealed record Snap(string EntityId, IReadOnlyDictionary<string, JsonElement> Props)
    {
        public string Str(string name) =>
            Props.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        public double Num(string name, double fallback) =>
            Props.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

        public string SymbolId => Str("symbol_id");

        public string SymKey => Str("sym_key");

        public double Confidence => Num("confidence", 1.0);
    }

    private sealed record Pending(
        string RuleId,
        string Dimension,
        string SubjectEntityId,
        string SubjectSymbolId,
        string SubjectSymKey,
        string Message,
        IReadOnlyList<DepaEvidenceRef> Evidence,
        double Confidence,
        string EvidenceKey,
        (string From, string Rel, string To, Dictionary<string, object?>? Props)? Signal = null);

    private sealed record RuleOutcome(string Verdict, string BlockedMessage, IReadOnlyList<Pending> Hits)
    {
        public static RuleOutcome Blocked(string message) => new("BLOCKED", message, []);

        public static RuleOutcome Ran(IReadOnlyList<Pending> hits) => new(hits.Count > 0 ? "GAP" : "PASS", "", hits);
    }

    /// <summary>
    /// The all-BLOCKED outcome for the zero-annotation early return (delta case
    /// blocked-not-guess): every detector names its missing input instead of guessing.
    /// </summary>
    internal static (IReadOnlyList<DepaRuleFinding> Findings, IReadOnlyDictionary<string, string> Verdicts) BlockedForMissingAnnotations()
    {
        var findings = DetectionRuleIds
            .Select(rule => new DepaRuleFinding(
                rule,
                "BLOCKED",
                "",
                $"BLOCKED: no depa annotations available (depa-map.json missing or empty, no heuristic hits) — {MissingInputOf(rule)}; not guessing."))
            .ToArray();
        var verdicts = DetectionRuleIds.ToDictionary(rule => rule, _ => "BLOCKED", StringComparer.Ordinal);
        return (findings, verdicts);
    }

    private static string MissingInputOf(string ruleId) => ruleId switch
    {
        "V-A1" => "missing capsule declarations to anchor the threading-density signal (depa-map.json capsules)",
        "V-C1" => "missing depa_impl purity=core judgements and depa_runtime_param role annotations (depa-map.json cores/runtimeParams)",
        "V-D1" => "missing fact-source grading table (depa-map.json factSources)",
        "V-D3" => "missing depa_projection annotations (depa-map.json projections)",
        "V-E1" => "missing depa_impl purity=core judgements (depa-map.json cores)",
        "V-E2" => "missing depa_impl purity=core judgements (depa-map.json cores)",
        "V-E3" => "missing depa_contract annotations (depa-map.json contractPackages)",
        "V-F1" => "missing depa_runtime_param role=config annotations (depa-map.json runtimeParams)",
        "V-F2" => "missing depa_runtime_carrier annotations (depa-map.json runtimeCarrierTypes)",
        "V-F3" => "missing depa_runtime_param role=config annotations and runtime-side types (depa-map.json runtimeParams/runtimeCarrierTypes)",
        "V-G1" => "missing capsule declarations to anchor the single-implementation signal (depa-map.json capsules)",
        "V-L1" => "missing capsule declarations (depa-map.json capsules)",
        "V-L2" => "missing capsule declarations (depa-map.json capsules)",
        "V-L3" => "missing depa_contract annotations (depa-map.json contractPackages)",
        "V-L4" => "missing capsule declarations (depa-map.json capsules)",
        "V-L5" => "missing depa_contract annotations and capsule declarations (depa-map.json contractPackages/capsules)",
        "V-L6" => "missing depa_contract annotations and capsule declarations (depa-map.json contractPackages/capsules)",
        "V-P2" => "missing layers[] declaration (depa-map.json layers, low→high)",
        "V-R1" => "missing depa_runtime_carrier / runtime-role depa_runtime_param annotations (depa-map.json runtimeCarrierTypes/runtimeParams)",
        "V-S1" => "missing depa_projection annotations and fact-source grading (depa-map.json projections/factSources)",
        "V-S2a" => "missing recoveryPaths[] declaration and grade-5 checkpoint grading (depa-map.json recoveryPaths/factSources)",
        "V-S2b" => "missing recoveryPaths[] declaration and grade-4 journal grading (depa-map.json recoveryPaths/factSources)",
        "V-S3" => "missing fact-source grading table and input-role depa_runtime_param annotations (depa-map.json factSources/runtimeParams)",
        "V-S4" => "missing depa_impl purity=core judgements (depa-map.json cores)",
        _ => "missing annotations",
    };

    internal static async Task<DepaDetectionOutcome> RunAsync(
        CozoOm om,
        IReadOnlyList<CkSymbol> symbols,
        IReadOnlyList<CkEdge> edges,
        IReadOnlyDictionary<string, string> filePaths,
        IReadOnlyList<DepaExternalCall>? externalCalls,
        IReadOnlyList<DepaCapsuleInfo> capsules,
        IReadOnlyList<DepaEffectRule> effectRules,
        DepaMapConfig map,
        string scanCommit,
        CancellationToken ct)
    {
        var symbolById = symbols.ToDictionary(s => s.SymbolId, StringComparer.Ordinal);

        // Judgement layer read back from the OM store: config + heuristic annotations from this
        // scan's ①/② segments and any manually upserted depa_* entities all participate.
        var impls = await LoadTypeAsync(om, "depa_impl", ct);
        var contracts = await LoadTypeAsync(om, "depa_contract", ct);
        var projections = await LoadTypeAsync(om, "depa_projection", ct);
        var factSources = await LoadTypeAsync(om, "depa_fact_source", ct);
        var runtimeParams = await LoadTypeAsync(om, "depa_runtime_param", ct);
        var carriers = await LoadTypeAsync(om, "depa_runtime_carrier", ct);
        // Batch-1 detector inputs (track expand-depa-detection-rules T2.1): the step-② entry
        // materialization is read back so V-L4 counts capsule_exposes fan-out.
        var entrySnaps = await LoadTypeAsync(om, "depa_entry", ct);
        var exposesEdges = await ListEdgesAsync(om, "capsule_exposes", ct);

        var implBySymbolId = impls
            .Where(s => s.SymbolId.Length > 0)
            .GroupBy(s => s.SymbolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var cores = impls
            .Where(s => s.Str("purity") == "core" && s.SymbolId.Length > 0 && symbolById.ContainsKey(s.SymbolId))
            .ToArray();

        var containsChildren = edges
            .Where(e => e.Kind == "CONTAINS")
            .ToLookup(e => e.FromId, e => e.ToId, StringComparer.Ordinal);

        HashSet<string> Closure(string rootSymbolId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { rootSymbolId };
            var queue = new Queue<string>([rootSymbolId]);
            while (queue.Count > 0)
            {
                foreach (var child in containsChildren[queue.Dequeue()])
                {
                    if (seen.Add(child))
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            return seen;
        }

        // Symbols on the compliant effect path: implementations of any depa_contract (plus
        // their CONTAINS closure). Design §3 row 1: a call site inside a contract
        // implementation is not a leak.
        var contractSymbolIds = contracts.Select(c => c.SymbolId).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var contractNames = contracts
            .Select(c => c.SymbolId.Length > 0 && symbolById.TryGetValue(c.SymbolId, out var s) ? s.Name : "")
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var exemptCallers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges.Where(e => e.Kind is "IMPLEMENTS" or "METHOD_IMPLEMENTS"))
        {
            var hitsContract = contractSymbolIds.Contains(edge.ToId)
                || (edge.ToId.StartsWith("typeref:", StringComparison.Ordinal)
                    && contractNames.Any(name => edge.ToId.EndsWith($":{name}", StringComparison.Ordinal)));
            if (hitsContract)
            {
                exemptCallers.UnionWith(Closure(edge.FromId));
            }
        }

        DepaCapsuleInfo? CapsuleOf(string path)
        {
            DepaCapsuleInfo? best = null;
            foreach (var capsule in capsules)
            {
                if (IsUnderPath(path, capsule.RootPath) && (best is null || capsule.RootPath.Length > best.RootPath.Length))
                {
                    best = capsule;
                }
            }

            return best;
        }

        string PathOfEdge(CkEdge edge, CkSymbol fallback) =>
            filePaths.TryGetValue(edge.FileId, out var path) && path.Length > 0 ? path : fallback.Path;

        // Shared field lookup for the type-shape rules (V-F1/V-F3/V-R1/V-S3).
        List<CkSymbol> FieldsOf(string typeSymbolId) => edges
            .Where(e => e.FromId == typeSymbolId && e.Kind is "HAS_PROPERTY" or "CONTAINS")
            .Select(e => symbolById.GetValueOrDefault(e.ToId))
            .Where(f => f is not null && f.Kind is "field" or "property")
            .Select(f => f!)
            .ToList();

        // ---- run the detectors (each guards its own inputs first; 8 MVP + 14 batch-1
        // + 3 batch-2, track expand-depa-detection-rules T2.1/T3.1) ----
        var outcomes = new Dictionary<string, RuleOutcome>(StringComparer.Ordinal)
        {
            ["V-E1"] = DetectE1(),
            ["V-E2"] = DetectE2(),
            ["V-E3"] = DetectE3(),
            ["V-D1"] = DetectD1(),
            ["V-D3"] = DetectD3(),
            ["V-S1"] = DetectS1(),
            ["V-S2a"] = DetectS2(
                "V-S2a", 5, "checkpoint",
                "checkpoints gate recovery/startup, not live control flow (fact-source-truth 规则④)"),
            ["V-S2b"] = DetectS2(
                "V-S2b", 4, "journal",
                "journals stay side-channel; live decisions read the authoritative/control plane (fact-source-truth 规则③)"),
            ["V-S3"] = DetectS3(),
            ["V-S4"] = DetectS4(),
            ["V-P2"] = DetectP2(),
            ["V-F1"] = DetectF1(),
            ["V-F2"] = DetectF2(),
            ["V-F3"] = DetectF3(),
            ["V-L1"] = DetectL1(),
            ["V-L2"] = DetectL2(),
            ["V-L3"] = DetectL3(),
            ["V-L4"] = DetectL4(),
            ["V-L5"] = DetectL5(),
            ["V-L6"] = DetectL6(),
            ["V-R1"] = DetectR1(),
            ["V-C1"] = DetectC1(),
            ["V-G1"] = DetectG1(),
            ["V-A1"] = DetectA1(),
        };

        RuleOutcome DetectE1()
        {
            if (cores.Length == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-E1")} — cannot tell core logic from edge adapters; not guessing.");
            }

            if (externalCalls is null)
            {
                return RuleOutcome.Blocked("BLOCKED: ck_external_call observation table is absent — out-of-repo calls were not indexed, V-E1 cannot see IO targets; not guessing.");
            }

            var callsByCaller = externalCalls.ToLookup(c => c.CallerId, StringComparer.Ordinal);
            var hits = new List<Pending>();
            foreach (var core in cores)
            {
                if (exemptCallers.Contains(core.SymbolId))
                {
                    continue; // the core IS a contract implementation — compliant effect path.
                }

                var coreSym = symbolById[core.SymbolId];
                foreach (var member in Closure(core.SymbolId))
                {
                    if (exemptCallers.Contains(member))
                    {
                        continue;
                    }

                    foreach (var call in callsByCaller[member])
                    {
                        var rule = DepaEffectCatalog.Match(effectRules, call.TargetKey);
                        if (rule is null || rule.Category == "exempt_contract" || !LeakCategories.Contains(rule.Category))
                        {
                            continue;
                        }

                        var memberSym = symbolById.GetValueOrDefault(member) ?? coreSym;
                        var path = filePaths.TryGetValue(call.FirstFileId, out var p) && p.Length > 0 ? p : memberSym.Path;
                        var line = call.FirstLine > 0 ? call.FirstLine : memberSym.StartLine;
                        if (path.Length == 0 || line <= 0)
                        {
                            continue; // no locatable evidence — design §5.2 rule ①: do not materialize.
                        }

                        var more = call.Count > 1 ? $", +{call.Count - 1} more call sites" : "";
                        hits.Add(new Pending(
                            "V-E1", "effect", core.EntityId, core.SymbolId, core.SymKey,
                            $"core '{coreSym.Name}' bypasses effect contracts: '{memberSym.Name}' calls {call.TargetKey} ({rule.Category})",
                            [new DepaEvidenceRef("external_call", path, line, $"CALLS {call.TargetKey} (category={rule.Category}, count={call.Count}{more})")],
                            core.Confidence,
                            $"{member}->{call.TargetKey}",
                            (core.EntityId, "effect_leaks_through", $"depa:effectapi:{rule.Pattern}", new Dictionary<string, object?>
                            {
                                ["target_key"] = call.TargetKey,
                                ["count"] = call.Count,
                                ["first_path"] = path,
                                ["first_line"] = line,
                            })));
                    }
                }
            }

            return RuleOutcome.Ran(hits);
        }

        RuleOutcome DetectE2()
        {
            if (cores.Length == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-E2")} — cannot tell core logic from edge adapters; not guessing.");
            }

            var hits = new List<Pending>();
            var candidates = 0;
            var withSignature = 0;
            foreach (var core in cores)
            {
                var coreSym = symbolById[core.SymbolId];
                var coreCapsule = CapsuleOf(coreSym.Path);
                foreach (var member in Closure(core.SymbolId))
                {
                    foreach (var edge in edges.Where(e => e.Kind == "ACCESSES" && e.FromId == member))
                    {
                        if (!symbolById.TryGetValue(edge.ToId, out var field) || field.Kind != "field")
                        {
                            continue;
                        }

                        candidates++;
                        if (field.Signature.Length == 0)
                        {
                            continue; // modifier not observed for this field.
                        }

                        withSignature++;
                        var mutableStatic = field.Signature.Contains("static", StringComparison.Ordinal)
                            && !field.Signature.Contains("readonly", StringComparison.Ordinal)
                            && !field.Signature.Contains("const ", StringComparison.Ordinal);
                        if (!mutableStatic || CapsuleOf(field.Path)?.EntityId == coreCapsule?.EntityId)
                        {
                            continue; // own-capsule statics are the capsule's business (design §3 row 2).
                        }

                        var memberSym = symbolById.GetValueOrDefault(member) ?? coreSym;
                        hits.Add(new Pending(
                            "V-E2", "effect", core.EntityId, core.SymbolId, core.SymKey,
                            $"core '{coreSym.Name}' depends on implicit global state: '{memberSym.Name}' accesses static mutable field '{field.Name}'",
                            [new DepaEvidenceRef("access", PathOfEdge(edge, memberSym), edge.Line > 0 ? edge.Line : memberSym.StartLine,
                                $"ACCESSES static mutable field {field.Name} ({field.Signature})")],
                            core.Confidence,
                            $"{member}->{edge.ToId}"));
                    }
                }
            }

            if (candidates > 0 && withSignature == 0)
            {
                return RuleOutcome.Blocked(
                    "BLOCKED: ck_symbol.signature does not carry the static modifier for any accessed field — "
                    + "indexer upgrade needed before V-E2 can judge implicit global state; not guessing.");
            }

            return RuleOutcome.Ran(hits);
        }

        RuleOutcome DetectD1()
        {
            if (factSources.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-D1")} — no graded fact sources to check for single-writer ownership; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var fact in factSources)
            {
                var grade = (int)fact.Num("grade", 0);
                if (grade is < 1 or > 3 || fact.SymbolId.Length == 0 || !symbolById.TryGetValue(fact.SymbolId, out var factSym))
                {
                    continue; // unanchored graded facts are already surfaced by factsource_must_have_writer (BLOCKED).
                }

                var writeEdges = edges
                    .Where(e => e.Kind == "ACCESSES" && e.ToId == fact.SymbolId
                        && e.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var writers = writeEdges
                    .GroupBy(e => e.FromId, StringComparer.Ordinal)
                    .Where(g => symbolById.ContainsKey(g.Key))
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToArray();
                if (writers.Length == 0)
                {
                    continue;
                }

                var expectedOwner = fact.Str("expected_owner");
                var writerNames = writers.Select(g => symbolById[g.Key].Name).ToArray();
                string? message = null;
                if (writers.Length > 1)
                {
                    message = $"fact source '{factSym.Name}' (grade {grade}) has {writers.Length} writers: {string.Join(", ", writerNames)}";
                }
                else if (expectedOwner.Length > 0)
                {
                    var writerSym = symbolById[writers[0].Key];
                    var writerEntityId = implBySymbolId.GetValueOrDefault(writers[0].Key)?.EntityId ?? "";
                    var matches = expectedOwner == writerEntityId || expectedOwner == writerSym.SymbolId
                        || expectedOwner == writerSym.SymKey || expectedOwner == writerSym.Name;
                    if (!matches)
                    {
                        message = $"fact source '{factSym.Name}' (grade {grade}) is written by '{writerSym.Name}' but expected owner is '{expectedOwner}'";
                    }
                }

                if (message is null)
                {
                    continue;
                }

                var evidence = writers
                    .Select(g => g.OrderBy(e => e.Line).First())
                    .Select(e => new DepaEvidenceRef("write", PathOfEdge(e, factSym), e.Line > 0 ? e.Line : factSym.StartLine,
                        $"{symbolById[e.FromId].Name} writes {factSym.Name}"))
                    .ToArray();
                hits.Add(new Pending(
                    "V-D1", "data", fact.EntityId, fact.SymbolId, fact.SymKey,
                    message, evidence, fact.Confidence,
                    string.Join("|", writers.Select(g => g.Key)) + (writers.Length == 1 ? $"|expected:{expectedOwner}" : "")));
            }

            return RuleOutcome.Ran(hits);
        }

        RuleOutcome DetectS1()
        {
            // V-S1 扩展 (track expand-depa-detection-rules T2.1, catalog E3/FST②⑤): the
            // back-write sources are projections AND grade-6/7 derived fact-source nodes; the
            // protected upstreams extend from grade<=2 to grade<=3 (control facts included).
            if (factSources.Count == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: missing fact-source grading table (depa-map.json factSources) — cannot tell which upstreams are grade<=3; not guessing.");
            }

            var derivedFacts = factSources
                .Where(f => (int)f.Num("grade", 0) is 6 or 7 && f.SymbolId.Length > 0 && symbolById.ContainsKey(f.SymbolId))
                .ToArray();
            if (projections.Count == 0 && derivedFacts.Length == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: missing depa_projection annotations (depa-map.json projections) and no grade-6/7 derived fact sources — no derived nodes to check for back-writes; not guessing.");
            }

            var highFacts = factSources
                .Where(f => (int)f.Num("grade", 0) is >= 1 and <= 3 && f.SymbolId.Length > 0 && symbolById.ContainsKey(f.SymbolId))
                .ToArray();
            var sources = projections
                .Where(p => p.SymbolId.Length > 0 && symbolById.ContainsKey(p.SymbolId))
                .Select(p => (Snap: p, IsProjection: true))
                .Concat(derivedFacts.Select(f => (Snap: f, IsProjection: false)))
                .ToArray();
            var hits = new List<Pending>();
            foreach (var (source, isProjection) in sources)
            {
                var sourceSym = symbolById[source.SymbolId];
                var closure = Closure(source.SymbolId);
                foreach (var fact in highFacts)
                {
                    if (fact.SymbolId == source.SymbolId)
                    {
                        continue; // dual-annotated symbols are V-D3's business, not a back-write.
                    }

                    var factSym = symbolById[fact.SymbolId];
                    var writeEdges = edges
                        .Where(e => e.Kind == "ACCESSES" && e.ToId == fact.SymbolId && closure.Contains(e.FromId)
                            && e.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.Line)
                        .ToArray();
                    if (writeEdges.Length == 0)
                    {
                        continue;
                    }

                    var sourceLabel = isProjection
                        ? $"projection '{sourceSym.Name}'"
                        : $"grade-{(int)source.Num("grade", 0)} fact source '{sourceSym.Name}'";
                    // The backwrites relation is typed depa_projection -> depa_fact_source;
                    // grade-6/7 fact hits carry evidence only, no signal edge.
                    (string From, string Rel, string To, Dictionary<string, object?>? Props)? signal = isProjection
                        ? (source.EntityId, "backwrites", fact.EntityId, null)
                        : null;
                    hits.Add(new Pending(
                        "V-S1", "fact_source", source.EntityId, source.SymbolId, source.SymKey,
                        $"{sourceLabel} writes back into grade-{(int)fact.Num("grade", 0)} fact source '{factSym.Name}'",
                        writeEdges.Select(e => new DepaEvidenceRef("write", PathOfEdge(e, sourceSym), e.Line > 0 ? e.Line : sourceSym.StartLine,
                            $"{sourceSym.Name} writes {factSym.Name}")).ToArray(),
                        Math.Min(source.Confidence, fact.Confidence),
                        fact.SymbolId,
                        signal));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        RuleOutcome DetectF1()
        {
            var configParams = runtimeParams.Where(p => p.Str("role") == "config").ToArray();
            if (configParams.Length == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-F1")} — no parameter is judged as config-role; not guessing.");
            }

            var resolvable = configParams
                .Where(p => p.Str("declared_type_id").Length > 0 && symbolById.ContainsKey(p.Str("declared_type_id")))
                .ToArray();
            if (resolvable.Length == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: no config-role parameter resolves its declared type to an observed symbol (declared_type_id unresolved) — V-F1 cannot inspect the config type's fields; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var param in resolvable)
            {
                var typeSym = symbolById[param.Str("declared_type_id")];
                foreach (var edge in edges.Where(e => e.FromId == typeSym.SymbolId && e.Kind is "HAS_PROPERTY" or "CONTAINS"))
                {
                    if (!symbolById.TryGetValue(edge.ToId, out var field) || field.Kind is not ("field" or "property"))
                    {
                        continue;
                    }

                    var functionObject = field.Signature.Contains("Func<", StringComparison.Ordinal)
                        || field.Signature.Contains("Action<", StringComparison.Ordinal)
                        || field.Signature.Contains("delegate", StringComparison.Ordinal)
                        || field.Name.EndsWith("Callback", StringComparison.Ordinal);
                    if (!functionObject)
                    {
                        continue;
                    }

                    hits.Add(new Pending(
                        "V-F1", "layering", param.EntityId, param.SymbolId, param.SymKey,
                        $"config parameter type '{typeSym.Name}' carries function-object field '{field.Name}' — behaviour does not belong in config",
                        [new DepaEvidenceRef("declaration", field.Path, field.StartLine, $"field {field.Name}: {field.Signature}")],
                        param.Confidence,
                        edge.ToId));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        RuleOutcome DetectF2()
        {
            if (carriers.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-F2")} — no runtime carrier types to check for business logic; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var carrier in carriers.Where(c => c.SymbolId.Length > 0 && symbolById.ContainsKey(c.SymbolId)))
            {
                var carrierSym = symbolById[carrier.SymbolId];
                foreach (var edge in edges.Where(e => e.FromId == carrier.SymbolId && e.Kind is "HAS_METHOD" or "CONTAINS"))
                {
                    if (!symbolById.TryGetValue(edge.ToId, out var method) || method.Kind != "method")
                    {
                        continue;
                    }

                    var accessorOrCtor = method.Name.StartsWith("get_", StringComparison.Ordinal)
                        || method.Name == ".ctor" || method.Name == "ctor" || method.Name == carrierSym.Name;
                    if (accessorOrCtor)
                    {
                        continue;
                    }

                    var outgoingCalls = edges.Count(e => e.Kind == "CALLS" && e.FromId == method.SymbolId);
                    if (outgoingCalls == 0)
                    {
                        continue;
                    }

                    // Pure-delegate facade exemption (track refine-depa-detection-precision
                    // T2.1, catalog B-3 "单表达式委托"): exactly one outgoing CALLS and no write
                    // ACCESSES is a compliant forwarding shape, not business logic — no GAP is
                    // materialized. Approximation and its weakening (read ACCESSES are not
                    // distinguished at ck_* granularity) are recorded in rubrics/rule-map.md
                    // V-F2 row; exempted methods are skipped silently (verdict stays an enum,
                    // decision in the track's decisions.md).
                    var writesState = edges.Any(e => e.Kind == "ACCESSES" && e.FromId == method.SymbolId
                        && e.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase));
                    if (outgoingCalls == 1 && !writesState)
                    {
                        continue;
                    }

                    hits.Add(new Pending(
                        "V-F2", "layering", carrier.EntityId, carrier.SymbolId, carrier.SymKey,
                        $"runtime carrier '{carrierSym.Name}' hosts business method '{method.Name}' (carrier types must stay data-only)",
                        [new DepaEvidenceRef("declaration", method.Path, method.StartLine, $"method {method.Name} has {outgoingCalls} outgoing CALLS")],
                        Math.Min(carrier.Confidence, MaxHeuristicViolationConfidence),
                        edge.ToId));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        RuleOutcome DetectL1()
        {
            if (capsules.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-L1")} — no capsule boundaries to enforce; not guessing.");
            }

            var crossings = new Dictionary<(string FromCapsule, string ToCapsule), List<CkEdge>>();
            foreach (var edge in edges.Where(e => e.Kind is "IMPORTS" or "CALLS" or "ACCESSES"))
            {
                if (!symbolById.TryGetValue(edge.FromId, out var fromSym) || !symbolById.TryGetValue(edge.ToId, out var toSym))
                {
                    continue;
                }

                var fromCapsule = CapsuleOf(fromSym.Path);
                if (fromCapsule is null)
                {
                    continue;
                }

                var toCapsule = CapsuleOf(toSym.Path);
                if (toCapsule is null || toCapsule.EntityId == fromCapsule.EntityId
                    || !PathGlobMatch(toCapsule.InternalsGlob, toSym.Path))
                {
                    continue;
                }

                var key = (fromCapsule.EntityId, toCapsule.EntityId);
                (crossings.TryGetValue(key, out var list) ? list : crossings[key] = []).Add(edge);
            }

            var capsuleById = capsules.ToDictionary(c => c.EntityId, StringComparer.Ordinal);
            var hits = new List<Pending>();
            foreach (var ((fromCapsuleId, toCapsuleId), crossingEdges) in crossings.OrderBy(p => p.Key.FromCapsule, StringComparer.Ordinal).ThenBy(p => p.Key.ToCapsule, StringComparer.Ordinal))
            {
                var fromCapsule = capsuleById[fromCapsuleId];
                var toCapsule = capsuleById[toCapsuleId];
                var evidence = crossingEdges
                    .OrderBy(e => e.FileId, StringComparer.Ordinal).ThenBy(e => e.Line)
                    .Select(e =>
                    {
                        var fromSym = symbolById[e.FromId];
                        var toSym = symbolById[e.ToId];
                        return new DepaEvidenceRef("boundary_edge", PathOfEdge(e, fromSym), e.Line > 0 ? e.Line : fromSym.StartLine,
                            $"{fromSym.Name} -{e.Kind}-> {toSym.Name} ({toSym.Path})");
                    })
                    .ToArray();
                hits.Add(new Pending(
                    "V-L1", "layering", fromCapsuleId, "", "",
                    $"capsule '{fromCapsule.Name}' reaches into '{toCapsule.Name}' internals ({evidence.Length} boundary-violating edge(s))",
                    evidence, 1.0,
                    toCapsuleId + "|" + string.Join("|", crossingEdges.Select(e => $"{e.FromId}->{e.ToId}@{e.Kind}").OrderBy(x => x, StringComparer.Ordinal))));
            }

            return RuleOutcome.Ran(hits);
        }

        RuleOutcome DetectL3()
        {
            if (contracts.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-L3")} — no contracts whose dependency direction could be checked; not guessing.");
            }

            var contractByPath = new Dictionary<string, Snap>(StringComparer.Ordinal);
            foreach (var contract in contracts.Where(c => c.SymbolId.Length > 0 && symbolById.ContainsKey(c.SymbolId)))
            {
                contractByPath.TryAdd(symbolById[contract.SymbolId].Path, contract);
            }

            var implPaths = impls
                .Where(i => i.SymbolId.Length > 0 && symbolById.ContainsKey(i.SymbolId))
                .Select(i => symbolById[i.SymbolId].Path)
                .ToHashSet(StringComparer.Ordinal);
            var hits = new List<Pending>();
            foreach (var edge in edges.Where(e => e.Kind == "IMPORTS"))
            {
                if (!symbolById.TryGetValue(edge.FromId, out var fromSym) || !symbolById.TryGetValue(edge.ToId, out var toSym)
                    || !contractByPath.TryGetValue(fromSym.Path, out var contract)
                    || !implPaths.Contains(toSym.Path) || fromSym.Path == toSym.Path)
                {
                    continue;
                }

                hits.Add(new Pending(
                    "V-L3", "layering", contract.EntityId, contract.SymbolId, contract.SymKey,
                    $"contract file '{fromSym.Path}' imports implementation file '{toSym.Path}' — the dependency must point the other way",
                    [new DepaEvidenceRef("import", PathOfEdge(edge, fromSym), edge.Line > 0 ? edge.Line : fromSym.StartLine,
                        $"{fromSym.Name} IMPORTS {toSym.Name} ({toSym.Path})")],
                    contract.Confidence,
                    $"{edge.FromId}->{edge.ToId}"));
            }

            return RuleOutcome.Ran(hits);
        }

        // ---- batch-1 detectors (track expand-depa-detection-rules T2.1) ----

        // V-E3 (catalog B2): a contract member with an outgoing CALLS edge hosts orchestration
        // — contracts must stay declaration-only. Weakened: only observable CALLS count;
        // call-free assembly expressions are invisible at the symbol level (rule-map.md).
        RuleOutcome DetectE3()
        {
            if (contracts.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-E3")} — no contracts whose members could host orchestration; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var contract in contracts.Where(c => c.SymbolId.Length > 0 && symbolById.ContainsKey(c.SymbolId)))
            {
                var contractSym = symbolById[contract.SymbolId];
                foreach (var member in Closure(contract.SymbolId))
                {
                    var memberSym = symbolById.GetValueOrDefault(member) ?? contractSym;
                    foreach (var edge in edges.Where(e => e.Kind == "CALLS" && e.FromId == member))
                    {
                        var targetName = symbolById.TryGetValue(edge.ToId, out var target) ? target.Name : edge.ToId;
                        hits.Add(new Pending(
                            "V-E3", "effect", contract.EntityId, contract.SymbolId, contract.SymKey,
                            $"contract '{contractSym.Name}' hosts orchestration: member '{memberSym.Name}' calls '{targetName}' (contracts declare effects, factories/bootstrap assemble them)",
                            [new DepaEvidenceRef("call", PathOfEdge(edge, memberSym), edge.Line > 0 ? edge.Line : memberSym.StartLine,
                                $"{memberSym.Name} CALLS {targetName}")],
                            contract.Confidence,
                            $"{edge.FromId}->{edge.ToId}"));
                    }
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-D3 (catalog A4): projection/fact entanglement — the same symbol judged BOTH as a
        // projection and as a grade<=3 fact source, or a projection written from outside its
        // own closure (treated as a source by someone else).
        RuleOutcome DetectD3()
        {
            if (projections.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-D3")} — no projections whose fact entanglement could be checked; not guessing.");
            }

            var gradedBySymbol = factSources
                .Where(f => (int)f.Num("grade", 0) is >= 1 and <= 3 && f.SymbolId.Length > 0)
                .GroupBy(f => f.SymbolId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var hits = new List<Pending>();
            foreach (var projection in projections.Where(p => p.SymbolId.Length > 0 && symbolById.ContainsKey(p.SymbolId)))
            {
                var projSym = symbolById[projection.SymbolId];
                if (gradedBySymbol.TryGetValue(projection.SymbolId, out var fact))
                {
                    hits.Add(new Pending(
                        "V-D3", "data", projection.EntityId, projection.SymbolId, projection.SymKey,
                        $"'{projSym.Name}' is annotated as a projection AND as a grade-{(int)fact.Num("grade", 0)} fact source — read model and fact are entangled",
                        [new DepaEvidenceRef("declaration", projSym.Path, projSym.StartLine,
                            $"{projSym.Name} carries both depa_projection and depa_fact_source judgements")],
                        Math.Min(projection.Confidence, fact.Confidence),
                        $"dual:{fact.EntityId}"));
                }

                var closure = Closure(projection.SymbolId);
                foreach (var edge in edges.Where(e => e.Kind == "ACCESSES" && e.ToId == projection.SymbolId
                             && e.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase)
                             && !closure.Contains(e.FromId)))
                {
                    if (!symbolById.TryGetValue(edge.FromId, out var writerSym))
                    {
                        continue;
                    }

                    hits.Add(new Pending(
                        "V-D3", "data", projection.EntityId, projection.SymbolId, projection.SymKey,
                        $"projection '{projSym.Name}' is written by external symbol '{writerSym.Name}' — outsiders treat the read model as a fact source",
                        [new DepaEvidenceRef("write", PathOfEdge(edge, writerSym), edge.Line > 0 ? edge.Line : writerSym.StartLine,
                            $"{writerSym.Name} writes projection {projSym.Name}")],
                        projection.Confidence,
                        $"write:{edge.FromId}"));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-S3 (catalog E6): a grade-5 checkpoint type carries a field whose declared type is
        // an input-role payload type — snapshots must hold grade 1-3 state only. Weakened:
        // field-type membership approximates "carries the payload" (rule-map.md).
        RuleOutcome DetectS3()
        {
            if (factSources.Count == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: missing fact-source grading table (depa-map.json factSources) — no grade-5 snapshots to inspect; not guessing.");
            }

            var inputParams = runtimeParams.Where(p => p.Str("role") == "input").ToArray();
            if (inputParams.Length == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: missing input-role depa_runtime_param annotations (depa-map.json runtimeParams) — cannot tell payload types from state types; not guessing.");
            }

            var inputTypeNames = inputParams
                .Select(p =>
                {
                    var declared = p.Str("declared_type");
                    if (declared.Length > 0)
                    {
                        return NormalizeType(declared);
                    }

                    var declaredId = p.Str("declared_type_id");
                    return declaredId.Length > 0 && symbolById.TryGetValue(declaredId, out var t) ? NormalizeType(t.Name) : "";
                })
                .Where(n => n.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (inputTypeNames.Count == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: input-role depa_runtime_param annotations carry no declared type (depa-map.json runtimeParams[].declaredType) — cannot tell payload types from state types; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var fact in factSources.Where(f => (int)f.Num("grade", 0) == 5 && f.SymbolId.Length > 0 && symbolById.ContainsKey(f.SymbolId)))
            {
                var factSym = symbolById[fact.SymbolId];
                foreach (var field in FieldsOf(fact.SymbolId))
                {
                    var fieldType = NormalizeType(DeclaredTypeTokenOf(field));
                    if (fieldType.Length == 0 || !inputTypeNames.Contains(fieldType))
                    {
                        continue;
                    }

                    hits.Add(new Pending(
                        "V-S3", "fact_source", fact.EntityId, fact.SymbolId, fact.SymKey,
                        $"grade-5 snapshot '{factSym.Name}' carries input-typed field '{field.Name}' ({fieldType}) — checkpoints hold grade 1-3 state, not per-call payloads",
                        [new DepaEvidenceRef("declaration", field.Path, field.StartLine, $"field {field.Name}: {field.Signature}")],
                        Math.Min(fact.Confidence, MaxHeuristicViolationConfidence),
                        field.SymbolId));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-S4 (catalog E4, phenomenon-level): a core probes file existence/mtime — run state
        // must come from the grade-3 control plane, not persistence metadata. Confidence is
        // capped and the evidence is marked heuristic (rule-map.md implemented-weakened).
        RuleOutcome DetectS4()
        {
            if (cores.Length == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-S4")} — cannot tell core logic from edge adapters; not guessing.");
            }

            if (externalCalls is null)
            {
                return RuleOutcome.Blocked("BLOCKED: ck_external_call observation table is absent — out-of-repo calls were not indexed, V-S4 cannot see file-metadata probes; not guessing.");
            }

            var callsByCaller = externalCalls.ToLookup(c => c.CallerId, StringComparer.Ordinal);
            var hits = new List<Pending>();
            foreach (var core in cores)
            {
                var coreSym = symbolById[core.SymbolId];
                foreach (var member in Closure(core.SymbolId))
                {
                    foreach (var call in callsByCaller[member].Where(c => IsFileMetadataProbe(c.TargetKey)))
                    {
                        var memberSym = symbolById.GetValueOrDefault(member) ?? coreSym;
                        var path = filePaths.TryGetValue(call.FirstFileId, out var p) && p.Length > 0 ? p : memberSym.Path;
                        var line = call.FirstLine > 0 ? call.FirstLine : memberSym.StartLine;
                        if (path.Length == 0 || line <= 0)
                        {
                            continue;
                        }

                        hits.Add(new Pending(
                            "V-S4", "fact_source", core.EntityId, core.SymbolId, core.SymKey,
                            $"core '{coreSym.Name}' probes file metadata for run state: '{memberSym.Name}' calls {call.TargetKey} — run state belongs to the grade-3 control plane",
                            [new DepaEvidenceRef("external_call", path, line,
                                $"heuristic phenomenon: {memberSym.Name} CALLS {call.TargetKey} (count={call.Count})")],
                            Math.Min(core.Confidence, PhenomenonConfidenceCeiling),
                            $"{member}->{call.TargetKey}"));
                    }
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // ---- batch-2 detectors (track expand-depa-detection-rules T3.1) ----

        // V-S2a (catalog E1 / fact-source-truth 规则④) and V-S2b (catalog E2 / 规则③): a
        // grade-5 checkpoint / grade-4 journal read outside every declared recovery path.
        // Phenomenon-level: the read is observed, "the read drives live control flow" is not —
        // confidence capped at 0.7, evidence marked heuristic (rule-map.md
        // implemented-weakened). Projection readers are exempt (journal/checkpoint →
        // projection is the normal derivation chain), as is the fact's own CONTAINS closure.
        RuleOutcome DetectS2(string ruleId, int grade, string gradeWord, string ruleRef)
        {
            if (map.RecoveryPaths.Count == 0)
            {
                return RuleOutcome.Blocked(
                    $"BLOCKED: missing recoveryPaths[] declaration (depa-map.json recoveryPaths) — "
                    + $"cannot tell recovery/startup readers of a {gradeWord} from live-path readers; not guessing.");
            }

            var gradedFacts = factSources
                .Where(f => (int)f.Num("grade", 0) == grade && f.SymbolId.Length > 0 && symbolById.ContainsKey(f.SymbolId))
                .ToArray();
            if (gradedFacts.Length == 0)
            {
                return RuleOutcome.Blocked(
                    $"BLOCKED: no grade-{grade} {gradeWord} fact source is declared and anchored "
                    + $"(depa-map.json factSources grade={grade}) — nothing whose live-path reads could be checked; not guessing.");
            }

            var projectionMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var projection in projections.Where(p => p.SymbolId.Length > 0 && symbolById.ContainsKey(p.SymbolId)))
            {
                projectionMembers.UnionWith(Closure(projection.SymbolId));
            }

            var hits = new List<Pending>();
            foreach (var fact in gradedFacts)
            {
                var factSym = symbolById[fact.SymbolId];
                var ownClosure = Closure(fact.SymbolId);
                foreach (var edge in edges
                             .Where(e => e.Kind == "ACCESSES" && e.ToId == fact.SymbolId
                                 && !e.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.FileId, StringComparer.Ordinal).ThenBy(e => e.Line))
                {
                    if (ownClosure.Contains(edge.FromId) || projectionMembers.Contains(edge.FromId)
                        || !symbolById.TryGetValue(edge.FromId, out var readerSym)
                        || map.RecoveryPaths.Any(glob => PathGlobMatch(glob, readerSym.Path)))
                    {
                        continue;
                    }

                    var path = PathOfEdge(edge, readerSym);
                    var line = edge.Line > 0 ? edge.Line : readerSym.StartLine;
                    if (path.Length == 0 || line <= 0)
                    {
                        continue; // no locatable evidence — design §5.2 rule ①.
                    }

                    hits.Add(new Pending(
                        ruleId, "fact_source", fact.EntityId, fact.SymbolId, fact.SymKey,
                        $"grade-{grade} {gradeWord} '{factSym.Name}' is read on the live path: '{readerSym.Name}' ({readerSym.Path}) sits outside every declared recovery path — {ruleRef}",
                        [new DepaEvidenceRef("read", path, line,
                            $"heuristic phenomenon: {readerSym.Name} reads {factSym.Name} outside recoveryPaths")],
                        Math.Min(fact.Confidence, SignalConfidenceCeiling),
                        $"{edge.FromId}->{fact.SymbolId}"));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-P2 (catalog C4): a lower-layer symbol reaching up into a higher layer — reusable
        // components must stay unaware of their calling domain. Layers come from depa-map
        // layers[] declared low→high; without the declaration the rule is BLOCKED, never
        // guessed. IMPORTS targets that degrade to hashed import:* nodes are not resolvable
        // (same limit as capsule_depends_on), so CALLS/ACCESSES carry the detection.
        RuleOutcome DetectP2()
        {
            if (map.Layers.Count == 0)
            {
                return RuleOutcome.Blocked(
                    "BLOCKED: missing layers[] declaration (depa-map.json layers, declared low→high) — "
                    + "no layer boundaries whose awareness direction could be checked; not guessing.");
            }

            if (capsules.Count == 0)
            {
                return RuleOutcome.Blocked(
                    "BLOCKED: missing capsule declarations (depa-map.json capsules) — the layer-awareness "
                    + "hit needs a subject entity to attach to; not guessing.");
            }

            int LayerOf(string path)
            {
                for (var i = 0; i < map.Layers.Count; i++)
                {
                    if (map.Layers[i].PathGlobs.Any(glob => PathGlobMatch(glob, path)))
                    {
                        return i;
                    }
                }

                return -1;
            }

            var hits = new List<Pending>();
            foreach (var edge in edges
                         .Where(e => e.Kind is "IMPORTS" or "CALLS" or "ACCESSES")
                         .OrderBy(e => e.FileId, StringComparer.Ordinal).ThenBy(e => e.Line))
            {
                if (!symbolById.TryGetValue(edge.FromId, out var fromSym) || !symbolById.TryGetValue(edge.ToId, out var toSym))
                {
                    continue;
                }

                var fromLayer = LayerOf(fromSym.Path);
                var toLayer = LayerOf(toSym.Path);
                if (fromLayer < 0 || toLayer < 0 || fromLayer >= toLayer)
                {
                    continue; // unlayered paths, same layer, or the compliant high→low direction.
                }

                var subjectEntityId = implBySymbolId.GetValueOrDefault(edge.FromId)?.EntityId
                    ?? CapsuleOf(fromSym.Path)?.EntityId ?? "";
                if (subjectEntityId.Length == 0)
                {
                    continue; // no depa entity to attach the hit to (outside every capsule).
                }

                var path = PathOfEdge(edge, fromSym);
                var line = edge.Line > 0 ? edge.Line : fromSym.StartLine;
                if (path.Length == 0 || line <= 0)
                {
                    continue;
                }

                // The violation carries the observation edge's own confidence: a name-only
                // "ambiguous" cross-file CALLS resolution (0.5) must not surface as a
                // config-grade (1.0) layer violation — T3.1 rescan adjudication (the
                // JsonElement.GetString misbinding class of false positives stays visible,
                // honestly weighted, instead of being cleared).
                var lowFidelity = edge.Confidence < 1.0;
                hits.Add(new Pending(
                    "V-P2", "processor", subjectEntityId, fromSym.SymbolId, fromSym.SymKey,
                    $"lower layer '{map.Layers[fromLayer].Name}' symbol '{fromSym.Name}' reaches up into layer '{map.Layers[toLayer].Name}' symbol '{toSym.Name}' — reusable components must stay unaware of their calling domain (catalog C4)",
                    [new DepaEvidenceRef("layer_edge", path, line,
                        (lowFidelity ? $"heuristic phenomenon (low-confidence call resolution, {edge.Evidence}): " : "")
                        + $"{fromSym.Name} -{edge.Kind}-> {toSym.Name} ({toSym.Path})")],
                    Math.Min(1.0, edge.Confidence),
                    $"{edge.FromId}->{edge.ToId}@{edge.Kind}"));
            }

            return RuleOutcome.Ran(hits);
        }

        // V-F3 (catalog F3): the same field (name + declared type) lives in a config-role type
        // AND a runtime-side type — one dependency, two homes, guaranteed drift.
        RuleOutcome DetectF3()
        {
            var configParams = runtimeParams.Where(p => p.Str("role") == "config").ToArray();
            if (configParams.Length == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: missing depa_runtime_param role=config annotations (depa-map.json runtimeParams) — no config types to compare; not guessing.");
            }

            var resolvableConfig = configParams
                .Where(p => p.Str("declared_type_id").Length > 0 && symbolById.ContainsKey(p.Str("declared_type_id")))
                .ToArray();
            if (resolvableConfig.Length == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: no config-role parameter resolves its declared type to an observed symbol — V-F3 cannot inspect config fields; not guessing.");
            }

            var runtimeSide = carriers
                .Where(c => c.SymbolId.Length > 0 && symbolById.ContainsKey(c.SymbolId))
                .Select(c => (TypeSym: symbolById[c.SymbolId], c.Confidence))
                .Concat(runtimeParams
                    .Where(p => p.Str("role") == "runtime" && p.Str("declared_type_id").Length > 0 && symbolById.ContainsKey(p.Str("declared_type_id")))
                    .Select(p => (TypeSym: symbolById[p.Str("declared_type_id")], p.Confidence)))
                .ToArray();
            if (runtimeSide.Length == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: missing runtime-side types (depa-map.json runtimeCarrierTypes / runtime-role runtimeParams with declaredType) — nothing to compare config fields against; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var param in resolvableConfig)
            {
                var cfgType = symbolById[param.Str("declared_type_id")];
                foreach (var cfgField in FieldsOf(cfgType.SymbolId))
                {
                    var cfgFieldType = NormalizeType(DeclaredTypeTokenOf(cfgField));
                    if (cfgFieldType.Length == 0)
                    {
                        continue;
                    }

                    foreach (var (rtType, rtConfidence) in runtimeSide)
                    {
                        foreach (var rtField in FieldsOf(rtType.SymbolId))
                        {
                            if (rtField.Name != cfgField.Name || NormalizeType(DeclaredTypeTokenOf(rtField)) != cfgFieldType)
                            {
                                continue;
                            }

                            hits.Add(new Pending(
                                "V-F3", "layering", param.EntityId, param.SymbolId, param.SymKey,
                                $"config type '{cfgType.Name}' duplicates runtime field '{cfgField.Name}' ({cfgFieldType}) of '{rtType.Name}' — one dependency needs one home",
                                [
                                    new DepaEvidenceRef("declaration", cfgField.Path, cfgField.StartLine, $"config field {cfgField.Name}: {cfgField.Signature}"),
                                    new DepaEvidenceRef("declaration", rtField.Path, rtField.StartLine, $"runtime field {rtField.Name}: {rtField.Signature}"),
                                ],
                                Math.Min(param.Confidence, rtConfidence),
                                $"{cfgField.SymbolId}|{rtField.SymbolId}"));
                        }
                    }
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-L2 (catalog D5 / capsule-protocol CP2§5): capsule dependency cycle — cross-capsule
        // CALLS/ACCESSES edges whose capsule graph contains a strongly-connected component.
        RuleOutcome DetectL2()
        {
            if (capsules.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-L2")} — no capsule boundaries whose dependency direction could be checked; not guessing.");
            }

            var firstCrossing = new Dictionary<(string From, string To), CkEdge>();
            foreach (var edge in edges
                         .Where(e => e.Kind is "CALLS" or "ACCESSES")
                         .OrderBy(e => e.FileId, StringComparer.Ordinal).ThenBy(e => e.Line))
            {
                if (!symbolById.TryGetValue(edge.FromId, out var fromSym) || !symbolById.TryGetValue(edge.ToId, out var toSym))
                {
                    continue;
                }

                var fromCapsule = CapsuleOf(fromSym.Path);
                var toCapsule = CapsuleOf(toSym.Path);
                if (fromCapsule is null || toCapsule is null || fromCapsule.EntityId == toCapsule.EntityId)
                {
                    continue;
                }

                firstCrossing.TryAdd((fromCapsule.EntityId, toCapsule.EntityId), edge);
            }

            var adjacency = firstCrossing.Keys
                .GroupBy(k => k.From, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(k => k.To).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

            HashSet<string> ReachableFrom(string start)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>(adjacency.GetValueOrDefault(start) ?? []);
                while (queue.Count > 0)
                {
                    var next = queue.Dequeue();
                    if (seen.Add(next))
                    {
                        foreach (var onward in adjacency.GetValueOrDefault(next) ?? [])
                        {
                            queue.Enqueue(onward);
                        }
                    }
                }

                return seen;
            }

            var capsuleById = capsules.ToDictionary(c => c.EntityId, StringComparer.Ordinal);
            var reachable = adjacency.Keys.ToDictionary(id => id, ReachableFrom, StringComparer.Ordinal);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var hits = new List<Pending>();
            foreach (var node in adjacency.Keys.OrderBy(id => id, StringComparer.Ordinal))
            {
                if (reported.Contains(node) || !reachable[node].Contains(node))
                {
                    continue; // not on a cycle.
                }

                var component = reachable[node]
                    .Where(other => reachable.TryGetValue(other, out var back) && back.Contains(node))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                reported.UnionWith(component);
                var evidence = new List<DepaEvidenceRef>();
                foreach (var from in component)
                {
                    foreach (var to in component.Where(to => to != from))
                    {
                        if (!firstCrossing.TryGetValue((from, to), out var edge))
                        {
                            continue;
                        }

                        var fromSym = symbolById[edge.FromId];
                        var toSym = symbolById[edge.ToId];
                        evidence.Add(new DepaEvidenceRef("boundary_edge", PathOfEdge(edge, fromSym), edge.Line > 0 ? edge.Line : fromSym.StartLine,
                            $"{capsuleById[from].Name} -> {capsuleById[to].Name}: {fromSym.Name} -{edge.Kind}-> {toSym.Name}"));
                    }
                }

                if (evidence.Count == 0)
                {
                    continue;
                }

                var names = string.Join(" <-> ", component.Select(id => capsuleById[id].Name));
                hits.Add(new Pending(
                    "V-L2", "layering", component[0], "", "",
                    $"capsule dependency cycle: {names} — dependencies must stay one-way (break the cycle or actor-ize one node)",
                    evidence, 1.0,
                    string.Join("|", component)));
            }

            return RuleOutcome.Ran(hits);
        }

        // V-L4 (capsule-protocol CP2§6): a capsule exposing more than one entry — core_logic
        // is the single stable entry of a capsule.
        RuleOutcome DetectL4()
        {
            if (capsules.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-L4")} — no capsules whose entry fan-out could be counted; not guessing.");
            }

            var entryById = entrySnaps.ToDictionary(e => e.EntityId, StringComparer.Ordinal);
            var capsuleById = capsules.ToDictionary(c => c.EntityId, StringComparer.Ordinal);
            var hits = new List<Pending>();
            foreach (var group in exposesEdges
                         .Where(e => capsuleById.ContainsKey(e.From))
                         .GroupBy(e => e.From, StringComparer.Ordinal)
                         .Where(g => g.Select(e => e.To).Distinct(StringComparer.Ordinal).Count() > 1)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var entryIds = group.Select(e => e.To).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                var evidence = entryIds
                    .Select(id => entryById.GetValueOrDefault(id))
                    .Where(snap => snap is not null && snap.Str("path").Length > 0 && (int)snap.Num("line", 0) > 0)
                    .Select(snap => new DepaEvidenceRef("entry", snap!.Str("path"), (int)snap.Num("line", 0),
                        $"exposed entry {(symbolById.TryGetValue(snap.SymbolId, out var s) ? s.Name : snap.SymKey)} ({snap.Str("entry_kind")})"))
                    .ToArray();
                if (evidence.Length == 0)
                {
                    continue; // no locatable entry evidence — design §5.2 rule ①.
                }

                var capsule = capsuleById[group.Key];
                var entryNames = entryIds
                    .Select(id => entryById.GetValueOrDefault(id))
                    .Select(snap => snap is not null && symbolById.TryGetValue(snap.SymbolId, out var s) ? s.Name : "?")
                    .ToArray();
                hits.Add(new Pending(
                    "V-L4", "layering", capsule.EntityId, "", "",
                    $"capsule '{capsule.Name}' exposes {entryIds.Length} entries (a capsule has one stable entry): {string.Join(", ", entryNames)}",
                    evidence, 1.0,
                    string.Join("|", entryIds)));
            }

            return RuleOutcome.Ran(hits);
        }

        // V-L5 (catalog F6 half): a contract member's signature references another capsule's
        // internal type. Weakened: textual word match over ck signatures, not semantic type
        // resolution (rule-map.md).
        RuleOutcome DetectL5()
        {
            if (contracts.Count == 0 || capsules.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-L5")} — cannot join contract signatures against internals globs; not guessing.");
            }

            var internalTypes = symbols
                .Where(s => IsTypeKind(s.Kind))
                .Select(s => (Sym: s, Capsule: CapsuleOf(s.Path)))
                .Where(t => t.Capsule is not null && PathGlobMatch(t.Capsule.InternalsGlob, t.Sym.Path))
                .ToArray();
            var hits = new List<Pending>();
            foreach (var contract in contracts.Where(c => c.SymbolId.Length > 0 && symbolById.ContainsKey(c.SymbolId)))
            {
                var contractSym = symbolById[contract.SymbolId];
                var contractCapsule = CapsuleOf(contractSym.Path);
                foreach (var member in Closure(contract.SymbolId))
                {
                    var memberSym = symbolById.GetValueOrDefault(member);
                    if (memberSym is null || memberSym.Signature.Length == 0)
                    {
                        continue;
                    }

                    foreach (var (internalSym, internalCapsule) in internalTypes)
                    {
                        if (internalCapsule!.EntityId == contractCapsule?.EntityId
                            || !ContainsWord(memberSym.Signature, internalSym.Name))
                        {
                            continue;
                        }

                        hits.Add(new Pending(
                            "V-L5", "layering", contract.EntityId, contract.SymbolId, contract.SymKey,
                            $"contract '{contractSym.Name}' member '{memberSym.Name}' references '{internalSym.Name}' — an internal type of capsule '{internalCapsule.Name}' must not surface in a contract signature",
                            [new DepaEvidenceRef("signature", memberSym.Path, memberSym.StartLine,
                                $"{memberSym.Name}: {memberSym.Signature} references {internalSym.Name} ({internalSym.Path})")],
                            Math.Min(contract.Confidence, MaxHeuristicViolationConfidence),
                            $"{member}|{internalSym.SymbolId}"));
                    }
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-L6 (catalog F7 half): the same contract type name defined both in the contract
        // package and inside another capsule — the contract boundary is not settled. Weakened:
        // same-name matching, structural identity is not compared (rule-map.md).
        RuleOutcome DetectL6()
        {
            if (contracts.Count == 0 || capsules.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-L6")} — cannot look for duplicate contract definitions across capsules; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var contract in contracts.Where(c => c.SymbolId.Length > 0 && symbolById.ContainsKey(c.SymbolId)))
            {
                var contractSym = symbolById[contract.SymbolId];
                var contractCapsule = CapsuleOf(contractSym.Path);
                foreach (var other in symbols
                             .Where(s => s.Name == contractSym.Name && s.SymbolId != contract.SymbolId
                                 && IsTypeKind(s.Kind) && s.Path != contractSym.Path)
                             .OrderBy(s => s.SymbolId, StringComparer.Ordinal))
                {
                    var otherCapsule = CapsuleOf(other.Path);
                    if (otherCapsule is null || otherCapsule.EntityId == contractCapsule?.EntityId)
                    {
                        continue;
                    }

                    hits.Add(new Pending(
                        "V-L6", "layering", contract.EntityId, contract.SymbolId, contract.SymKey,
                        $"contract type '{contractSym.Name}' is defined twice: in the contract package ({contractSym.Path}) and in capsule '{otherCapsule.Name}' ({other.Path}) — one contract, one definition",
                        [
                            new DepaEvidenceRef("declaration", contractSym.Path, contractSym.StartLine, $"contract definition of {contractSym.Name}"),
                            new DepaEvidenceRef("declaration", other.Path, other.StartLine, $"duplicate definition of {other.Name} in {otherCapsule.Name}"),
                        ],
                        contract.Confidence,
                        other.SymbolId));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-R1 (runtime-explicitness "runtime = Any"): a runtime-role parameter or carrier
        // field declared as an unstructured big bag (Dictionary<string,object>/object/dynamic
        // lexicon). Phenomenon-level word list — confidence capped, evidence marked heuristic.
        RuleOutcome DetectR1()
        {
            var runtimeRoleParams = runtimeParams.Where(p => p.Str("role") == "runtime").ToArray();
            var anchoredCarriers = carriers.Where(c => c.SymbolId.Length > 0 && symbolById.ContainsKey(c.SymbolId)).ToArray();
            if (runtimeRoleParams.Length == 0 && anchoredCarriers.Length == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-R1")} — no runtime-side declarations whose structure could be judged; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var param in runtimeRoleParams)
            {
                var declared = NormalizeType(param.Str("declared_type"));
                if (declared.Length == 0 && param.Str("declared_type_id").Length > 0
                    && symbolById.TryGetValue(param.Str("declared_type_id"), out var declaredSym))
                {
                    declared = NormalizeType(declaredSym.Name);
                }

                var path = param.Str("path");
                var line = (int)param.Num("line", 0);
                if (!IsBagType(declared) || path.Length == 0 || line <= 0)
                {
                    continue;
                }

                hits.Add(new Pending(
                    "V-R1", "layering", param.EntityId, param.SymbolId, param.SymKey,
                    $"runtime-role parameter is declared as '{param.Str("declared_type")}' — an unstructured big bag defeats runtime explicitness (roles become unauditable)",
                    [new DepaEvidenceRef("declaration", path, line,
                        $"heuristic phenomenon (big-bag lexicon): runtime param declared as {param.Str("declared_type")}")],
                    Math.Min(param.Confidence, SignalConfidenceCeiling),
                    $"param:{param.EntityId}"));
            }

            foreach (var carrier in anchoredCarriers)
            {
                var carrierSym = symbolById[carrier.SymbolId];
                foreach (var field in FieldsOf(carrier.SymbolId))
                {
                    var fieldType = NormalizeType(DeclaredTypeTokenOf(field));
                    if (!IsBagType(fieldType))
                    {
                        continue;
                    }

                    hits.Add(new Pending(
                        "V-R1", "layering", carrier.EntityId, carrier.SymbolId, carrier.SymKey,
                        $"runtime carrier '{carrierSym.Name}' hosts big-bag field '{field.Name}' ({fieldType}) — runtime fields must be structured and role-addressable",
                        [new DepaEvidenceRef("declaration", field.Path, field.StartLine,
                            $"heuristic phenomenon (big-bag lexicon): field {field.Name}: {field.Signature}")],
                        Math.Min(carrier.Confidence, SignalConfidenceCeiling),
                        field.SymbolId));
                }
            }

            return RuleOutcome.Ran(hits);
        }

        // V-C1 (capsule-protocol CP1§5): fn(runtime, input, config) role coverage of a core.
        // Weakened: only cores with at least one role-annotated parameter are judged; cores
        // whose parameters are unobserved/unannotated cannot be told apart from non-DOP cores
        // (rule-map.md).
        RuleOutcome DetectC1()
        {
            if (cores.Length == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: missing depa_impl purity=core judgements (depa-map.json cores) — no cores whose DOP form could be checked; not guessing.");
            }

            var paramsBySymbol = runtimeParams
                .Where(p => p.SymbolId.Length > 0 && p.Str("role").Length > 0)
                .GroupBy(p => p.SymbolId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            if (paramsBySymbol.Count == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: missing depa_runtime_param role annotations (depa-map.json runtimeParams) — cannot judge fn(runtime,input,config) coverage; not guessing.");
            }

            var judged = 0;
            var hits = new List<Pending>();
            foreach (var core in cores)
            {
                var coreSym = symbolById[core.SymbolId];
                var roles = Closure(core.SymbolId)
                    .Select(member => paramsBySymbol.GetValueOrDefault(member))
                    .Where(p => p is not null)
                    .Select(p => p!.Str("role"))
                    .ToHashSet(StringComparer.Ordinal);
                if (roles.Count == 0)
                {
                    continue; // no role-annotated parameter observed under this core — cannot judge it.
                }

                judged++;
                var missing = RequiredParamRoles.Where(role => !roles.Contains(role)).ToArray();
                if (missing.Length == 0)
                {
                    continue;
                }

                hits.Add(new Pending(
                    "V-C1", "processor", core.EntityId, core.SymbolId, core.SymKey,
                    $"core '{coreSym.Name}' does not cover fn(runtime, input, config): observed roles [{string.Join(", ", roles.OrderBy(r => r, StringComparer.Ordinal))}], missing [{string.Join(", ", missing)}]",
                    [new DepaEvidenceRef("declaration", coreSym.Path, coreSym.StartLine,
                        $"role-annotated parameters of {coreSym.Name} cover [{string.Join(", ", roles.OrderBy(r => r, StringComparer.Ordinal))}] only")],
                    core.Confidence,
                    string.Join("|", missing)));
            }

            if (judged == 0)
            {
                return RuleOutcome.Blocked("BLOCKED: no role-annotated parameter symbol is observed under any core (ck parameter symbols or depa-map.json runtimeParams anchors missing) — V-C1 cannot judge DOP coverage; not guessing.");
            }

            return RuleOutcome.Ran(hits);
        }

        // V-G1 (catalog G2): a non-contract abstraction with exactly one implementing type —
        // single-implementation polymorphism signal. Effect contracts are exempt: the single
        // contract impl is DEPA's own mandated pattern, not overdesign (rule-map.md).
        RuleOutcome DetectG1()
        {
            if (capsules.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-G1")} — the signal needs a capsule subject to attach to; not guessing.");
            }

            var implementorsByTarget = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            void AddImplementor(string targetSymbolId, string fromId)
            {
                (implementorsByTarget.TryGetValue(targetSymbolId, out var set)
                    ? set
                    : implementorsByTarget[targetSymbolId] = new HashSet<string>(StringComparer.Ordinal)).Add(fromId);
            }

            var abstractions = symbols
                .Where(s => s.Kind == "interface" || s.Signature.Contains("abstract", StringComparison.Ordinal))
                .ToArray();
            foreach (var edge in edges.Where(e => e.Kind is "IMPLEMENTS" or "METHOD_IMPLEMENTS"))
            {
                if (symbolById.ContainsKey(edge.ToId))
                {
                    AddImplementor(edge.ToId, edge.FromId);
                }
                else if (edge.ToId.StartsWith("typeref:", StringComparison.Ordinal))
                {
                    foreach (var abstraction in abstractions.Where(a => edge.ToId.EndsWith($":{a.Name}", StringComparison.Ordinal)))
                    {
                        AddImplementor(abstraction.SymbolId, edge.FromId);
                    }
                }
            }

            var hits = new List<Pending>();
            foreach (var abstraction in abstractions.OrderBy(a => a.SymbolId, StringComparer.Ordinal))
            {
                if (contractSymbolIds.Contains(abstraction.SymbolId)
                    || !implementorsByTarget.TryGetValue(abstraction.SymbolId, out var implementors)
                    || implementors.Count != 1)
                {
                    continue;
                }

                var capsule = CapsuleOf(abstraction.Path);
                if (capsule is null)
                {
                    continue; // no subject entity to attach the signal to (outside every capsule).
                }

                var implementorName = symbolById.TryGetValue(implementors.Single(), out var impl) ? impl.Name : implementors.Single();
                hits.Add(new Pending(
                    "V-G1", "overdesign", capsule.EntityId, abstraction.SymbolId, abstraction.SymKey,
                    $"abstraction '{abstraction.Name}' has exactly one implementation ('{implementorName}') — single-implementation polymorphism signal: collapse until a second implementation exists",
                    [new DepaEvidenceRef("declaration", abstraction.Path, abstraction.StartLine,
                        $"single-implementation signal: {abstraction.Name} <- {implementorName} only")],
                    SignalConfidenceCeiling,
                    abstraction.SymbolId));
            }

            return RuleOutcome.Ran(hits);
        }

        // V-A1 (catalog D2, phenomenon-level): threading-primitive density per symbol — bare
        // locks smearing shared state instead of messages. Confidence capped at 0.6, evidence
        // marked heuristic (rule-map.md implemented-weakened).
        RuleOutcome DetectA1()
        {
            if (externalCalls is null)
            {
                return RuleOutcome.Blocked("BLOCKED: ck_external_call observation table is absent — out-of-repo calls were not indexed, V-A1 cannot see threading primitives; not guessing.");
            }

            if (capsules.Count == 0)
            {
                return RuleOutcome.Blocked($"BLOCKED: {MissingInputOf("V-A1")} — the density signal needs a subject entity to attach to; not guessing.");
            }

            var hits = new List<Pending>();
            foreach (var group in externalCalls
                         .Where(c => (c.Category.Length > 0 ? c.Category : DepaEffectCatalog.Match(effectRules, c.TargetKey)?.Category ?? "") == "threading")
                         .GroupBy(c => c.CallerId, StringComparer.Ordinal)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var total = group.Sum(c => c.Count);
                if (total < ThreadingDensityThreshold || !symbolById.TryGetValue(group.Key, out var callerSym))
                {
                    continue;
                }

                var subject = implBySymbolId.GetValueOrDefault(group.Key);
                var subjectEntityId = subject?.EntityId ?? CapsuleOf(callerSym.Path)?.EntityId ?? "";
                if (subjectEntityId.Length == 0)
                {
                    continue; // no depa entity to attach the signal to.
                }

                var evidence = group
                    .OrderBy(c => c.TargetKey, StringComparer.Ordinal)
                    .Select(c =>
                    {
                        var path = filePaths.TryGetValue(c.FirstFileId, out var p) && p.Length > 0 ? p : callerSym.Path;
                        var line = c.FirstLine > 0 ? c.FirstLine : callerSym.StartLine;
                        return path.Length > 0 && line > 0
                            ? new DepaEvidenceRef("external_call", path, line,
                                $"heuristic phenomenon (lock density): {callerSym.Name} CALLS {c.TargetKey} (count={c.Count})")
                            : null;
                    })
                    .Where(e => e is not null)
                    .Select(e => e!)
                    .ToArray();
                if (evidence.Length == 0)
                {
                    continue;
                }

                hits.Add(new Pending(
                    "V-A1", "actor", subjectEntityId, callerSym.SymbolId, callerSym.SymKey,
                    $"'{callerSym.Name}' aggregates {total} threading-primitive calls — bare locks smearing shared state instead of a single owner behind messages (phenomenon-level signal)",
                    evidence,
                    Math.Min(subject?.Confidence ?? 1.0, PhenomenonConfidenceCeiling),
                    group.Key));
            }

            return RuleOutcome.Ran(hits);
        }

        // ---- materialize: idempotent upsert + violates/signal edges (design §5.2) ----
        var detectedAt = om.Runtime.Options.TimeProvider.GetUtcNow().UtcDateTime.ToString("O");
        var materialized = new Dictionary<string, DepaViolationSummary>(StringComparer.Ordinal);
        var desiredSignals = new Dictionary<string, HashSet<(string From, string To)>>(StringComparer.Ordinal)
        {
            ["effect_leaks_through"] = [],
            ["backwrites"] = [],
        };

        foreach (var pending in outcomes.Values.SelectMany(o => o.Hits))
        {
            var subjectKey = pending.SubjectSymKey.Length > 0 ? pending.SubjectSymKey : pending.SubjectEntityId;
            var id = $"depa:violation:{pending.RuleId}@{subjectKey}@{StableHash(pending.EvidenceKey)}";
            if (materialized.ContainsKey(id))
            {
                continue;
            }

            await om.UpsertEntityAsync(id, "depa_violation", $"{pending.RuleId} on {subjectKey}", ct);
            await om.SetPropertyAsync(id, "rule_id", pending.RuleId, cancellationToken: ct);
            await om.SetPropertyAsync(id, "verdict", "GAP", cancellationToken: ct);
            await om.SetPropertyAsync(id, "dimension", pending.Dimension, cancellationToken: ct);
            await om.SetPropertyAsync(id, "message", pending.Message, cancellationToken: ct);
            await om.SetPropertyAsync(id, "confidence", pending.Confidence, cancellationToken: ct);
            await om.SetPropertyAsync(id, "assigned_by", pending.Confidence >= 1.0 ? "config" : "heuristic", cancellationToken: ct);
            await om.SetPropertyAsync(id, "symbol_id", pending.SubjectSymbolId, cancellationToken: ct);
            await om.SetPropertyAsync(id, "sym_key", pending.SubjectSymKey, cancellationToken: ct);
            await om.SetPropertyAsync(id, "path", pending.Evidence[0].Path, cancellationToken: ct);
            await om.SetPropertyAsync(id, "line", pending.Evidence[0].Line, cancellationToken: ct);
            await om.SetPropertyAsync(id, "evidence_json", new Dictionary<string, object?>
            {
                ["rule_id"] = pending.RuleId,
                ["verdict"] = "GAP",
                ["dimension"] = pending.Dimension,
                ["subject"] = new Dictionary<string, object?>
                {
                    ["entity_id"] = pending.SubjectEntityId,
                    ["symbol_id"] = pending.SubjectSymbolId,
                    ["sym_key"] = pending.SubjectSymKey,
                },
                ["evidence"] = pending.Evidence
                    .Select(e => new Dictionary<string, object?>
                    {
                        ["kind"] = e.Kind,
                        ["path"] = e.Path,
                        ["line"] = e.Line,
                        ["detail"] = e.Detail,
                    })
                    .ToArray(),
                ["confidence"] = pending.Confidence,
                ["detected_at"] = detectedAt,
                ["scan_commit"] = scanCommit,
            }, cancellationToken: ct);
            await om.LinkEntitiesAsync(id, "violates", pending.SubjectEntityId, cancellationToken: ct);

            if (pending.Signal is { } signal)
            {
                if (desiredSignals[signal.Rel].Add((signal.From, signal.To)))
                {
                    await om.LinkEntitiesAsync(signal.From, signal.Rel, signal.To, signal.Props, cancellationToken: ct);
                }
            }

            materialized[id] = new DepaViolationSummary(
                id, pending.RuleId, pending.Dimension, pending.SubjectEntityId, pending.Message, pending.Confidence, pending.Evidence);
        }

        // ---- expire: violations (and their edges) of rules that ran but no longer hit ----
        var ranRules = outcomes.Where(o => o.Value.Verdict != "BLOCKED").Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in await ListViolationIdsAsync(om, ct))
        {
            if (materialized.ContainsKey(staleId) || !ranRules.Contains(RuleOfViolationId(staleId)))
            {
                continue;
            }

            // Cascade delete through the OM API: entity + all property rows + all touching
            // edges (including 'violates') in one transaction — no orphan om_property rows.
            await om.DeleteEntityAsync(staleId, ct);
        }

        foreach (var (rel, ruleId) in new[] { ("effect_leaks_through", "V-E1"), ("backwrites", "V-S1") })
        {
            if (!ranRules.Contains(ruleId))
            {
                continue;
            }

            foreach (var (from, to) in await ListEdgesAsync(om, rel, ct))
            {
                if (!desiredSignals[rel].Contains((from, to)))
                {
                    await om.UnlinkEntitiesAsync(from, rel, to, cancellationToken: ct);
                }
            }
        }

        // ---- outcome ----
        var blockedFindings = outcomes
            .Where(o => o.Value.Verdict == "BLOCKED")
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => new DepaRuleFinding(o.Key, "BLOCKED", "", o.Value.BlockedMessage))
            .ToArray();
        var verdicts = outcomes.ToDictionary(o => o.Key, o => o.Value.Verdict, StringComparer.Ordinal);
        var summaries = materialized.Values
            .OrderBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.ViolationId, StringComparer.Ordinal)
            .ToArray();
        return new DepaDetectionOutcome(blockedFindings, verdicts, summaries);
    }

    private static string RuleOfViolationId(string violationId)
    {
        const string prefix = "depa:violation:";
        if (!violationId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "";
        }

        var at = violationId.IndexOf('@', prefix.Length);
        return at > prefix.Length ? violationId[prefix.Length..at] : "";
    }

    private static string StableHash(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(digest)[..12].ToLowerInvariant();
    }

    private static bool IsUnderPath(string path, string prefix) =>
        prefix.Length > 0 && (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));

    // ---- batch-1 helpers (track expand-depa-detection-rules T2.1) ----

    private static bool IsTypeKind(string kind) =>
        kind is "class" or "record" or "struct" or "record_struct" or "interface" or "enum";

    /// <summary>
    /// Extracts the declared-type text of a field/property from its ck signature: everything
    /// before the member name with leading modifiers stripped (e.g.
    /// "public Dictionary&lt;string, object&gt; Extras { get; set; }" → "Dictionary&lt;string, object&gt;").
    /// Best-effort textual extraction — signatures are indexer-produced, not parsed types.
    /// </summary>
    internal static string DeclaredTypeTokenOf(CkSymbol field)
    {
        var signature = field.Signature;
        if (signature.Length == 0 || field.Name.Length == 0)
        {
            return "";
        }

        var marker = " " + field.Name;
        var index = signature.IndexOf(marker + " ", StringComparison.Ordinal);
        if (index < 0 && signature.EndsWith(marker, StringComparison.Ordinal))
        {
            index = signature.Length - marker.Length;
        }

        if (index < 0)
        {
            return "";
        }

        var tokens = signature[..index].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string[] modifiers =
        [
            "public", "private", "protected", "internal", "static", "readonly", "virtual",
            "override", "sealed", "required", "new", "volatile", "const", "abstract",
        ];
        while (tokens.Count > 0 && modifiers.Contains(tokens[0], StringComparer.Ordinal))
        {
            tokens.RemoveAt(0);
        }

        return string.Join(" ", tokens);
    }

    /// <summary>Whitespace-insensitive type-text normalization for name+type comparisons.</summary>
    internal static string NormalizeType(string typeText) => typeText.Replace(" ", "");

    /// <summary>
    /// V-R1 big-bag lexicon (runtime-explicitness "runtime = Any" red light): unstructured
    /// carrier types that defeat role placement. Operates on normalized (space-free) type text.
    /// </summary>
    internal static bool IsBagType(string normalizedType) =>
        normalizedType.Length > 0
        && (normalizedType is "object" or "object?" or "dynamic" or "System.Object" or "Hashtable" or "System.Collections.Hashtable"
            || normalizedType.StartsWith("Dictionary<string,object", StringComparison.Ordinal)
            || normalizedType.StartsWith("IDictionary<string,object", StringComparison.Ordinal)
            || normalizedType.StartsWith("System.Collections.Generic.Dictionary<string,object", StringComparison.Ordinal)
            || normalizedType.StartsWith("System.Collections.Generic.IDictionary<string,object", StringComparison.Ordinal)
            || normalizedType.Contains("ExpandoObject", StringComparison.Ordinal));

    /// <summary>
    /// V-S4 lexicon (fact-source-truth rules ③④): file-metadata probes that infer run state
    /// from persistence-layer metadata instead of the grade-3 control plane.
    /// </summary>
    internal static bool IsFileMetadataProbe(string targetKey) =>
        targetKey.StartsWith("System.IO.", StringComparison.Ordinal)
        && (targetKey.EndsWith(".Exists", StringComparison.Ordinal)
            || targetKey.Contains("LastWriteTime", StringComparison.Ordinal)
            || targetKey.Contains("LastAccessTime", StringComparison.Ordinal)
            || targetKey.Contains("CreationTime", StringComparison.Ordinal));

    /// <summary>Word-boundary containment: 'word' in 'text' not glued to identifier characters.</summary>
    internal static bool ContainsWord(string text, string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0;
             i = i + 1 >= text.Length ? -1 : text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            var beforeOk = i == 0 || (!char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_');
            var end = i + word.Length;
            var afterOk = end >= text.Length || (!char.IsLetterOrDigit(text[end]) && text[end] != '_');
            if (beforeOk && afterOk)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Slash-segmented path glob: '*' matches within one segment (may be partial),
    /// '**' matches any number of segments (including zero). Same semantics as the
    /// dot-segmented FQN matcher of the effect whitelist, over '/' instead of '.'.
    /// </summary>
    internal static bool PathGlobMatch(string pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(path))
        {
            return false;
        }

        return MatchSegments(pattern.Split('/'), 0, path.Split('/'), 0);
    }

    private static bool MatchSegments(string[] pattern, int pi, string[] target, int ti)
    {
        if (pi == pattern.Length)
        {
            return ti == target.Length;
        }

        if (pattern[pi] == "**")
        {
            return MatchSegments(pattern, pi + 1, target, ti)
                || (ti < target.Length && MatchSegments(pattern, pi, target, ti + 1));
        }

        return ti < target.Length
            && MatchSegment(pattern[pi], target[ti])
            && MatchSegments(pattern, pi + 1, target, ti + 1);
    }

    private static bool MatchSegment(string pattern, string segment)
    {
        return MatchChars(pattern, 0, segment, 0);

        static bool MatchChars(string pattern, int pi, string segment, int si)
        {
            while (true)
            {
                if (pi == pattern.Length)
                {
                    return si == segment.Length;
                }

                if (pattern[pi] == '*')
                {
                    if (MatchChars(pattern, pi + 1, segment, si))
                    {
                        return true;
                    }

                    if (si == segment.Length)
                    {
                        return false;
                    }

                    si++;
                    continue;
                }

                if (si == segment.Length || pattern[pi] != segment[si])
                {
                    return false;
                }

                pi++;
                si++;
            }
        }
    }

    private static async Task<IReadOnlyList<Snap>> LoadTypeAsync(CozoOm om, string typeName, CancellationToken ct)
    {
        var result = await om.Runtime.Store.RunAsync(
            """
            ?[id, attr_name, value] :=
              *om_entity{ id, type_name: $type },
              *om_property{ entity_id: id, attr_name, value @ "NOW" }
            """,
            new Dictionary<string, object?> { ["type"] = typeName },
            cancellationToken: ct);
        return result.Rows
            .Where(row => row[0].ValueKind == JsonValueKind.String && row[1].ValueKind == JsonValueKind.String)
            .GroupBy(row => row[0].GetString()!, StringComparer.Ordinal)
            .Select(group => new Snap(
                group.Key,
                group.ToDictionary(row => row[1].GetString()!, row => row[2], StringComparer.Ordinal)))
            .OrderBy(s => s.EntityId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> ListViolationIdsAsync(CozoOm om, CancellationToken ct)
    {
        var result = await om.Runtime.Store.RunAsync(
            """?[id] := *om_entity{ id, type_name: "depa_violation" }""",
            cancellationToken: ct);
        return result.Rows
            .Where(row => row[0].ValueKind == JsonValueKind.String)
            .Select(row => row[0].GetString()!)
            .ToArray();
    }

    private static async Task<IReadOnlyList<(string From, string To)>> ListEdgesAsync(CozoOm om, string relName, CancellationToken ct)
    {
        var result = await om.Runtime.Store.RunAsync(
            """
            ?[from_id, to_id] :=
              *om_edge{ from_id, rel_name: $rel, to_id, props: _props @ "NOW" }
            """,
            new Dictionary<string, object?> { ["rel"] = relName },
            cancellationToken: ct);
        return result.Rows
            .Where(row => row[0].ValueKind == JsonValueKind.String && row[1].ValueKind == JsonValueKind.String)
            .Select(row => (row[0].GetString()!, row[1].GetString()!))
            .ToArray();
    }
}
