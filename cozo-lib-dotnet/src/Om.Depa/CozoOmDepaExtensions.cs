namespace Cozo.DotNet.Om.Depa;

/// <summary>
/// Public surface of the Om.Depa capsule (track add-llm-wiki-depa-ontology, design "Track 补充设计"):
/// ontology initialization plus the depa_scan pipeline segment. Everything else — including the
/// effect-whitelist materialization, which DepaScanAsync runs automatically — is internal.
/// </summary>
public static class CozoOmDepaExtensions
{
    /// <summary>
    /// Defines the DEPA ontology: depa_node root with common attributes, the 11 depa_* subtypes,
    /// the structural and violation-signal relations, and the five Check-mode existential rules.
    /// Idempotent — safe to call on every startup.
    /// </summary>
    public static Task InitDepaOntologyAsync(this CozoOm om, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        return DepaOntologySchema.InitAsync(om, cancellationToken);
    }

    /// <summary>
    /// Materializes the effective effect-API whitelist (built-in table merged with an optional
    /// depa-effects.json; user entries are matched first, identical patterns override) as
    /// depa_effect_api entities. Requires <see cref="InitDepaOntologyAsync"/> to have run.
    /// Idempotent upsert; returns the number of whitelist entries materialized.
    /// Internal (design public-surface list): <see cref="DepaScanAsync"/> runs this sync
    /// automatically before annotation sync, so the public workflow never needs it directly;
    /// tests reach it via InternalsVisibleTo.
    /// </summary>
    internal static Task<int> SyncEffectApisAsync(this CozoOm om, string? effectsPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        return DepaOntologySchema.SyncEffectApisAsync(
            om, effectsPath is null ? null : DepaEffectCatalog.LoadUserEffects(effectsPath), cancellationToken);
    }

    /// <summary>
    /// First segment of the depa_scan pipeline (design §3 steps ①②④): syncs role annotations
    /// from depa-map.json (assigned_by=config) with a heuristic fallback (assigned_by=heuristic,
    /// confidence &lt;= 0.7), materializes the structural relations of design §2.1 over the ck_*
    /// observation layer, and reports existential-rule findings. Zero annotations yield BLOCKED
    /// findings instead of guesses; repeated scans are idempotent. Red-light detection queries
    /// (design §3 step ③) are out of scope of this segment.
    /// Configuration explicitness (track fix-om-depa-conformance-gaps REC-2): this entry resolves
    /// MapPath/EffectsPath to parsed objects here — the pipeline core only consumes resolved
    /// configuration and performs zero file reads. Pre-parsed <see cref="DepaScanOptions.Map"/> /
    /// <see cref="DepaScanOptions.Effects"/> take precedence over the path fields.
    /// </summary>
    public static Task<DepaScanResult> DepaScanAsync(this CozoOm om, DepaScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        var (map, userEffects) = ResolveScanInputs(options);
        return DepaScanPipeline.ScanAsync(om, map, userEffects, cancellationToken);
    }

    /// <summary>
    /// Entry-layer configuration resolution (track fix-om-depa-conformance-gaps REC-2): the only
    /// place the depa-map.json / depa-effects.json path fields are turned into objects. Non-null
    /// pre-parsed objects win over the path fields; a missing/blank path resolves to
    /// <see cref="DepaMapConfig.Empty"/> / null (built-in effects only).
    /// </summary>
    internal static (DepaMapConfig Map, IReadOnlyList<DepaEffectRule>? UserEffects) ResolveScanInputs(DepaScanOptions? options)
    {
        var map = options?.Map
            ?? (string.IsNullOrWhiteSpace(options?.MapPath) ? DepaMapConfig.Empty : DepaMapConfig.Load(options!.MapPath!));
        var effects = options?.Effects
            ?? (string.IsNullOrWhiteSpace(options?.EffectsPath) ? null : DepaEffectCatalog.LoadUserEffects(options!.EffectsPath!));
        return (map, effects);
    }

    // --- report aggregation queries (track add-llm-wiki-depa-conformance-tools, design §5.3;
    // together with DepaScanAsync these are the whole public surface of Om.Depa) ---

    /// <summary>
    /// The conformance report (design §5.3): detection-rule verdicts grouped by dimension
    /// (data / effect / processor / layering / fact_source / actor / overdesign / vendor) — each rule PASS/GAP/BLOCKED,
    /// violations ordered by confidence descending with path:line evidence, BLOCKED rows
    /// carrying their reason — plus the structural existential findings. ScanFirst (default)
    /// runs the depa_scan pipeline first; ScanFirst=false aggregates persisted violations,
    /// reporting BLOCKED for rules without hits (a rule that did not run cannot claim PASS).
    /// </summary>
    public static Task<DepaConformanceReport> GetConformanceReportAsync(this CozoOm om, DepaConformanceOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        return DepaReportQueries.GetConformanceReportAsync(om, options, cancellationToken);
    }

    /// <summary>
    /// The fact-source grade map (design §5.3): every depa_fact_source node with its grading
    /// (grade / grade_id / expected_owner) plus the fact_written_by and
    /// projection_derived_from adjacency currently asserted in the store.
    /// </summary>
    public static Task<DepaFactGradeMap> GetFactGradeMapAsync(this CozoOm om, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        return DepaReportQueries.GetFactGradeMapAsync(om, cancellationToken);
    }

    /// <summary>
    /// Per-dimension health rows (design §5.3): gap/blocked counts, rule coverage and a
    /// display-only score per dimension. Deliberately no merged overall field — the four
    /// DEPA dimensions are judged independently and never folded into one verdict.
    /// </summary>
    public static Task<DepaHealthScore> GetHealthScoreAsync(this CozoOm om, DepaConformanceOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        return DepaReportQueries.GetHealthScoreAsync(om, options, cancellationToken);
    }
}
