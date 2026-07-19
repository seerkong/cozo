using System.Text.Json;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologyQualityReportRequest(string OntologyId);

public sealed record BusinessOntologyQualityReport(
    string OntologyId,
    string GenerationId,
    IReadOnlyList<BusinessOntologyRawCarrierQuality> RawImplementationCarriers,
    IReadOnlyList<BusinessOntologyConceptQuality> CanonicalConcepts,
    BusinessOntologyConsolidationQuality Consolidation,
    BusinessOntologyConceptPollutionQuality ConceptPollution,
    BusinessOntologyFieldConstraintQuality FieldConstraints,
    BusinessOntologyCandidateKindQuality BusinessRules,
    BusinessOntologyCandidateKindQuality Relations,
    BusinessOntologyLifecycleQuality Lifecycles,
    BusinessOntologySemanticCoverageQuality SemanticCoverage,
    BusinessOntologyUseCaseEvidenceQuality UseCaseEvidence,
    BusinessOntologyCrossFileEvidenceQuality CrossFileEvidence,
    BusinessOntologyAttributePollutionQuality AttributePollution,
    BusinessOntologyQualityVerdict QualityVerdict);

public sealed record BusinessOntologyRawCarrierQuality(
    string CandidateId,
    string ProposedId,
    string SourceSymbol,
    string SourceName,
    string Role,
    string RoleFamily,
    string CanonicalFamily,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<BusinessOntologyQualityAnchor> RepresentativeAnchors);

public sealed record BusinessOntologyConceptQuality(
    string ConceptId,
    string Label,
    string Kind,
    string Status,
    bool HasTechnicalSuffix,
    string? TechnicalSuffix,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<BusinessOntologyQualityAnchor> RepresentativeAnchors);

public sealed record BusinessOntologyConsolidationQuality(
    int ConceptCount,
    int RawCarrierCount,
    int MappedCarrierCount,
    int UnconsolidatedCarrierCount,
    double CarrierToConceptRatio,
    IReadOnlyList<BusinessOntologyCarriersPerConceptQuality> CarriersPerConcept,
    IReadOnlyList<BusinessOntologyRawCarrierQuality> UnconsolidatedCarriers);

public sealed record BusinessOntologyCarriersPerConceptQuality(
    string ConceptId,
    int CarrierCount,
    IReadOnlyList<string> CarrierCandidateIds);

public sealed record BusinessOntologyConceptPollutionQuality(
    int ConceptCount,
    int PollutedConceptCount,
    double PollutionRatio,
    IReadOnlyList<BusinessOntologyTechnicalSuffixHit> TechnicalSuffixHits);

public sealed record BusinessOntologyTechnicalSuffixHit(
    string ConceptId,
    string Name,
    string Suffix);

public sealed record BusinessOntologyFieldConstraintQuality(
    int Count,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyList<BusinessOntologyQualityAnchor> RepresentativeAnchors);

public sealed record BusinessOntologyCandidateKindQuality(
    int Count,
    int PendingCandidateCount,
    int DirectOnlyDiagnosticCount,
    int CrossFileCoveredCount,
    int BusinessConditionCount,
    IReadOnlyDictionary<string, int> BySemanticKind,
    IReadOnlyList<BusinessOntologyQualityAnchor> RepresentativeAnchors);

public sealed record BusinessOntologyLifecycleQuality(
    int Count,
    int TransitionCount,
    int CrossFileCoveredCount,
    IReadOnlyList<BusinessOntologyQualityAnchor> RepresentativeAnchors);

public sealed record BusinessOntologySemanticCoverageQuality(
    int CandidateCount,
    int DirectEvidenceCoveredCount,
    int UseCaseEvidenceCoveredCount,
    int CrossFileCoveredCount,
    int DirectOnlyRelationDiagnosticCount,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyList<BusinessOntologyQualityAnchor> RepresentativeAnchors);

public sealed record BusinessOntologyUseCaseEvidenceQuality(
    int Count,
    IReadOnlyList<BusinessOntologyQualityAnchor> RepresentativeAnchors);

public sealed record BusinessOntologyCrossFileEvidenceQuality(
    int CandidateCount,
    int CoveredCandidateCount,
    int UnmetCandidateCount,
    IReadOnlyList<BusinessOntologyCrossFileEvidenceItem> UnmetItems);

public sealed record BusinessOntologyCrossFileEvidenceItem(
    string CandidateId,
    string Kind,
    string ProposedId,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Roles);

public sealed record BusinessOntologyQualityAnchor(
    string Path,
    int Line,
    string Symbol,
    string Kind,
    string EvidenceId,
    string Summary);

public sealed record BusinessOntologyAttributePollutionQuality(
    int AttributeCount,
    int PollutedAttributeCount,
    IReadOnlyList<string> PollutedAttributes);

public sealed record BusinessOntologyQualityVerdict(
    string Status,
    IReadOnlyList<BusinessOntologyQualityDiagnostic> Diagnostics);

