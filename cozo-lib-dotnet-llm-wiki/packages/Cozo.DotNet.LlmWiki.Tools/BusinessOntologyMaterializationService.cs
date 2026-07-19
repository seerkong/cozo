using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologyMaterializationResult(
    string OntologyId,
    string GenerationId,
    int Relations,
    int Rules,
    int Lifecycles,
    int States,
    int Transitions,
    int StaleReviews);

/// <summary>
/// Promotes only locally valid, effectively accepted semantic candidates.
/// Reviews remain append-only and model output is never treated as direct evidence.
/// </summary>
public sealed class BusinessOntologyMaterializationService
{
    private readonly BusinessOntologyStore store;
    private readonly OntologySemanticCandidateValidator validator;

    public BusinessOntologyMaterializationService(
        BusinessOntologyStore store,
        OntologySemanticCandidateValidator? validator = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.validator = validator ?? new OntologySemanticCandidateValidator();
    }

    public async Task<BusinessOntologyMaterializationResult> MaterializeAsync(
        string ontologyId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await store.ReadExportableAsync(ontologyId, cancellationToken);
        var history = await store.ReadReviewHistoryAsync(
            ontologyId,
            cancellationToken: cancellationToken);
        var candidates = snapshot.Candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var evidence = snapshot.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var conceptIds = snapshot.Concepts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var latestReviews = history
            .GroupBy(item => item.Review.CandidateId, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.Review.ReviewedAt, StringComparer.Ordinal)
                .ThenBy(item => item.Review.Id, StringComparer.Ordinal)
                .Last())
            .OrderBy(item => item.Review.CandidateId, StringComparer.Ordinal)
            .ToArray();

