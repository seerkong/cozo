using System.Text.Json;

namespace Cozo.DotNet.Om.Depa;

/// <summary>
/// Report aggregation over the depa_* judgement layer (track add-llm-wiki-depa-conformance-tools,
/// design §5.3): the read-side backing of the depa_conformance / fact_grade_map / health_score
/// tools. Aggregation only — all detection lives in <see cref="DepaViolationDetectors"/>.
/// </summary>
internal static class DepaReportQueries
{
    /// <summary>
    /// Canonical eight-dimension order (track expand-depa-detection-rules T1.1: design §5.3
    /// grouping + actor + overdesign + vendor so the report spans the full rubrics catalog).
    /// </summary>
    private static readonly string[] DimensionOrder =
    [
        "data", "effect", "processor", "layering", "fact_source", "actor", "overdesign", "vendor",
    ];

    /// <summary>
    /// Rule ↔ dimension mapping, anchored to rubrics/rule-map.md (the in-repo判据真源). V-F1/V-F2
    /// belong to layering per violation-catalog.md F 组 (dimension 修正, track
    /// expand-depa-detection-rules decisions.md). Catalog rows that no static detector can judge
    /// are explicit placeholders (see <see cref="PlaceholderRules"/>) so coverage spans the full
    /// catalog instead of the implemented subset; the four human-only rows (D3/D4/G4/G5) are
    /// marked in rule-map.md and stay out of this denominator.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> RulesByDimension = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // Batch-1 (track expand-depa-detection-rules T2.1) and batch-2 (T3.1: V-S2a/V-S2b/
        // V-P2, keyed on the depa-map recoveryPaths/layers annotations) detectors joined
        // their rule-map dimensions: 24 detectors + 8 placeholders = 32 rules in the
        // denominator.
        ["data"] = ["V-D1", "V-D2", "V-D3"],
        ["effect"] = ["V-E1", "V-E2", "V-E3"],
        ["processor"] = ["V-C1", "V-P1", "V-P2", "V-P3", "V-P4"],
        ["layering"] = ["V-F1", "V-F2", "V-F3", "V-L1", "V-L2", "V-L3", "V-L4", "V-L5", "V-L6", "V-R1"],
        ["fact_source"] = ["V-S1", "V-S2a", "V-S2b", "V-S3", "V-S4"],
        ["actor"] = ["V-A*", "V-A1"],
        ["overdesign"] = ["V-G1", "V-G2", "V-G3"],
        ["vendor"] = ["V-V*"],
    };

    /// <summary>
    /// Placeholder rules with no detector — verdict is BLOCKED by construction, and the reason
    /// names the missing observation class (gap-matrix 分类：需语句级 AST / 需运行时语义 /
    /// 需人工语义比对), never a generic excuse. Mirrors rule-map.md status placeholder-BLOCKED.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PlaceholderRules = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["V-D2"] = "需运行时语义：状态可重建性（事件源→重放）无法从静态符号图证明",
        ["V-P1"] = "需语句级 AST：if/elif 字符串分发在方法体语句层，符号级观测不可见",
        ["V-P3"] = "需语句级 AST：未知 id 静默回退（return null/None 分支）在方法体语句层",
        ["V-P4"] = "需运行时语义：command（等应答）与 message（单向）的调用语义静态不可判",
        ["V-A*"] = "需运行时语义：并发任务/mailbox 行为静态观测不足",
        ["V-G2"] = "需人工语义比对：outer/inner 字段同构与\"只透传\"的空壳 adapter 判定",
        ["V-G3"] = "需语句级 AST：配置开关的调用点实参是否全程默认值",
        ["V-V*"] = "需人工语义比对：\"自造 vendor 原语\"需与 vendor 能力清单人工比对",
    };

    private static string PlaceholderBlockedReason(string rule) =>
        $"静态观测不足（占位规则，见 rubrics/rule-map.md）：{PlaceholderRules[rule]}";

    private static readonly HashSet<string> DetectionRuleIds = RulesByDimension.Values
        .SelectMany(rules => rules)
        .Where(rule => !PlaceholderRules.ContainsKey(rule))
        .ToHashSet(StringComparer.Ordinal);

    private const string HealthNote =
        "Display layer only: each DEPA dimension is judged independently (八组分别裁决) and never "
        + "merged into a single overall verdict. score = PASS rules / dimension rule total; "
        + "BLOCKED rules reduce coverage, they never count as compliance. The rule total spans "
        + "the full rubrics catalog (rule-map.md): undetectable rows are explicit BLOCKED "
        + "placeholders, human-only rows stay out of the denominator.";

    internal static async Task<DepaConformanceReport> GetConformanceReportAsync(
        CozoOm om, DepaConformanceOptions? options, CancellationToken ct)
    {
        var opts = options ?? new DepaConformanceOptions();
        if (opts.Dimension is { Length: > 0 } dimension && !RulesByDimension.ContainsKey(dimension))
        {
            throw new ArgumentException(
                $"Invalid dimension: {dimension}. Expected one of: {string.Join(", ", DimensionOrder)}.");
        }

        IReadOnlyDictionary<string, string> verdicts;
        IReadOnlyList<DepaViolationSummary> violations;
        IReadOnlyList<DepaRuleFinding> findings;
        if (opts.ScanFirst)
        {
            // Entry-layer resolution (REC-2): paths become objects here, before the core pipeline.
            var (map, userEffects) = CozoOmDepaExtensions.ResolveScanInputs(new DepaScanOptions(opts.MapPath, opts.EffectsPath));
            var scan = await DepaScanPipeline.ScanAsync(om, map, userEffects, ct);
            verdicts = scan.DetectionVerdicts;
            violations = scan.Violations;
            findings = scan.RuleFindings;
        }
        else
        {
            // Aggregate what the store holds without re-detecting. Persisted violations are GAP by
            // construction; every other rule is BLOCKED — a rule that was not (re)run cannot claim
            // PASS (design §5.1).
            await DepaOntologySchema.InitAsync(om, ct);
            violations = await LoadPersistedViolationsAsync(om, ct);
            var hitRules = violations.Select(v => v.RuleId).ToHashSet(StringComparer.Ordinal);
            verdicts = DetectionRuleIds.ToDictionary(
                rule => rule,
                rule => hitRules.Contains(rule) ? "GAP" : "BLOCKED",
                StringComparer.Ordinal);
            findings = DetectionRuleIds
                .Where(rule => !hitRules.Contains(rule))
                .OrderBy(rule => rule, StringComparer.Ordinal)
                .Select(rule => new DepaRuleFinding(
                    rule,
                    "BLOCKED",
                    "",
                    "BLOCKED: aggregating persisted violations without a scan (scanFirst=false) — "
                    + "this rule has no persisted hit, and PASS cannot be told apart from BLOCKED "
                    + "without running the detectors."))
                .ToArray();
        }

        var blockedByRule = findings
            .Where(f => f.Verdict == "BLOCKED" && DetectionRuleIds.Contains(f.RuleId))
            .GroupBy(f => f.RuleId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.Ordinal);
        var violationsByRule = violations
            .GroupBy(v => v.RuleId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DepaViolationSummary>)g.ToArray(), StringComparer.Ordinal);

        var dimensions = DimensionOrder
            .Where(dim => opts.Dimension is not { Length: > 0 } || dim == opts.Dimension)
            .Select(dim => new DepaDimensionReport(
                dim,
                RulesByDimension[dim]
                    .Select(rule => PlaceholderRules.ContainsKey(rule)
                        // Placeholder (rule-map.md status placeholder-BLOCKED): no static detector
                        // exists, so the verdict is BLOCKED by construction — never PASS, never
                        // silently dropped, and the reason names the missing observation class.
                        ? new DepaRuleReport(rule, "BLOCKED", PlaceholderBlockedReason(rule), [])
                        : new DepaRuleReport(
                            rule,
                            verdicts.GetValueOrDefault(rule, "BLOCKED"),
                            blockedByRule.GetValueOrDefault(rule, ""),
                            (violationsByRule.GetValueOrDefault(rule) ?? [])
                                .OrderByDescending(v => v.Confidence)
                                .ThenBy(v => v.ViolationId, StringComparer.Ordinal)
                                .ToArray()))
                    .ToArray()))
            .ToArray();
        var structural = findings
            .Where(f => !DetectionRuleIds.Contains(f.RuleId))
            .ToArray();
        return new DepaConformanceReport(dimensions, structural, opts.ScanFirst);
    }

    internal static async Task<DepaFactGradeMap> GetFactGradeMapAsync(CozoOm om, CancellationToken ct)
    {
        await DepaOntologySchema.InitAsync(om, ct);
        var nodes = (await LoadEntitySnapsAsync(om, "depa_fact_source", ct))
            .Select(snap => new DepaFactGradeNode(
                snap.EntityId,
                snap.Label,
                (int)Num(snap.Props, "grade", 0),
                Str(snap.Props, "grade_id"),
                Str(snap.Props, "expected_owner"),
                Str(snap.Props, "path"),
                (int)Num(snap.Props, "line", 0)))
            .OrderBy(n => n.Grade)
            .ThenBy(n => n.EntityId, StringComparer.Ordinal)
            .ToArray();
        var edges = new List<DepaFactGradeEdge>();
        foreach (var relation in new[] { "fact_written_by", "projection_derived_from" })
        {
            foreach (var (from, to) in await ListEdgesAsync(om, relation, ct))
            {
                edges.Add(new DepaFactGradeEdge(relation, from, to));
            }
        }

        return new DepaFactGradeMap(
            nodes,
            edges
                .OrderBy(e => e.Relation, StringComparer.Ordinal)
                .ThenBy(e => e.FromId, StringComparer.Ordinal)
                .ThenBy(e => e.ToId, StringComparer.Ordinal)
                .ToArray());
    }

    internal static async Task<DepaHealthScore> GetHealthScoreAsync(
        CozoOm om, DepaConformanceOptions? options, CancellationToken ct)
    {
        var report = await GetConformanceReportAsync(om, options, ct);
        var dimensions = report.Dimensions
            .Select(dim => new DepaDimensionHealth(
                dim.Dimension,
                GapCount: dim.Rules.Sum(r => r.Violations.Count),
                BlockedCount: dim.Rules.Count(r => r.Verdict == "BLOCKED"),
                RulesCovered: dim.Rules.Count(r => r.Verdict != "BLOCKED"),
                RulesTotal: dim.Rules.Count,
                Score: dim.Rules.Count == 0
                    ? 0
                    : Math.Round((double)dim.Rules.Count(r => r.Verdict == "PASS") / dim.Rules.Count, 4)))
            .ToArray();
        return new DepaHealthScore(dimensions, HealthNote);
    }

    // ---- store read-back helpers (judgement layer @ NOW) ----

    private sealed record EntitySnap(string EntityId, string Label, IReadOnlyDictionary<string, JsonElement> Props);

    private static async Task<IReadOnlyList<EntitySnap>> LoadEntitySnapsAsync(CozoOm om, string typeName, CancellationToken ct)
    {
        var entityResult = await om.Runtime.Store.RunAsync(
            """?[id, label] := *om_entity{ id, type_name: $type, label }""",
            new Dictionary<string, object?> { ["type"] = typeName },
            cancellationToken: ct);
        var labels = entityResult.Rows
            .Where(row => row[0].ValueKind == JsonValueKind.String)
            .ToDictionary(
                row => row[0].GetString()!,
                row => row[1].ValueKind == JsonValueKind.String ? row[1].GetString() ?? "" : "",
                StringComparer.Ordinal);
        var propResult = await om.Runtime.Store.RunAsync(
            """
            ?[id, attr_name, value] :=
              *om_entity{ id, type_name: $type },
              *om_property{ entity_id: id, attr_name, value @ "NOW" }
            """,
            new Dictionary<string, object?> { ["type"] = typeName },
            cancellationToken: ct);
        var propsById = propResult.Rows
            .Where(row => row[0].ValueKind == JsonValueKind.String && row[1].ValueKind == JsonValueKind.String)
            .GroupBy(row => row[0].GetString()!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, JsonElement>)g.ToDictionary(row => row[1].GetString()!, row => row[2], StringComparer.Ordinal),
                StringComparer.Ordinal);
        return labels.Keys
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new EntitySnap(
                id,
                labels[id],
                propsById.GetValueOrDefault(id) ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<DepaViolationSummary>> LoadPersistedViolationsAsync(CozoOm om, CancellationToken ct)
    {
        var summaries = new List<DepaViolationSummary>();
        foreach (var snap in await LoadEntitySnapsAsync(om, "depa_violation", ct))
        {
            var ruleId = Str(snap.Props, "rule_id");
            if (ruleId.Length == 0)
            {
                continue; // hand-made violation without the §5.2 fields — not reportable.
            }

            var subject = "";
            var evidence = new List<DepaEvidenceRef>();
            if (snap.Props.TryGetValue("evidence_json", out var evidenceJson) && evidenceJson.ValueKind == JsonValueKind.Object)
            {
                if (evidenceJson.TryGetProperty("subject", out var subjectEl) && subjectEl.ValueKind == JsonValueKind.Object
                    && subjectEl.TryGetProperty("entity_id", out var subjectId) && subjectId.ValueKind == JsonValueKind.String)
                {
                    subject = subjectId.GetString() ?? "";
                }

                if (evidenceJson.TryGetProperty("evidence", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        evidence.Add(new DepaEvidenceRef(
                            GetStr(entry, "kind"), GetStr(entry, "path"), GetInt(entry, "line"), GetStr(entry, "detail")));
                    }
                }
            }

            summaries.Add(new DepaViolationSummary(
                snap.EntityId,
                ruleId,
                Str(snap.Props, "dimension"),
                subject,
                Str(snap.Props, "message"),
                Num(snap.Props, "confidence", 1.0),
                evidence));
        }

        return summaries
            .OrderBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.ViolationId, StringComparer.Ordinal)
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

    private static string Str(IReadOnlyDictionary<string, JsonElement> props, string name) =>
        props.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Num(IReadOnlyDictionary<string, JsonElement> props, string name, double fallback) =>
        props.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

    private static string GetStr(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
