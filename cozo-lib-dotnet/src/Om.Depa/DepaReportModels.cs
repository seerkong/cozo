namespace Cozo.DotNet.Om.Depa;

/// <summary>
/// Options for the report aggregation queries (track add-llm-wiki-depa-conformance-tools,
/// design §5.3). <see cref="ScanFirst"/> = true (default) runs the full depa_scan pipeline
/// before aggregating; false aggregates the violations already persisted in the store — in
/// that mode rules without persisted hits report BLOCKED, because PASS cannot be told apart
/// from BLOCKED without running the detectors (design §5.1: BLOCKED is never folded into PASS).
/// </summary>
/// <param name="ScanFirst">Run <c>DepaScanAsync</c> before aggregating.</param>
/// <param name="Dimension">Only report this dimension (data | effect | processor | layering | fact_source | actor | overdesign | vendor).</param>
/// <param name="MapPath">Path to depa-map.json, forwarded to the scan.</param>
/// <param name="EffectsPath">Path to depa-effects.json, forwarded to the scan.</param>
public sealed record DepaConformanceOptions(
    bool ScanFirst = true,
    string? Dimension = null,
    string? MapPath = null,
    string? EffectsPath = null);

/// <summary>
/// One detection rule inside a dimension group: its verdict (PASS | GAP | BLOCKED), the
/// BLOCKED reason (empty otherwise) and its violations ordered by confidence descending.
/// </summary>
public sealed record DepaRuleReport(
    string RuleId,
    string Verdict,
    string BlockedReason,
    IReadOnlyList<DepaViolationSummary> Violations);

/// <summary>One dimension group of the conformance report (design §5.3).</summary>
public sealed record DepaDimensionReport(string Dimension, IReadOnlyList<DepaRuleReport> Rules);

/// <summary>
/// The conformance report (design §5.3): rule verdicts grouped by dimension
/// (data / effect / processor / layering / fact_source / actor / overdesign / vendor) plus the structural
/// existential-rule findings that do not belong to a single detection rule. The
/// design-gap placeholder rules (V-D2 / V-P1 / V-A*, design §3 末 "MVP 明确不做")
/// always report BLOCKED with 原因=静态观测不足 — they are never faked as PASS.
/// </summary>
/// <param name="Dimensions">Dimension groups in canonical order.</param>
/// <param name="StructuralFindings">Existential-check findings (design §2.3), e.g. capsule_must_expose_entry.</param>
/// <param name="Scanned">Whether this report ran a fresh scan (ScanFirst) or aggregated persisted violations.</param>
public sealed record DepaConformanceReport(
    IReadOnlyList<DepaDimensionReport> Dimensions,
    IReadOnlyList<DepaRuleFinding> StructuralFindings,
    bool Scanned);

/// <summary>One graded fact-source node of the grade map (fact-source-truth ladder, grade 1-7).</summary>
public sealed record DepaFactGradeNode(
    string EntityId,
    string Label,
    int Grade,
    string GradeId,
    string ExpectedOwner,
    string Path,
    int Line);

/// <summary>One adjacency edge of the grade map (fact_written_by | projection_derived_from).</summary>
public sealed record DepaFactGradeEdge(string Relation, string FromId, string ToId);

/// <summary>
/// The fact-source grade map (design §5.3): every depa_fact_source node with its grading
/// plus the fact_written_by / projection_derived_from adjacency.
/// </summary>
public sealed record DepaFactGradeMap(
    IReadOnlyList<DepaFactGradeNode> FactSources,
    IReadOnlyList<DepaFactGradeEdge> Edges);

/// <summary>
/// Per-dimension health row. <see cref="Score"/> is display-layer only
/// (= PASS rules / <see cref="RulesTotal"/>); BLOCKED rules reduce coverage
/// (<see cref="RulesCovered"/>), never count as compliance.
/// </summary>
public sealed record DepaDimensionHealth(
    string Dimension,
    int GapCount,
    int BlockedCount,
    int RulesCovered,
    int RulesTotal,
    double Score);

/// <summary>
/// The health score surface (design §5.3): per-dimension rows only. Deliberately carries no
/// merged overall field — tao/depa-paradigm: the four dimensions are judged independently
/// (四维分别裁决), so a single combined score would be a category error, not a summary.
/// </summary>
public sealed record DepaHealthScore(IReadOnlyList<DepaDimensionHealth> Dimensions, string Note);