        var expectations = new List<BusinessOntologyMaterializationExpectation>();
        var materializations = new List<BusinessOntologyMaterializationRecord>();
        var relations = new List<BusinessOntologyRelation>();
        var rules = new List<BusinessOntologyRule>();
        var lifecycles = new List<BusinessOntologyLifecycle>();
        var states = new List<BusinessOntologyState>();
        var transitions = new List<BusinessOntologyTransition>();
        var diagnostics = new List<BusinessOntologyDiagnostic>();
        var candidateStatuses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var reviewEntry in latestReviews)
        {
            var review = reviewEntry.Review;
            candidates.TryGetValue(review.CandidateId, out var candidate);
            var currentEvidence = candidate?.EvidenceIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray() ?? [];
            var expectedEvidence = reviewEntry.ExpectedEvidenceIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            expectations.Add(new BusinessOntologyMaterializationExpectation(
                review.CandidateId,
                candidate?.PayloadJson,
                currentEvidence,
                review.Id,
                review.Decision,
                expectedEvidence));

            if (candidate is null)
            {
                if (review.Decision == "accepted")
                {
                    diagnostics.Add(StaleDiagnostic(
                        reviewEntry,
                        "candidate_absent",
                        currentEvidence,
                        "已接受候选在当前 generation 中不存在，需要重新审核。"));
                }
                continue;
            }

            if (!currentEvidence.SequenceEqual(expectedEvidence, StringComparer.Ordinal))
            {
                if (review.Decision == "accepted")
                {
                    diagnostics.Add(StaleDiagnostic(
                        reviewEntry,
                        "evidence_changed",
                        currentEvidence,
                        "已接受候选的直接证据集合已经变化，需要重新审核。"));
                }
                continue;
            }

            if (review.Decision is "rejected" or "superseded")
            {
                candidateStatuses[candidate.Id] = review.Decision;
                continue;
            }
            if (review.Decision != "accepted")
            {
                continue;
            }

            var unavailableEvidence = currentEvidence
                .Where(id => !evidence.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (unavailableEvidence.Length > 0)
            {
                diagnostics.Add(StaleDiagnostic(
                    reviewEntry,
                    "evidence_absent",
                    currentEvidence,
                    "已接受候选引用的直接证据在当前 generation 中不存在，需要重新审核。"));
                continue;
            }
            if (currentEvidence.Any(id => IsModelEvidence(evidence[id])))
            {
                diagnostics.Add(StaleDiagnostic(
                    reviewEntry,
                    "model_evidence_forbidden",
                    currentEvidence,
                    "已接受候选包含模型或提供商来源，不能作为直接证据，需要重新审核。"));
                continue;
            }

            ValidatedOntologySemanticCandidate validated;
            try
            {
                validated = validator.Validate(candidate.PayloadJson, conceptIds, evidence.Keys);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
            {
                diagnostics.Add(StaleDiagnostic(
                    reviewEntry,
                    "payload_invalid",
                    currentEvidence,
                    "已接受候选的语义载荷不再满足当前本地契约，需要重新审核。"));
                continue;
            }
            if (!StringComparer.Ordinal.Equals(candidate.Id, validated.Id)
                || !StringComparer.Ordinal.Equals(candidate.PayloadJson, validated.CanonicalPayloadJson)
                || !StringComparer.Ordinal.Equals(candidate.SubjectKind, validated.Kind)
                || !StringComparer.Ordinal.Equals(candidate.ProposedId, validated.SemanticId)
                || !currentEvidence.SequenceEqual(validated.EvidenceIds, StringComparer.Ordinal))
            {
                diagnostics.Add(StaleDiagnostic(
                    reviewEntry,
                    "candidate_identity_changed",
                    currentEvidence,
                    "已接受候选的 identity、canonical payload 或直接证据不再一致，需要重新审核。"));
                continue;
            }

            if (validated.Kind == OntologySemanticCandidateKinds.Rule
                && TryGetUnsupportedRuleProjection(
                    validated,
                    out var unsupported))
            {
                diagnostics.Add(UnsupportedRuleDiagnostic(
                    reviewEntry,
                    validated,
                    unsupported!));
                continue;
            }

            Materialize(
                candidate,
                validated,
                review,
                relations,
                rules,
                lifecycles,
                states,
                transitions,
                materializations);
            candidateStatuses[candidate.Id] = "accepted";
        }

        EnsureUniqueMaterializedSubjects(materializations);
        await store.ApplyMaterializationAsync(
            new BusinessOntologyMaterializationBatch(
                snapshot.OntologyId,
                snapshot.GenerationId,
                expectations,
                materializations,
                relations,
                rules,
                lifecycles,
                states,
                transitions,
                diagnostics,
                candidateStatuses),
            cancellationToken);

        return new BusinessOntologyMaterializationResult(
            snapshot.OntologyId,
            snapshot.GenerationId,
            relations.Count,
            rules.Count,
            lifecycles.Count,
            states.Count,
            transitions.Count,
            diagnostics.Count(item => item.Kind == "stale_review"));
    }

    private static void Materialize(
        BusinessOntologyCandidate candidate,
        ValidatedOntologySemanticCandidate validated,
        BusinessOntologyReview review,
        ICollection<BusinessOntologyRelation> relations,
        ICollection<BusinessOntologyRule> rules,
        ICollection<BusinessOntologyLifecycle> lifecycles,
        ICollection<BusinessOntologyState> states,
        ICollection<BusinessOntologyTransition> transitions,
        ICollection<BusinessOntologyMaterializationRecord> materializations)
    {
        using var document = JsonDocument.Parse(validated.CanonicalPayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        switch (validated.Kind)
        {
            case OntologySemanticCandidateKinds.Relation:
                relations.Add(new BusinessOntologyRelation(
                    validated.SemanticId,
                    String(semantic, "name"),
                    String(semantic, "fromConceptId"),
                    String(semantic, "toConceptId"),
                    true,
                    String(semantic, "min"),
                    String(semantic, "max") == "many" ? "*" : String(semantic, "max"),
                    String(semantic, "descriptionZh"),
                    "accepted",
                    candidate.Confidence,
                    validated.EvidenceIds));
                break;

            case OntologySemanticCandidateKinds.Rule:
                rules.Add(new BusinessOntologyRule(
                    validated.SemanticId,
                    String(semantic, "subjectConceptId"),
                    String(semantic, "ruleKind"),
                    String(semantic, "descriptionZh"),
                    OntologySemanticJson.Canonicalize(semantic.GetProperty("predicate").GetRawText()),
                    OntologySemanticJson.Canonicalize(semantic.GetProperty("effect").GetRawText()),
                    "accepted",
                    candidate.Confidence,
                    validated.EvidenceIds));
                break;

            case OntologySemanticCandidateKinds.Lifecycle:
                lifecycles.Add(new BusinessOntologyLifecycle(
                    validated.SemanticId,
                    String(semantic, "subjectConceptId"),
                    String(semantic, "stateProperty"),
                    String(semantic, "initialState"),
                    String(semantic, "descriptionZh"),
                    "accepted",
                    candidate.Confidence,
                    validated.EvidenceIds));
                foreach (var state in semantic.GetProperty("states").EnumerateArray())
                {
                    states.Add(new BusinessOntologyState(
                        validated.SemanticId,
                        String(state, "id"),
                        state.GetProperty("terminal").GetBoolean(),
                        String(state, "descriptionZh"),
                        validated.EvidenceIds));
                }
                foreach (var transition in semantic.GetProperty("transitions").EnumerateArray())
                {
                    transitions.Add(new BusinessOntologyTransition(
                        String(transition, "id"),
                        validated.SemanticId,
                        String(transition, "action"),
                        String(transition, "fromState"),
                        String(transition, "toState"),
                        String(transition, "descriptionZh"),
                        OntologySemanticJson.Canonicalize(transition.GetProperty("guard").GetRawText()),
                        OntologySemanticJson.Canonicalize(transition.GetProperty("effect").GetRawText()),
                        "accepted",
                        candidate.Confidence,
                        validated.EvidenceIds));
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported semantic candidate kind '{validated.Kind}'.");
        }

        materializations.Add(new BusinessOntologyMaterializationRecord(
            candidate.Id,
            validated.Kind,
            validated.SemanticId,
            review.Id,
            validated.CanonicalPayloadJson));
    }

    private static BusinessOntologyDiagnostic StaleDiagnostic(
        BusinessOntologyReviewEntry reviewEntry,
        string reasonCode,
        IReadOnlyList<string> currentEvidenceIds,
        string message)
    {
        var details = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
        {
            candidateId = reviewEntry.Review.CandidateId,
            reviewId = reviewEntry.Review.Id,
            reasonCode,
            expectedEvidenceIds = reviewEntry.ExpectedEvidenceIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            currentEvidenceIds = currentEvidenceIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
        }));
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(details)))
            .ToLowerInvariant();
        return new BusinessOntologyDiagnostic(
            "diagnostic:stale-review:" + digest,
            "stale_review",
            "candidate",
            reviewEntry.Review.CandidateId,
            message,
            details,
            "warning");
    }

    private static bool TryGetUnsupportedRuleProjection(
        ValidatedOntologySemanticCandidate validated,
        out BusinessOntologyUnsupportedRuleProjection? unsupported)
    {
        using var document = JsonDocument.Parse(validated.CanonicalPayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        return !BusinessOntologyRuleXmlProjector.TryProject(
            String(semantic, "ruleKind"),
            OntologySemanticJson.Canonicalize(
                semantic.GetProperty("predicate").GetRawText()),
            out _,
            out unsupported);
    }

    private static BusinessOntologyDiagnostic UnsupportedRuleDiagnostic(
        BusinessOntologyReviewEntry reviewEntry,
        ValidatedOntologySemanticCandidate validated,
        BusinessOntologyUnsupportedRuleProjection unsupported)
    {
        using var document = JsonDocument.Parse(validated.CanonicalPayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        var details = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
        {
            candidateId = reviewEntry.Review.CandidateId,
            reviewId = reviewEntry.Review.Id,
            ruleId = validated.SemanticId,
            ruleKind = String(semantic, "ruleKind"),
            reasonCode = unsupported.ReasonCode,
            predicate = JsonSerializer.Deserialize<JsonElement>(
                OntologySemanticJson.Canonicalize(
                    semantic.GetProperty("predicate").GetRawText())),
        }));
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(details)))
            .ToLowerInvariant();
        return new BusinessOntologyDiagnostic(
            "diagnostic:unsupported-rule-projection:" + digest,
            "unsupported_rule_projection",
            "candidate",
            reviewEntry.Review.CandidateId,
            unsupported.Message,
            details,
            "error");
    }

    private static bool IsModelEvidence(BusinessOntologyEvidence evidence)
    {
        static bool ModelToken(string value) =>
            value.Contains("llm", StringComparison.OrdinalIgnoreCase)
            || value.Contains("language-model", StringComparison.OrdinalIgnoreCase)
            || value.Contains("provider-response", StringComparison.OrdinalIgnoreCase)
            || StringComparer.OrdinalIgnoreCase.Equals(value, "model");
        return ModelToken(evidence.SourceKind) || ModelToken(evidence.Resolver);
    }

    private static void EnsureUniqueMaterializedSubjects(
        IReadOnlyList<BusinessOntologyMaterializationRecord> materializations)
    {
        var duplicate = materializations
            .GroupBy(item => (item.SubjectKind, item.SubjectId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Multiple accepted candidates target '{duplicate.Key.SubjectKind}:{duplicate.Key.SubjectId}'.");
        }
    }

    private static string String(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString() ?? "";
}
