namespace Cozo.DotNet.Om.Depa;

/// <summary>
/// Options for <c>DepaScanAsync</c> (track add-llm-wiki-depa-ontology, design §3 pipeline
/// segments ①②④). Both paths are optional: a missing depa-map.json means zero config
/// annotations (heuristics may still fire); a missing depa-effects.json means the built-in
/// whitelist only.
/// </summary>
/// <param name="MapPath">Path to depa-map.json (design §4.3 channel 1, assigned_by=config).</param>
/// <param name="EffectsPath">Path to depa-effects.json (design §4.1 user extension).</param>
public sealed record DepaScanOptions(
    string? MapPath = null,
    string? EffectsPath = null)
{
    // --- track fix-om-depa-conformance-gaps REC-2 (add-only, init properties keep the
    // positional constructor and every existing caller unchanged): pre-parsed object channel.
    // File reads happen only at the public entry (DepaScanAsync); the pipeline core consumes
    // these resolved objects and never touches the filesystem. ---

    /// <summary>Pre-parsed depa-map.json. Non-null takes precedence over <see cref="MapPath"/>.</summary>
    public DepaMapConfig? Map { get; init; }

    /// <summary>Pre-parsed user effect rules (depa-effects.json content). Non-null takes precedence over <see cref="EffectsPath"/>.</summary>
    public IReadOnlyList<DepaEffectRule>? Effects { get; init; }
}

/// <summary>
/// One rule-level scan finding (design §5.1 verdict discipline). Verdict is
/// "GAP" (hit with evidence) or "BLOCKED" (cannot judge: missing annotations or
/// insufficient observation) — BLOCKED is never folded into PASS.
/// </summary>
public sealed record DepaRuleFinding(string RuleId, string Verdict, string EntityId, string Message);

/// <summary>
/// Result of <c>DepaScanAsync</c>. Counts cover what this scan materialized/relinked
/// (idempotent upserts — a second scan over the same data yields the same counts).
/// </summary>
/// <param name="EntityCounts">Materialized depa entities per type (depa_capsule, depa_contract, ...).</param>
/// <param name="RelationCounts">Distinct structural links asserted per relation name (design §2.1).</param>
/// <param name="RuleFindings">Existential-check findings (design §2.3) or the zero-annotation BLOCKED rows.</param>
/// <param name="AnnotatedFromConfig">Entities whose judgement came from depa-map.json.</param>
/// <param name="AnnotatedFromHeuristic">Entities produced by the heuristic fallback (confidence &lt;= 0.7).</param>
public sealed record DepaScanResult(
    IReadOnlyDictionary<string, int> EntityCounts,
    IReadOnlyDictionary<string, int> RelationCounts,
    IReadOnlyList<DepaRuleFinding> RuleFindings,
    int AnnotatedFromConfig,
    int AnnotatedFromHeuristic)
{
    // --- track add-llm-wiki-depa-conformance-tools (design §3 step ③): add-only init
    // properties so the positional G5 constructor and every existing caller stay valid ---

    /// <summary>Violations materialized (or re-asserted) by this scan, ordered by rule then id.</summary>
    public IReadOnlyList<DepaViolationSummary> Violations { get; init; } = [];

    /// <summary>
    /// Verdict per detection rule (V-D1, V-E1, V-E2, V-F1, V-F2, V-L1, V-L3, V-S1):
    /// "PASS" (ran, no hit), "GAP" (hit with evidence) or "BLOCKED" (missing input — never
    /// folded into PASS, see design §5.1).
    /// </summary>
    public IReadOnlyDictionary<string, string> DetectionVerdicts { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>One evidence entry of a violation (design §5.2): every entry carries a real path:line.</summary>
public sealed record DepaEvidenceRef(string Kind, string Path, int Line, string Detail);

/// <summary>
/// One materialized depa_violation (design §3/§5.2). <see cref="ViolationId"/> is stable across
/// scans for the same (rule, subject, evidence key), so repeated scans upsert in place and
/// hits that disappear are expired at scan end.
/// </summary>
public sealed record DepaViolationSummary(
    string ViolationId,
    string RuleId,
    string Dimension,
    string SubjectEntityId,
    string Message,
    double Confidence,
    IReadOnlyList<DepaEvidenceRef> Evidence);
