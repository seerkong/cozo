using System.Text.Json;
using Cozo.DotNet.Om.Contracts.Models;

namespace Cozo.DotNet.Om.Depa;

/// <summary>
/// DEPA ontology (interpretation layer over the ck_* observation layer): the depa_node root
/// with 11 subtypes (design §1), the structural + violation-signal relations (design §2.1/§2.2)
/// and the five Check-mode existential rules (design §2.3). Every OM define call is a put-style
/// upsert, so initialization is idempotent by construction.
/// </summary>
internal static class DepaOntologySchema
{
    internal static async Task InitAsync(CozoOm om, CancellationToken cancellationToken)
    {
        await om.InitSchemaAsync(cancellationToken);

        // --- root + common attributes (design §1.0), inherited by all depa_* subtypes ---
        await om.DefineTypeAsync("depa_node", "DEPA ontology root; every depa_* judgement entity inherits from it", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_node", "symbol_id", OmValueType.String, description: "Anchor ck_symbol.symbol_id (empty when anchored to a path/prefix)", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_node", "sym_key", OmValueType.String, description: "Stable key for re-anchoring after reindex", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_node", "path", OmValueType.String, description: "Primary evidence path", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_node", "line", OmValueType.Number, description: "Primary evidence start line", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_node", "assigned_by", OmValueType.String, description: "Judgement provenance: manual | config | heuristic | llm", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_node", "confidence", OmValueType.Number, description: "0-1; required for heuristic/llm judgements", cancellationToken: cancellationToken);

        // --- 11 subtypes with their specific attributes (design §1.1) ---
        await om.DefineTypeAsync("depa_capsule", "Self-contained module unit (capsule-protocol §0)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_capsule", "name", OmValueType.String, cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_capsule", "root_path", OmValueType.String, description: "Directory prefix anchoring ck_file.path membership", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_capsule", "internals_path", OmValueType.String, description: "Internals glob, default **/Internals/**", cancellationToken: cancellationToken);
        await om.DefineComputedAsync("depa_capsule", "entry_count", "Number of exposed entries (computed)", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_contract", "Effect/type contract (Effect dimension + layering)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_contract", "contract_kind", OmValueType.String, description: "effect | types", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_impl", "Contract implementation / core logic unit", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_impl", "purity", OmValueType.String, description: "core (must stay pure) | edge (IO-permitted adapter/bootstrap)", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_reducer", "Deterministic fold function (Data dimension, events to state)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_reducer", "deterministic", OmValueType.Bool, cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_projection", "Grade-6 projection / read model (grade-7 surface folded in via grade)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_projection", "rebuildable", OmValueType.Bool, cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_fact_source", "Node on the fact-source ladder", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_fact_source", "grade", OmValueType.Number, description: "1-7 per fact-source-truth §1", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_fact_source", "grade_id", OmValueType.String, description: "authoritative_fact | domain_canonical_event | runtime_control_fact | append_only_journal | checkpoint_snapshot | derived_projection_cache | surface_view", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_fact_source", "expected_owner", OmValueType.String, description: "Expected single writer (depa entity id or symbol_id)", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_runtime_param", "fn(runtime,input,config) parameter placement verdict", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_runtime_param", "role", OmValueType.String, description: "runtime | input | config", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_runtime_param", "runtime_facet", OmValueType.String, description: "effect_contract | app_resource | mutable_inner_ctx | frozen_outer_ctx | options", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_runtime_param", "declared_type_id", OmValueType.String, description: "symbol_id of the declared parameter type", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_runtime_param", "declared_type", OmValueType.String, description: "raw declared-type text from depa-map.json (V-R1 big-bag lexicon / V-S3 input-type join, track expand-depa-detection-rules T2.1)", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_runtime_carrier", "Runtime data-carrier type (runtime-paradigm §4 invariant 1 subject)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_runtime_carrier", "shape", OmValueType.String, description: "flat | grouped | nested | reactive | faceted", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_entry", "Capsule entry point (run_<capsule> equivalent)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_entry", "entry_kind", OmValueType.String, description: "ck_entry_point.kind vocabulary (public_api/http_route/mcp_tool/main)", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_effect_api", "Whitelisted external side-effect API (design §4)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_effect_api", "target_pattern", OmValueType.String, description: "FQN glob (* single segment, ** any segments)", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_effect_api", "category", OmValueType.String, description: "file_io | network | db | process | console | env | threading | nondeterminism | exempt_contract", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_effect_api", "direction", OmValueType.String, description: "read | write | both", cancellationToken: cancellationToken);

        await om.DefineTypeAsync("depa_violation", "A red-light hit (design §3 output)", parentType: "depa_node", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_violation", "rule_id", OmValueType.String, description: "V-E1, V-D1, ...", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_violation", "verdict", OmValueType.String, description: "GAP | BLOCKED", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_violation", "dimension", OmValueType.String, description: "data | effect | processor | layering | fact_source | actor | overdesign | vendor", cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_violation", "message", OmValueType.String, cancellationToken: cancellationToken);
        await om.DefineAttributeAsync("depa_violation", "evidence_json", OmValueType.Json, description: "path:line evidence array (design §5.2); ck_edge evidence stays a property, not an OM edge", cancellationToken: cancellationToken);

        // --- structural relations (design §2.1) ---
        await om.DefineRelationAsync("capsule_contains", "depa_capsule", "depa_node", description: "Capsule membership", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("capsule_exposes", "depa_capsule", "depa_entry", description: "Public entry; expected cardinality 1 (multi-entry is a V-L4 signal via counting)", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("capsule_depends_on", "depa_capsule", "depa_capsule", description: "Inter-capsule dependency (should be acyclic, one-way)", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("contract_implemented_by", "depa_contract", "depa_impl", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("impl_uses_contract", "depa_impl", "depa_contract", description: "Core logic effects via contract (the compliant path)", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("entry_delegates_to", "depa_entry", "depa_impl", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("runtime_carries", "depa_runtime_carrier", "depa_runtime_param", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("fn_takes", "depa_impl", "depa_runtime_param", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("projection_derived_from", "depa_projection", "depa_fact_source", description: "Single upstream of a projection (derivation chain, one-way)", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("reducer_folds", "depa_reducer", "depa_fact_source", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("fact_written_by", "depa_fact_source", "depa_impl", description: "Observed writer; grade 1-3 expects exactly one (more is V-D1)", cancellationToken: cancellationToken);

        // --- violation-signal relations (design §2.2; evidenced_by_edge stays a property) ---
        await om.DefineRelationAsync("effect_leaks_through", "depa_impl", "depa_effect_api", description: "V-E1: core logic bypasses contracts into direct IO", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("backwrites", "depa_projection", "depa_fact_source", description: "V-S1: projection writes back into a higher-grade upstream", cancellationToken: cancellationToken);
        await om.DefineRelationAsync("violates", "depa_violation", "depa_node", description: "Violation attached to its subject entity", cancellationToken: cancellationToken);

        // --- five Check-mode existential rules (design §2.3) ---
        await om.DefineExistentialRuleAsync(
            "capsule_must_expose_entry",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("depa_capsule"),
                new ExistentialExistsSpec("capsule_exposes", ExistentialDirection.Out, "depa_entry"),
                Message: "GAP: capsule exposes no entry (not capsule-ized)"),
            cancellationToken);
        await om.DefineExistentialRuleAsync(
            "contract_must_have_impl",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("depa_contract", [new ExistentialWhereCondition("contract_kind", "=", JsonSerializer.SerializeToElement("effect"))]),
                new ExistentialExistsSpec("contract_implemented_by", ExistentialDirection.Out, "depa_impl"),
                Message: "GAP: effect contract declared without any implementation"),
            cancellationToken);
        await om.DefineExistentialRuleAsync(
            "projection_must_have_upstream",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("depa_projection"),
                new ExistentialExistsSpec("projection_derived_from", ExistentialDirection.Out, "depa_fact_source"),
                Message: "BLOCKED: projection has no single upstream (cannot grade)"),
            cancellationToken);
        await om.DefineExistentialRuleAsync(
            "factsource_must_have_writer",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("depa_fact_source", [new ExistentialWhereCondition("grade", "<=", JsonSerializer.SerializeToElement(3))]),
                new ExistentialExistsSpec("fact_written_by", ExistentialDirection.Out, "depa_impl"),
                Message: "BLOCKED: grade 1-3 fact source has no observed writer (insufficient observation)"),
            cancellationToken);
        await om.DefineExistentialRuleAsync(
            "violation_must_have_subject",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("depa_violation"),
                new ExistentialExistsSpec("violates", ExistentialDirection.Out, "depa_node"),
                Message: "Pipeline self-check: violations must not dangle"),
            cancellationToken);
    }

    /// <summary>
    /// Materializes the effective whitelist (built-in + optional pre-parsed user rules, first
    /// hit wins) as depa_effect_api entities so the whitelist itself is queryable (design §4.1).
    /// Idempotent: entity id is derived from the pattern, repeated syncs upsert in place.
    /// Takes resolved objects only (track fix-om-depa-conformance-gaps REC-2) — the
    /// depa-effects.json file read lives at the entry layer.
    /// </summary>
    internal static async Task<int> SyncEffectApisAsync(CozoOm om, IReadOnlyList<DepaEffectRule>? userEffects, CancellationToken cancellationToken)
    {
        var merged = DepaEffectCatalog.Merge(userEffects);

        foreach (var rule in merged)
        {
            var id = $"depa:effectapi:{rule.Pattern}";
            await om.UpsertEntityAsync(id, "depa_effect_api", rule.Pattern, cancellationToken);
            await om.SetPropertyAsync(id, "target_pattern", rule.Pattern, cancellationToken: cancellationToken);
            await om.SetPropertyAsync(id, "category", rule.Category, cancellationToken: cancellationToken);
            await om.SetPropertyAsync(id, "direction", rule.Direction, cancellationToken: cancellationToken);
            await om.SetPropertyAsync(id, "assigned_by", "config", cancellationToken: cancellationToken);
            await om.SetPropertyAsync(id, "confidence", 1.0, cancellationToken: cancellationToken);
        }

        return merged.Count;
    }
}