public sealed record BusinessOntologyQualityDiagnostic(
    string Kind,
    string Severity,
    string Message);

/// <summary>
/// Builds deterministic, machine-readable quality metrics for onto_* generations. The report is
/// a gate over semantic quality, not a replacement for XML validity.
/// </summary>
public sealed class BusinessOntologyQualityReportBuilder(CozoOm om, BusinessOntologyStore store)
{
    public const int MaxRepresentativeAnchors = 8;
    public const double MaxConceptPollutionRatio = 0.2;
    public const double MaxConceptToCarrierRatio = 0.75;

    private static readonly string[] TechnicalSuffixes =
    [
        "DTO", "Dto", "Entity", "VO", "Vo", "Request", "Req", "Response", "Resp",
        "Query", "Save", "Update", "Page", "Form",
    ];

    public async Task<BusinessOntologyQualityReport> BuildAsync(
        BusinessOntologyQualityReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await store.ReadExportableAsync(request.OntologyId, cancellationToken);
        return await BuildAsync(request, snapshot, cancellationToken);
    }

    public async Task<BusinessOntologyQualityReport> BuildAsync(
        BusinessOntologyQualityReportRequest request,
        BusinessOntologySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!StringComparer.Ordinal.Equals(request.OntologyId, snapshot.OntologyId))
        {
            throw new ArgumentException(
                "Quality report snapshot must belong to the requested ontology.",
                nameof(snapshot));
        }
        var evidence = snapshot.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var semanticClaims = await ReadSemanticClaimsAsync(cancellationToken);
        var rawCarriers = RawCarriers(snapshot, evidence);
        var concepts = Concepts(snapshot, evidence);
        var consolidation = Consolidation(snapshot, rawCarriers);
        var pollution = ConceptPollution(concepts);
        var fieldConstraints = FieldConstraints(semanticClaims);
        var ruleCandidates = CandidateKind(snapshot, evidence, semanticClaims, "rule");
        var relationCandidates = CandidateKind(snapshot, evidence, semanticClaims, "relation");
        var lifecycleCandidates = LifecycleKind(snapshot, evidence, semanticClaims);
        var semanticCoverage = SemanticCoverage(snapshot, evidence, semanticClaims);
        var useCaseEvidence = UseCaseEvidence(snapshot, evidence);
        var crossFileEvidence = CrossFileEvidence(snapshot, evidence);
        var attributePollution = AttributePollution(snapshot);
        var relationPollutionDiagnostics = RelationPollutionDiagnostics(snapshot, semanticClaims);
        var directEvidenceMissingCount = SemanticDirectEvidenceMissingCount(snapshot, evidence, semanticClaims);
        var verdict = Verdict(
            rawCarriers,
            concepts,
            consolidation,
            pollution,
            attributePollution,
            fieldConstraints,
            ruleCandidates,
            relationCandidates,
            lifecycleCandidates,
            semanticCoverage,
            crossFileEvidence,
            semanticClaims.RelationAvailable,
            directEvidenceMissingCount,
            relationPollutionDiagnostics);

        return new BusinessOntologyQualityReport(
            snapshot.OntologyId,
            snapshot.GenerationId,
            rawCarriers.Take(MaxRepresentativeAnchors).ToArray(),
            concepts.Take(MaxRepresentativeAnchors).ToArray(),
            consolidation,
            pollution,
            fieldConstraints,
            ruleCandidates,
            relationCandidates,
            lifecycleCandidates,
            semanticCoverage,
            useCaseEvidence,
            crossFileEvidence,
            attributePollution,
            verdict);
    }

    private static IReadOnlyList<BusinessOntologyRawCarrierQuality> RawCarriers(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence)
    {
        return snapshot.Candidates
            .Where(item => item.SubjectKind == "implementation"
                || item.Id.StartsWith("candidate:implementation:", StringComparison.Ordinal))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item =>
            {
                var payload = ParseObject(item.PayloadJson);
                var role = GetString(payload, "role");
                if (role.Length == 0)
                {
                    role = GetString(payload, "kind");
                }
                var sourceName = GetString(payload, "sourceName");
                return new BusinessOntologyRawCarrierQuality(
                    item.Id,
                    item.ProposedId,
                    GetString(payload, "sourceSymbol"),
                    sourceName,
                    role,
                    RoleFamily(role, sourceName, item.ProposedId),
                    GetString(payload, "canonicalFamily"),
                    item.EvidenceIds,
                    Anchors(item.EvidenceIds, evidence, "implementation"));
            })
            .ToArray();
    }

    private static IReadOnlyList<BusinessOntologyConceptQuality> Concepts(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence)
    {
        return snapshot.Concepts
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item =>
            {
                var name = ConceptName(item);
                var suffix = TechnicalSuffix(name);
                return new BusinessOntologyConceptQuality(
                    item.Id,
                    item.Label,
                    item.Kind,
                    item.Status,
                    suffix is not null,
                    suffix,
                    item.EvidenceIds,
                    Anchors(item.EvidenceIds, evidence, "concept"));
            })
            .ToArray();
    }

    private static BusinessOntologyConsolidationQuality Consolidation(
        BusinessOntologySnapshot snapshot,
        IReadOnlyList<BusinessOntologyRawCarrierQuality> rawCarriers)
    {
        var bySourceSymbol = rawCarriers
            .Where(item => item.SourceSymbol.Length > 0)
            .GroupBy(item => item.SourceSymbol, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.CandidateId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var mapped = new Dictionary<string, List<BusinessOntologyRawCarrierQuality>>(StringComparer.Ordinal);
        foreach (var mapping in snapshot.Mappings
            .Where(item => item.SubjectKind == "concept" && item.MappingRole == "representedBy")
            .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!bySourceSymbol.TryGetValue(mapping.Symbol, out var carriers))
            {
                continue;
            }
            if (!mapped.TryGetValue(mapping.SubjectId, out var list))
            {
                list = [];
                mapped.Add(mapping.SubjectId, list);
            }
            foreach (var carrier in carriers)
            {
                if (!list.Any(item => item.CandidateId == carrier.CandidateId))
                {
                    list.Add(carrier);
                }
            }
        }

        var mappedIds = mapped.Values
            .SelectMany(item => item)
            .Select(item => item.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        var unconsolidated = rawCarriers
            .Where(item => !mappedIds.Contains(item.CandidateId))
            .OrderBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToArray();
        var carriersPerConcept = mapped
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new BusinessOntologyCarriersPerConceptQuality(
                item.Key,
                item.Value.Count,
                item.Value
                    .Select(carrier => carrier.CandidateId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

        return new BusinessOntologyConsolidationQuality(
            snapshot.Concepts.Count,
            rawCarriers.Count,
            mappedIds.Count,
            unconsolidated.Length,
            snapshot.Concepts.Count == 0
                ? 0
                : rawCarriers.Count / (double)snapshot.Concepts.Count,
            carriersPerConcept.Take(MaxRepresentativeAnchors).ToArray(),
            unconsolidated.Take(MaxRepresentativeAnchors).ToArray());
    }

    private static BusinessOntologyConceptPollutionQuality ConceptPollution(
        IReadOnlyList<BusinessOntologyConceptQuality> concepts)
    {
        var hits = concepts
            .Where(item => item.HasTechnicalSuffix)
            .Select(item => new BusinessOntologyTechnicalSuffixHit(
                item.ConceptId,
                item.Label.Length == 0 ? item.ConceptId.Split('.').Last() : item.Label,
                item.TechnicalSuffix ?? ""))
            .OrderBy(item => item.ConceptId, StringComparer.Ordinal)
            .ToArray();
        return new BusinessOntologyConceptPollutionQuality(
            concepts.Count,
            hits.Length,
            concepts.Count == 0 ? 0 : hits.Length / (double)concepts.Count,
            hits);
    }

    private static BusinessOntologyFieldConstraintQuality FieldConstraints(
        SemanticClaimReadResult claims)
    {
        var constraints = claims.Claims
            .Where(item => item.Kind is "validation_constraint" or "persistence_constraint")
            .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
            .ToArray();
        return new BusinessOntologyFieldConstraintQuality(
            constraints.Length,
            constraints
                .GroupBy(item => item.Kind, StringComparer.Ordinal)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            constraints
                .Select(item => new BusinessOntologyQualityAnchor(
                    SafeRelativePath(item.Path),
                    item.StartLine,
                    item.SubjectId,
                    item.Kind,
                    item.ClaimId,
                    ClaimSummary(item)))
                .Take(MaxRepresentativeAnchors)
                .ToArray());
    }

    private static BusinessOntologyCandidateKindQuality CandidateKind(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence,
        SemanticClaimReadResult semanticClaims,
        string kind)
    {
        var pendingCandidates = SemanticCandidates(snapshot, kind);
        var directOnlyCandidates = kind == "relation"
            ? pendingCandidates.Where(item => IsDirectOnlyRelationCandidate(item, evidence)).ToArray()
            : [];
        var candidates = kind == "relation"
            ? pendingCandidates.Where(item => !IsDirectOnlyRelationCandidate(item, evidence)).ToArray()
            : pendingCandidates;
        var bySemanticKind = candidates
            .Select(item => SemanticKind(item, kind))
            .GroupBy(item => item, StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new BusinessOntologyCandidateKindQuality(
            candidates.Length,
            pendingCandidates.Length,
            directOnlyCandidates.Length,
            candidates.Count(item => HasCrossFileEvidence(item, evidence)),
            candidates.Count(item => SemanticKind(item, kind) == "businessCondition"),
            bySemanticKind,
            candidates
                .SelectMany(item => CandidateAnchors(item, evidence, semanticClaims))
                .Take(MaxRepresentativeAnchors)
                .ToArray());
    }

    private static BusinessOntologyLifecycleQuality LifecycleKind(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence,
        SemanticClaimReadResult semanticClaims)
    {
        var candidates = SemanticCandidates(snapshot, "lifecycle");
        return new BusinessOntologyLifecycleQuality(
            candidates.Length,
            candidates.Sum(TransitionCount),
            candidates.Count(item => HasCrossFileEvidence(item, evidence)),
            candidates
                .SelectMany(item => CandidateAnchors(item, evidence, semanticClaims))
                .Take(MaxRepresentativeAnchors)
                .ToArray());
    }

    private static BusinessOntologySemanticCoverageQuality SemanticCoverage(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence,
        SemanticClaimReadResult semanticClaims)
    {
        var candidates = SemanticCandidates(snapshot, "relation")
            .Concat(SemanticCandidates(snapshot, "rule"))
            .Concat(SemanticCandidates(snapshot, "lifecycle"))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var byKind = candidates
            .Select(CandidateKindName)
            .GroupBy(item => item, StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new BusinessOntologySemanticCoverageQuality(
            candidates.Length,
            candidates.Count(item => HasDirectCandidateEvidence(item, evidence, semanticClaims)),
            candidates.Count(item => HasUseCaseEvidence(item, evidence)),
            candidates.Count(item => HasCrossFileEvidence(item, evidence)),
            candidates.Count(IsMarkedDirectOnlyRelationCandidate),
            byKind,
            candidates
                .SelectMany(item => CandidateAnchors(item, evidence, semanticClaims))
                .Take(MaxRepresentativeAnchors)
                .ToArray());
    }

    private static BusinessOntologyUseCaseEvidenceQuality UseCaseEvidence(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence)
    {
        var items = snapshot.Evidence
            .Where(IsUseCaseEvidence)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return new BusinessOntologyUseCaseEvidenceQuality(
            items.Length,
            items
                .Select(item => Anchor(item, "use-case"))
                .Take(MaxRepresentativeAnchors)
                .ToArray());
    }

    private static BusinessOntologyCrossFileEvidenceQuality CrossFileEvidence(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence)
    {
        var candidates = SemanticCandidates(snapshot, "relation")
            .Concat(SemanticCandidates(snapshot, "rule"))
            .Concat(SemanticCandidates(snapshot, "lifecycle"))
            .Where(item => !IsDirectOnlyRelationCandidate(item, evidence))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var unmetCandidates = candidates
            .Where(item => !HasCrossFileEvidence(item, evidence))
            .ToArray();
        var unmetItems = unmetCandidates
            .Select(item =>
            {
                var anchors = item.EvidenceIds
                    .Where(evidence.ContainsKey)
                    .Select(id => evidence[id])
                    .ToArray();
                return new BusinessOntologyCrossFileEvidenceItem(
                    item.Id,
                    CandidateKindName(item),
                    item.ProposedId,
                    item.EvidenceIds,
                    anchors.Select(anchor => SafeRelativePath(anchor.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    anchors.Select(anchor => EvidenceRole(anchor)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            })
            .Take(MaxRepresentativeAnchors)
            .ToArray();
        return new BusinessOntologyCrossFileEvidenceQuality(
            candidates.Length,
            candidates.Count(item => HasCrossFileEvidence(item, evidence)),
            unmetCandidates.Length,
            unmetItems);
    }

    private static BusinessOntologyAttributePollutionQuality AttributePollution(
        BusinessOntologySnapshot snapshot)
    {
        var polluted = snapshot.Attributes
            .Select(item => item.Name)
            .Where(IsImplementationAttributeName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new BusinessOntologyAttributePollutionQuality(
            snapshot.Attributes.Count,
            polluted.Length,
            polluted);
    }

    private static BusinessOntologyQualityVerdict Verdict(
        IReadOnlyList<BusinessOntologyRawCarrierQuality> rawCarriers,
        IReadOnlyList<BusinessOntologyConceptQuality> concepts,
        BusinessOntologyConsolidationQuality consolidation,
        BusinessOntologyConceptPollutionQuality pollution,
        BusinessOntologyAttributePollutionQuality attributePollution,
        BusinessOntologyFieldConstraintQuality fieldConstraints,
        BusinessOntologyCandidateKindQuality rules,
        BusinessOntologyCandidateKindQuality relations,
        BusinessOntologyLifecycleQuality lifecycles,
        BusinessOntologySemanticCoverageQuality semanticCoverage,
        BusinessOntologyCrossFileEvidenceQuality crossFileEvidence,
        bool semanticClaimRelationAvailable,
        int directEvidenceMissingCount,
        IReadOnlyList<BusinessOntologyQualityDiagnostic> relationPollutionDiagnostics)
    {
        var diagnostics = new List<BusinessOntologyQualityDiagnostic>();
        diagnostics.AddRange(relationPollutionDiagnostics);
        if (!semanticClaimRelationAvailable)
        {
            diagnostics.Add(new(
                "semantic_claims_unavailable",
                "warning",
                "ck_semantic_claim is unavailable, so field-constraint quality cannot be audited from source claims."));
        }
        if (rawCarriers.Count > 0
            && concepts.Count / (double)rawCarriers.Count > MaxConceptToCarrierRatio)
        {
            diagnostics.Add(new(
                "concept_consolidation_weak",
                "error",
                "Canonical concept count is not significantly smaller than raw implementation carrier count."));
        }
        if (pollution.PollutionRatio > MaxConceptPollutionRatio)
        {
            diagnostics.Add(new(
                "concept_pollution_high",
                "error",
                "Canonical concept names still contain technical implementation suffixes above the fixed quality threshold."));
        }
        if (attributePollution.PollutedAttributeCount > 0)
        {
            diagnostics.Add(new(
                "attribute_pollution_present",
                "error",
                "Canonical concept attributes still include implementation/cache/lock/list carrier fields."));
        }
        if (fieldConstraints.Count > 0 && rules.Count == 0)
        {
            diagnostics.Add(new(
                "field_constraints_without_business_rules",
                "error",
                "Field constraints exist, but no semantic candidate business rule was found; validation annotations are not businessRules."));
        }
        if (relations.Count + rules.Count + lifecycles.Count + lifecycles.TransitionCount == 0)
        {
            diagnostics.Add(new(
                "business_semantics_absent",
                "error",
                "No semantic relation, rule, lifecycle, or transition candidates are present."));
        }
        if (directEvidenceMissingCount > 0)
        {
            diagnostics.Add(new(
                "semantic_direct_evidence_missing",
                "error",
                "At least one semantic candidate lacks the direct source claim required for its relation/rule/lifecycle kind."));
        }
        if (lifecycles.Count > 0 && lifecycles.TransitionCount == 0)
        {
            diagnostics.Add(new(
                "lifecycle_transitions_absent",
                "error",
                "Lifecycle candidates exist, but no guarded state transition was reconstructed."));
        }
        if (crossFileEvidence.UnmetCandidateCount > 0)
        {
            diagnostics.Add(new(
                "cross_file_evidence_missing",
                "error",
                "At least one relation/rule/lifecycle candidate lacks cross-file, cross-role, or use-case slice evidence."));
        }
        if (consolidation.UnconsolidatedCarrierCount > consolidation.MappedCarrierCount)
        {
            diagnostics.Add(new(
                "unconsolidated_carriers_high",
                "warning",
                "More implementation carriers remain unconsolidated than mapped to canonical concepts."));
        }

        return new BusinessOntologyQualityVerdict(
            diagnostics.Any(item => item.Severity == "error") ? "GAP" : "PASS",
            diagnostics.OrderBy(item => item.Kind, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<BusinessOntologyQualityDiagnostic> RelationPollutionDiagnostics(
        BusinessOntologySnapshot snapshot,
        SemanticClaimReadResult semanticClaims)
    {
        var diagnostics = new List<BusinessOntologyQualityDiagnostic>();
        var relations = SemanticCandidates(snapshot, "relation")
            .Select(RelationShape)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        if (relations.Any(item => item.FromConceptId == item.ToConceptId))
        {
            diagnostics.Add(new(
                "relation_self_pollution",
                "error",
                "Relation candidates include self-relations, which indicates code-flow artifacts rather than business object structure."));
        }
        if (relations.Any(item => IsOperationLikeRelationName(item.Name)))
        {
            diagnostics.Add(new(
                "relation_operation_name_pollution",
                "error",
                "Relation candidate names include operation-like method signatures such as query/find/get/load."));
        }
        var structuralTypedReferences = semanticClaims.Claims
            .Where(item => item.Kind == "typed_reference")
            .Select(item => ParseObject(item.PayloadJson))
            .Count(IsStructuralTypedReference);
        if (relations.Length > 0
            && structuralTypedReferences > 0
            && relations.Length > structuralTypedReferences * 2)
        {
            diagnostics.Add(new(
                "relation_structural_inflation",
                "error",
                "Relation candidates substantially exceed direct structural field/record typed references."));
        }
        if (snapshot.Concepts.Count > 0
            && relations.Length > snapshot.Concepts.Count * 4
            && relations.Length > 50)
        {
            diagnostics.Add(new(
                "relation_concept_ratio_inflated",
                "error",
                "Relation candidate count is abnormally high relative to canonical concept count."));
        }
        return diagnostics
            .GroupBy(item => item.Kind, StringComparer.Ordinal)
            .Select(item => item.First())
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<SemanticClaimReadResult> ReadSemanticClaimsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await om.Runtime.Store.RunAsync(
                """
                ?[claim_id, subject_id, kind, payload_json, path, start_line, end_line, resolver, evidence] :=
                    *ck_semantic_claim{
                        claim_id,
                        subject_id,
                        kind,
                        payload_json,
                        file_id,
                        start_line,
                        end_line,
                        confidence: _,
                        resolver,
                        evidence
                    },
                    *ck_file{file_id, path}
                """,
                cancellationToken: cancellationToken);
            return new SemanticClaimReadResult(
                true,
                rows.Rows
                    .Select(row => new SemanticClaimQualityFact(
                        S(row, 0),
                        S(row, 1),
                        S(row, 2),
                        S(row, 3),
                        S(row, 4),
                        I(row, 5),
                        I(row, 6),
                        S(row, 7),
                        S(row, 8)))
                    .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
                    .ToArray());
        }
        catch
        {
            return new SemanticClaimReadResult(false, []);
        }
    }

    private static BusinessOntologyCandidate[] SemanticCandidates(
        BusinessOntologySnapshot snapshot,
        string kind) =>
        snapshot.Candidates
            .Where(item => CandidateKindName(item) == kind)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    private static string CandidateKindName(BusinessOntologyCandidate candidate)
    {
        if (candidate.SubjectKind is "relation" or "rule" or "lifecycle")
        {
            return candidate.SubjectKind;
        }
        var root = ParseObject(candidate.PayloadJson);
        return GetString(root, "kind");
    }

    private static IReadOnlyList<BusinessOntologyQualityAnchor> CandidateAnchors(
        BusinessOntologyCandidate candidate,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence,
        SemanticClaimReadResult semanticClaims)
    {
        var candidateKind = CandidateKindName(candidate);
        var kind = SemanticKind(candidate, candidateKind);
        var anchors = candidate.EvidenceIds
            .Where(evidence.ContainsKey)
            .Select(id => evidence[id])
            .ToArray();
        return anchors
            .Where(item => IsDirectCandidateEvidence(candidateKind, item, semanticClaims))
            .OrderBy(item => DirectSemanticAnchorPriority(candidateKind, item, semanticClaims))
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => Anchor(item, kind))
            .ToArray();
    }

    private static IReadOnlyList<BusinessOntologyQualityAnchor> Anchors(
        IReadOnlyList<string> evidenceIds,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence,
        string kind) =>
        evidenceIds
            .Where(evidence.ContainsKey)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => Anchor(evidence[id], kind))
            .Take(MaxRepresentativeAnchors)
            .ToArray();

    private static BusinessOntologyQualityAnchor Anchor(
        BusinessOntologyEvidence item,
        string kind) =>
        new(
            SafeRelativePath(item.Path),
            Math.Max(1, item.StartLine),
            item.Symbol,
            kind,
            item.Id,
            item.Summary);

    private static RelationQualityShape? RelationShape(BusinessOntologyCandidate candidate)
    {
        var root = ParseObject(candidate.PayloadJson);
        if (!root.TryGetProperty("semantic", out var semantic)
            || semantic.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var name = GetString(semantic, "name");
        return name.Length == 0
            ? null
            : new RelationQualityShape(
                GetString(semantic, "fromConceptId"),
                GetString(semantic, "toConceptId"),
                name);
    }

    private static bool IsStructuralTypedReference(JsonElement payload)
    {
        var usageKind = GetString(payload, "memberKind");
        if (usageKind.Length == 0)
        {
            usageKind = GetString(payload, "usageKind");
        }
        if (usageKind.Length == 0)
        {
            usageKind = GetString(payload, "referenceKind");
        }
        return usageKind is "field" or "record_component";
    }

    private static bool IsOperationLikeRelationName(string name)
    {
        if (name.StartsWith("get", StringComparison.Ordinal)
            || name.StartsWith("set", StringComparison.Ordinal)
            || name.StartsWith("query", StringComparison.Ordinal)
            || name.StartsWith("find", StringComparison.Ordinal)
            || name.StartsWith("load", StringComparison.Ordinal)
            || name.StartsWith("list", StringComparison.Ordinal)
            || name.StartsWith("select", StringComparison.Ordinal)
            || name.StartsWith("save", StringComparison.Ordinal)
            || name.StartsWith("update", StringComparison.Ordinal)
            || name.StartsWith("delete", StringComparison.Ordinal)
            || name.StartsWith("remove", StringComparison.Ordinal)
            || name.StartsWith("export", StringComparison.Ordinal)
            || name.StartsWith("import", StringComparison.Ordinal)
            || name.StartsWith("create", StringComparison.Ordinal))
        {
            return true;
        }
        return name.EndsWith("ById", StringComparison.Ordinal)
            || name.EndsWith("ByKey", StringComparison.Ordinal);
    }

    private static string SemanticKind(BusinessOntologyCandidate candidate, string fallback)
    {
        var root = ParseObject(candidate.PayloadJson);
        if (fallback == "rule")
        {
            if (root.TryGetProperty("semantic", out var semantic)
                && semantic.ValueKind == JsonValueKind.Object)
            {
                var ruleKind = GetString(semantic, "ruleKind");
                return ruleKind.Length == 0 ? "rule" : ruleKind;
            }
            return "rule";
        }
        return fallback;
    }

    private static int TransitionCount(BusinessOntologyCandidate candidate)
    {
        var root = ParseObject(candidate.PayloadJson);
        if (!root.TryGetProperty("semantic", out var semantic)
            || semantic.ValueKind != JsonValueKind.Object
            || !semantic.TryGetProperty("transitions", out var transitions)
            || transitions.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        return transitions.GetArrayLength();
    }

    private static bool HasCrossFileEvidence(
        BusinessOntologyCandidate candidate,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence)
    {
        if (IsDirectOnlyRelationCandidate(candidate, evidence))
        {
            return false;
        }
        var anchors = candidate.EvidenceIds
            .Where(evidence.ContainsKey)
            .Select(id => evidence[id])
            .ToArray();
        if (anchors.Any(IsUseCaseEvidence))
        {
            return true;
        }
        return anchors
            .Select(item => SafeRelativePath(item.Path))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count() >= 2
            || anchors
                .Select(EvidenceRole)
                .Where(role => role.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count() >= 2;
    }

    private static bool HasUseCaseEvidence(
        BusinessOntologyCandidate candidate,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence) =>
        candidate.EvidenceIds
            .Where(evidence.ContainsKey)
            .Select(id => evidence[id])
            .Any(IsUseCaseEvidence);

    private static bool IsUseCaseEvidence(BusinessOntologyEvidence item) =>
        item.Id.StartsWith("usecase:", StringComparison.Ordinal)
        || item.SourceKind.Contains("usecase", StringComparison.OrdinalIgnoreCase)
        || item.SourceKind.Contains("use-case", StringComparison.OrdinalIgnoreCase);

    private static bool IsMarkedDirectOnlyRelationCandidate(BusinessOntologyCandidate candidate) =>
        CandidateKindName(candidate) == "relation"
        && candidate.Reason.Contains("direct-only diagnostic", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectOnlyRelationCandidate(
        BusinessOntologyCandidate candidate,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence) =>
        CandidateKindName(candidate) == "relation"
        && (IsMarkedDirectOnlyRelationCandidate(candidate)
            || !HasUseCaseEvidence(candidate, evidence));

    private static int DirectSemanticAnchorPriority(
        string candidateKind,
        BusinessOntologyEvidence item,
        SemanticClaimReadResult semanticClaims)
    {
        var claimKind = DirectClaimKind(item, semanticClaims);
        if (candidateKind == "lifecycle")
        {
            return claimKind switch
            {
                CodeSemanticClaimKinds.StateAssignment => 0,
                CodeSemanticClaimKinds.StateField => 1,
                CodeSemanticClaimKinds.StateValue => 2,
                _ => 3,
            };
        }
        if (candidateKind == "rule")
        {
            return claimKind == CodeSemanticClaimKinds.BusinessGuard ? 0 : 1;
        }
        if (candidateKind == "relation")
        {
            return claimKind == CodeSemanticClaimKinds.TypedReference ? 0 : 1;
        }
        return 0;
    }

    private static int SemanticDirectEvidenceMissingCount(
        BusinessOntologySnapshot snapshot,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence,
        SemanticClaimReadResult semanticClaims) =>
        SemanticCandidates(snapshot, "relation")
            .Concat(SemanticCandidates(snapshot, "rule"))
            .Concat(SemanticCandidates(snapshot, "lifecycle"))
            .Count(item => !HasDirectCandidateEvidence(item, evidence, semanticClaims));

    private static bool HasDirectCandidateEvidence(
        BusinessOntologyCandidate candidate,
        IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence,
        SemanticClaimReadResult semanticClaims) =>
        candidate.EvidenceIds
            .Where(evidence.ContainsKey)
            .Select(id => evidence[id])
            .Any(item => IsDirectCandidateEvidence(CandidateKindName(candidate), item, semanticClaims));

    private static bool IsDirectCandidateEvidence(
        string candidateKind,
        BusinessOntologyEvidence item,
        SemanticClaimReadResult semanticClaims)
    {
        var claimKind = DirectClaimKind(item, semanticClaims);
        return candidateKind switch
        {
            "relation" => claimKind == CodeSemanticClaimKinds.TypedReference
                && IsStructuralTypedReferenceEvidence(item, semanticClaims),
            "rule" => claimKind == CodeSemanticClaimKinds.BusinessGuard,
            "lifecycle" => claimKind is CodeSemanticClaimKinds.StateField
                or CodeSemanticClaimKinds.StateValue
                or CodeSemanticClaimKinds.StateAssignment,
            _ => false,
        };
    }

    private static string DirectClaimKind(
        BusinessOntologyEvidence item,
        SemanticClaimReadResult semanticClaims)
    {
        var claim = semanticClaims.Claims.FirstOrDefault(claim =>
            StringComparer.Ordinal.Equals(claim.ClaimId, item.Id));
        if (claim is not null)
        {
            return claim.Kind;
        }
        if (!item.SourceKind.Equals("code-semantic-claim", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        foreach (var kind in CodeSemanticClaimKinds.All.Order(StringComparer.Ordinal))
        {
            if (item.Summary.Contains(kind, StringComparison.Ordinal))
            {
                return kind;
            }
        }
        return "";
    }

    private static bool IsStructuralTypedReferenceEvidence(
        BusinessOntologyEvidence item,
        SemanticClaimReadResult semanticClaims)
    {
        var claim = semanticClaims.Claims.FirstOrDefault(claim =>
            StringComparer.Ordinal.Equals(claim.ClaimId, item.Id));
        if (claim is not null)
        {
            return IsStructuralTypedReference(ParseObject(claim.PayloadJson));
        }
        return item.Summary.Contains("memberKind=field", StringComparison.Ordinal)
            || item.Summary.Contains("usageKind=field", StringComparison.Ordinal)
            || item.Summary.Contains("referenceKind=field", StringComparison.Ordinal)
            || item.Summary.Contains("memberKind=record_component", StringComparison.Ordinal)
            || item.Summary.Contains("usageKind=record_component", StringComparison.Ordinal)
            || item.Summary.Contains("referenceKind=record_component", StringComparison.Ordinal);
    }

    private static string EvidenceRole(BusinessOntologyEvidence item) =>
        string.IsNullOrWhiteSpace(item.SourceKind) ? item.Grade : item.SourceKind;

    private static string RoleFamily(string role, string sourceName, string proposedId)
    {
        var material = (role + " " + sourceName + " " + proposedId).ToLowerInvariant();
        if (material.Contains("controller", StringComparison.Ordinal)) return "controller";
        if (material.Contains("service", StringComparison.Ordinal)) return "service";
        if (material.Contains("repository", StringComparison.Ordinal)) return "repository";
        if (material.Contains("entity", StringComparison.Ordinal)) return "entity";
        if (material.Contains("request", StringComparison.Ordinal) || material.Contains("query", StringComparison.Ordinal) || material.Contains("save", StringComparison.Ordinal) || material.Contains("update", StringComparison.Ordinal)) return "request";
        if (material.Contains("response", StringComparison.Ordinal)) return "response";
        if (material.Contains("dto", StringComparison.Ordinal)) return "dto";
        if (material.Contains("vo", StringComparison.Ordinal)) return "vo";
        if (material.Contains("route", StringComparison.Ordinal)) return "route";
        return role.Length == 0 ? "unknown" : role;
    }

    private static string? TechnicalSuffix(string value) =>
        TechnicalSuffixes.FirstOrDefault(suffix =>
            value.EndsWith(suffix, StringComparison.Ordinal)
            && value.Length > suffix.Length);

    private static bool IsImplementationAttributeName(string value)
    {
        var lower = value.ToLowerInvariant();
        return value.Equals("serialVersionUID", StringComparison.Ordinal)
            || lower.Contains("cache", StringComparison.Ordinal)
            || lower.Contains("lock", StringComparison.Ordinal)
            || lower.EndsWith("entitylist", StringComparison.Ordinal)
            || lower.EndsWith("entities", StringComparison.Ordinal)
            || lower.EndsWith("dtolist", StringComparison.Ordinal)
            || lower.EndsWith("volist", StringComparison.Ordinal)
            || lower.EndsWith("list", StringComparison.Ordinal) && lower.Contains("data", StringComparison.Ordinal);
    }

    private static string ConceptName(BusinessOntologyConcept item) =>
        string.IsNullOrWhiteSpace(item.Label)
            ? item.Id.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? item.Id
            : item.Label;

    private static JsonElement ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return "";
        }
        return property.GetString() ?? "";
    }

    private static string ClaimSummary(SemanticClaimQualityFact item)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return item.Evidence;
        }
        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var member = GetString(document.RootElement, "member");
            var annotation = GetString(document.RootElement, "annotation");
            return string.Join(
                " ",
                new[] { annotation, member, item.Evidence }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (JsonException)
        {
            return item.Evidence;
        }
    }

    private static string SafeRelativePath(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            return "";
        }
        if (!Path.IsPathRooted(normalized))
        {
            return normalized.TrimStart('/');
        }
        return Path.GetFileName(normalized);
    }

    private static string S(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.String
            ? row[index].GetString() ?? ""
            : row[index].ToString();

    private static int I(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.Number
            ? row[index].GetInt32()
            : 0;

    private sealed record SemanticClaimReadResult(
        bool RelationAvailable,
        IReadOnlyList<SemanticClaimQualityFact> Claims);

    private sealed record SemanticClaimQualityFact(
        string ClaimId,
        string SubjectId,
        string Kind,
        string PayloadJson,
        string Path,
        int StartLine,
        int EndLine,
        string Resolver,
        string Evidence);

    private sealed record RelationQualityShape(
        string FromConceptId,
        string ToConceptId,
        string Name);
}
