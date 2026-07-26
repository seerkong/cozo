using System.Text.Json;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// A typed, in-memory draft produced before critic routing. It deliberately has no persistence,
/// artifact, or promotion capability. T3.3 supplies the real critic verdicts.
/// </summary>
internal sealed record BusinessOntologySemanticAttribute(
    string Id,
    string SubjectConceptId,
    string Name,
    string ValueType,
    string DescriptionZh,
    IReadOnlyList<string> EvidenceIds);

internal sealed record BusinessOntologySemanticPendingDraft(
    string DomainId,
    IReadOnlyList<BusinessOntologySemanticDomainCharter> DomainCharters,
    IReadOnlyList<BusinessOntologySemanticCluster> Clusters,
    IReadOnlyList<BusinessOntologySemanticAttribute> Attributes,
    IReadOnlyList<BusinessOntologySemanticRelation> Relations,
    IReadOnlyList<BusinessOntologySemanticRule> Rules,
    IReadOnlyList<BusinessOntologySemanticLifecycle> Lifecycles);

internal sealed record BusinessOntologySemanticPendingDraftValidation(
    BusinessOntologySemanticPendingDraft Draft,
    BusinessOntologySemanticQualityReport QualityReport);

/// <summary>
/// Strictly accepts a bounded candidate payload only when every reference is already present in
/// the runtime-owned evidence registry. It reuses the v3 quality gate with provisional defer
/// verdicts: the draft is structurally checked now, while T3.3 owns the final review routing.
/// </summary>
internal static class BusinessOntologySemanticPendingDraftValidator
{
    internal const string SchemaVersion = "business-ontology-semantic-pending-draft-v1";
    private const int MaxItemsPerKind = 12;
    private const int MaxEvidencePerItem = 8;

    public static BusinessOntologySemanticPendingDraft ParseAndValidate(
        string json,
        IReadOnlyList<BusinessOntologySemanticEvidence> evidence,
        BusinessOntologySemanticEvaluationContext context,
        IBusinessOntologySemanticVerifiedProjectionBaselineProvider verifiedBaselineProvider,
        IBusinessOntologySemanticTrustedCriticRunVerifier trustedCriticRunVerifier)
    {
        var draft = Parse(json);
        ValidateStructural(draft, evidence, expectedCharters: null);
        Validate(draft, evidence, context, verifiedBaselineProvider, trustedCriticRunVerifier);
        return draft;
    }

    /// <summary>
    /// Performs the strict pre-critic boundary check. This deliberately does not claim a trusted
    /// projection baseline or critic provenance: those inputs only exist after T3.3 review.
    /// </summary>
    public static BusinessOntologySemanticPendingDraft ParseAndValidateForStaging(
        string json,
        IReadOnlyList<BusinessOntologySemanticEvidence> evidence,
        IReadOnlyList<BusinessOntologySemanticDomainCharter> expectedCharters)
    {
        var draft = Parse(json);
        ValidateStructural(draft, evidence, expectedCharters);
        return draft;
    }

    private static BusinessOntologySemanticPendingDraft Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireObject(root, "$", ["schemaVersion", "domainId", "domainCharters", "clusters", "attributes", "relations", "rules", "lifecycles"]);
        if (root.GetProperty("schemaVersion").GetString() != SchemaVersion)
        {
            throw Invalid("$.schemaVersion", "must be the pending semantic draft schema version.");
        }
        RequireArrayShapes(root, "domainCharters", ["Id", "NameZh", "DescriptionZh", "EvidenceIds", "WorkflowNames"]);
        RequireArrayShapes(root, "clusters", ["Id", "DomainId", "ConceptId", "NameZh", "DescriptionZh", "ImplementationAnchors"],
            ("ImplementationAnchors", new[] { "SymbolId", "Role", "RelativePath", "EvidenceId" }));
        RequireArrayShapes(root, "attributes", ["Id", "SubjectConceptId", "Name", "ValueType", "DescriptionZh", "EvidenceIds"]);
        RequireArrayShapes(root, "relations", ["Id", "FromConceptId", "ToConceptId", "Name", "DescriptionZh", "EvidenceBindings"],
            ("EvidenceBindings", new[] { "BindingType", "SummaryZh", "EvidenceId" }));
        RequireArrayShapes(root, "rules", ["Id", "SubjectConceptId", "DescriptionZh", "EvidenceBindings"],
            ("EvidenceBindings", new[] { "BindingType", "SummaryZh", "EvidenceId" }));
        RequireArrayShapes(root, "lifecycles", ["Id", "SubjectConceptId", "StateProperty", "DescriptionZh", "EvidenceBindings"],
            ("EvidenceBindings", new[] { "BindingType", "SummaryZh", "EvidenceId" }));

        var draft = new BusinessOntologySemanticPendingDraft(
            RequireString(root, "domainId", "$"),
            DeserializeArray<BusinessOntologySemanticDomainCharter>(root, "domainCharters", "$", 6),
            DeserializeArray<BusinessOntologySemanticCluster>(root, "clusters", "$", MaxItemsPerKind),
            DeserializeArray<BusinessOntologySemanticAttribute>(root, "attributes", "$", MaxItemsPerKind),
            DeserializeArray<BusinessOntologySemanticRelation>(root, "relations", "$", MaxItemsPerKind),
            DeserializeArray<BusinessOntologySemanticRule>(root, "rules", "$", MaxItemsPerKind),
            DeserializeArray<BusinessOntologySemanticLifecycle>(root, "lifecycles", "$", MaxItemsPerKind));
        return draft;
    }

    public static BusinessOntologySemanticPendingDraftValidation Validate(
        BusinessOntologySemanticPendingDraft draft,
        IReadOnlyList<BusinessOntologySemanticEvidence> evidence,
        BusinessOntologySemanticEvaluationContext context,
        IBusinessOntologySemanticVerifiedProjectionBaselineProvider verifiedBaselineProvider,
        IBusinessOntologySemanticTrustedCriticRunVerifier trustedCriticRunVerifier)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verifiedBaselineProvider);
        ArgumentNullException.ThrowIfNull(trustedCriticRunVerifier);
        ValidateStructural(draft, evidence, expectedCharters: null);

        var candidateIds = draft.Clusters.Select(item => item.ConceptId)
            .Concat(draft.Relations.Select(item => item.Id))
            .Concat(draft.Rules.Select(item => item.Id))
            .Concat(draft.Lifecycles.Select(item => item.Id))
            .ToArray();
        var criticRun = new BusinessOntologySemanticCriticRunProvenance(
            "pending-critic", context.SourceFingerprint, context.InputFingerprint, context.ExpectedCriticSnapshotDigest);
        var bundle = new BusinessOntologySemanticCandidateBundle(
            "pending", context.SynthesisRunId, context.SourceFingerprint, context.InputFingerprint, criticRun,
            evidence, draft.DomainCharters, draft.Clusters, draft.Relations, draft.Rules, draft.Lifecycles,
            candidateIds.Select(id => new BusinessOntologySemanticCriticVerdict(
                id, BusinessOntologySemanticCriticDecisions.Defer, "待独立 critic 审核的结构化业务候选。", EvidenceForCandidate(id, draft))).ToArray());
        var report = new BusinessOntologySemanticQualityGate().Evaluate(
            bundle,
            context,
            verifiedBaselineProvider,
            trustedCriticRunVerifier);
        if (report.Status != BusinessOntologySemanticQualityGate.Passed)
        {
            throw Invalid("$", "does not satisfy the existing semantic quality gate: "
                + string.Join(',', report.Diagnostics.Where(item => item.Severity == "error").Select(item => item.Code)) + ".");
        }
        return new BusinessOntologySemanticPendingDraftValidation(draft, report);
    }

    private static void ValidateStructural(
        BusinessOntologySemanticPendingDraft draft,
        IReadOnlyList<BusinessOntologySemanticEvidence> evidence,
        IReadOnlyList<BusinessOntologySemanticDomainCharter>? expectedCharters)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(evidence);
        RequireBounded(draft.DomainCharters, nameof(draft.DomainCharters), 6, requireItems: true);
        RequireBounded(draft.Clusters, nameof(draft.Clusters), MaxItemsPerKind, requireItems: true);
        RequireBounded(draft.Attributes, nameof(draft.Attributes), MaxItemsPerKind, requireItems: true);
        RequireBounded(draft.Relations, nameof(draft.Relations), MaxItemsPerKind, requireItems: true);
        RequireBounded(draft.Rules, nameof(draft.Rules), MaxItemsPerKind, requireItems: true);
        RequireBounded(draft.Lifecycles, nameof(draft.Lifecycles), MaxItemsPerKind, requireItems: true);

        var evidenceById = evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (evidenceById.Count != evidence.Count || evidence.Any(item => Blank(item.Id) || Blank(item.SourceKind)
            || Blank(item.Repository) || Blank(item.RelativePath) || Blank(item.SymbolId)))
        {
            throw Invalid("$.evidence", "must be a unique runtime-owned registry with source metadata.");
        }
        if (!draft.DomainCharters.Any(charter => charter.Id == draft.DomainId)
            || !BusinessOntologySemanticIdentifierGrammar.IsValidDomainId(draft.DomainId))
        {
            throw Invalid("$.domainId", "must name one declared canonical business-domain charter.");
        }
        if (draft.DomainCharters.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || draft.DomainCharters.Any(charter => !BusinessOntologySemanticIdentifierGrammar.IsValidDomainId(charter.Id)
                || !ContainsCjk(charter.NameZh) || !ContainsCjk(charter.DescriptionZh)))
        {
            throw Invalid("$.domainCharters", "must have unique canonical identifiers and Chinese business descriptions.");
        }
        foreach (var charter in draft.DomainCharters)
        {
            RequireClosedEvidence(charter.EvidenceIds, evidenceById, "$.domainCharters");
            if (charter.WorkflowNames.Count is 0 or > MaxEvidencePerItem || charter.WorkflowNames.Any(Blank))
            {
                throw Invalid("$.domainCharters", "must retain at least one declared workflow name.");
            }
        }
        if (expectedCharters is not null && !SameCharters(draft.DomainCharters, expectedCharters))
        {
            throw Invalid("$.domainCharters", "must exactly retain runtime-discovered charters rather than invent or alter them.");
        }

        var conceptIds = draft.Clusters.Select(item => item.ConceptId).ToHashSet(StringComparer.Ordinal);
        if (conceptIds.Count != draft.Clusters.Count
            || draft.Clusters.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != draft.Clusters.Count)
        {
            throw Invalid("$.clusters", "must not duplicate business concepts.");
        }
        foreach (var cluster in draft.Clusters)
        {
            if (Blank(cluster.Id) || cluster.DomainId != draft.DomainId
                || !BusinessOntologySemanticIdentifierGrammar.IsValidConceptId(cluster.ConceptId, draft.DomainId)
                || !ContainsCjk(cluster.NameZh) || !ContainsCjk(cluster.DescriptionZh)
                || cluster.ImplementationAnchors.Count is < 2 or > MaxEvidencePerItem
                || cluster.ImplementationAnchors.Select(anchor => anchor.SymbolId).Distinct(StringComparer.Ordinal).Count() != cluster.ImplementationAnchors.Count
                || cluster.ImplementationAnchors.Select(anchor => anchor.EvidenceId).Distinct(StringComparer.Ordinal).Count() != cluster.ImplementationAnchors.Count
                || cluster.ImplementationAnchors.Select(anchor => anchor.Role).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                throw Invalid("$.clusters", "must be Chinese, business-named, and aggregate at least two distinct cross-role implementation anchors.");
            }
            foreach (var anchor in cluster.ImplementationAnchors)
            {
                if (Blank(anchor.SymbolId) || Blank(anchor.Role) || Blank(anchor.RelativePath)
                    || !evidenceById.TryGetValue(anchor.EvidenceId, out var source)
                    || !string.Equals(anchor.SymbolId, source.SymbolId, StringComparison.Ordinal)
                    || !string.Equals(anchor.RelativePath, source.RelativePath, StringComparison.Ordinal))
                {
                    throw Invalid("$.clusters", "must use only matching runtime-owned implementation evidence.");
                }
            }
        }

        var anchorEvidenceByConcept = draft.Clusters.ToDictionary(
            cluster => cluster.ConceptId,
            cluster => cluster.ImplementationAnchors.Select(anchor => anchor.EvidenceId).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var anchorSymbolsByConcept = draft.Clusters.ToDictionary(
            cluster => cluster.ConceptId,
            cluster => cluster.ImplementationAnchors.Select(anchor => anchor.SymbolId).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        if (draft.Attributes.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != draft.Attributes.Count
            || draft.Attributes.GroupBy(item => item.SubjectConceptId + "\u001f" + item.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw Invalid("$.attributes", "must not duplicate an attribute name on the same business concept.");
        }
        foreach (var attribute in draft.Attributes)
        {
            if (Blank(attribute.Id) || !conceptIds.Contains(attribute.SubjectConceptId) || Blank(attribute.Name)
                || Blank(attribute.ValueType) || !ContainsCjk(attribute.DescriptionZh))
            {
                throw Invalid("$.attributes", "must have an existing business subject, a name/type, and a Chinese description.");
            }
            RequireClosedEvidence(attribute.EvidenceIds, evidenceById, "$.attributes");
        }

        ValidateCandidateIds(draft);
        foreach (var relation in draft.Relations)
        {
            if (!conceptIds.Contains(relation.FromConceptId) || !conceptIds.Contains(relation.ToConceptId)
                || relation.FromConceptId == relation.ToConceptId || !ContainsCjk(relation.Name) || !ContainsCjk(relation.DescriptionZh))
            {
                throw Invalid("$.relations", "must connect two known business concepts with Chinese business wording.");
            }
            var bindingEvidenceIds = RequireBindings(relation.EvidenceBindings, evidenceById, "$.relations", "relation");
            if (!BusinessOntologySemanticBindingValidation.HasAnchorConnectedBinding(
                    relation.FromConceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept)
                || !BusinessOntologySemanticBindingValidation.HasAnchorConnectedBinding(
                    relation.ToConceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept))
            {
                throw Invalid("$.relations", "must use compatible bindings connected to both business-concept endpoints.");
            }
        }
        foreach (var rule in draft.Rules)
        {
            if (!conceptIds.Contains(rule.SubjectConceptId) || !ContainsCjk(rule.DescriptionZh))
            {
                throw Invalid("$.rules", "must name an existing business subject with a Chinese description.");
            }
            var bindingEvidenceIds = RequireBindings(rule.EvidenceBindings, evidenceById, "$.rules", "rule");
            if (!BusinessOntologySemanticBindingValidation.HasAnchorConnectedBinding(
                    rule.SubjectConceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept))
            {
                throw Invalid("$.rules", "must use compatible bindings connected to the business-concept subject.");
            }
        }
        foreach (var lifecycle in draft.Lifecycles)
        {
            if (!conceptIds.Contains(lifecycle.SubjectConceptId) || Blank(lifecycle.StateProperty) || !ContainsCjk(lifecycle.DescriptionZh))
            {
                throw Invalid("$.lifecycles", "must name an existing business subject, state property, and Chinese description.");
            }
            var bindingEvidenceIds = RequireBindings(lifecycle.EvidenceBindings, evidenceById, "$.lifecycles", "lifecycle");
            if (!BusinessOntologySemanticBindingValidation.HasAnchorConnectedBinding(
                    lifecycle.SubjectConceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept))
            {
                throw Invalid("$.lifecycles", "must use compatible bindings connected to the business-concept subject.");
            }
        }
    }

    private static IReadOnlyList<string> EvidenceForCandidate(string id, BusinessOntologySemanticPendingDraft draft)
    {
        var ids = draft.Clusters.Where(item => item.ConceptId == id).SelectMany(item => item.ImplementationAnchors.Select(anchor => anchor.EvidenceId))
            .Concat(draft.Relations.Where(item => item.Id == id).SelectMany(item => item.EvidenceBindings.Select(binding => binding.EvidenceId)))
            .Concat(draft.Rules.Where(item => item.Id == id).SelectMany(item => item.EvidenceBindings.Select(binding => binding.EvidenceId)))
            .Concat(draft.Lifecycles.Where(item => item.Id == id).SelectMany(item => item.EvidenceBindings.Select(binding => binding.EvidenceId)))
            .Distinct(StringComparer.Ordinal).ToArray();
        return ids.Length == 0 ? draft.DomainCharters.SelectMany(item => item.EvidenceIds).Take(1).ToArray() : ids;
    }

    private static void RequireClosedEvidence(IReadOnlyList<string> ids, IReadOnlyDictionary<string, BusinessOntologySemanticEvidence> evidence, string path)
    {
        if (ids.Count is 0 or > MaxEvidencePerItem || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
            || ids.Any(id => !evidence.ContainsKey(id)))
        {
            throw Invalid(path, "must reference 1..8 distinct runtime-owned evidence identities.");
        }
    }

    private static HashSet<string> RequireBindings(
        IReadOnlyList<BusinessOntologySemanticClaimEvidenceBinding> bindings,
        IReadOnlyDictionary<string, BusinessOntologySemanticEvidence> evidence,
        string path,
        string candidateKind)
    {
        if (bindings.Count is 0 or > MaxEvidencePerItem
            || bindings.Any(binding => !KnownBindingTypes.Contains(binding.BindingType)
                || !ContainsCjk(binding.SummaryZh) || !evidence.TryGetValue(binding.EvidenceId, out var source)
                || !BusinessOntologySemanticBindingValidation.HasCompatibleBindingType(candidateKind, binding.BindingType, source.SourceKind))
            || bindings.Select(binding => binding.EvidenceId).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
        {
            throw Invalid(path, "must have 1..8 distinct, Chinese-rationalized bindings with source-kind compatibility to runtime-owned evidence.");
        }
        return bindings.Select(binding => binding.EvidenceId).ToHashSet(StringComparer.Ordinal);
    }

    private static void ValidateCandidateIds(BusinessOntologySemanticPendingDraft draft)
    {
        var ids = draft.Clusters.Select(item => item.ConceptId)
            .Concat(draft.Relations.Select(item => item.Id))
            .Concat(draft.Rules.Select(item => item.Id))
            .Concat(draft.Lifecycles.Select(item => item.Id))
            .ToArray();
        if (ids.Any(Blank) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw Invalid("$", "must have non-empty candidate identifiers that are unique across kinds.");
        }
    }

    private static bool SameCharters(
        IReadOnlyList<BusinessOntologySemanticDomainCharter> actual,
        IReadOnlyList<BusinessOntologySemanticDomainCharter> expected) =>
        actual.OrderBy(item => item.Id, StringComparer.Ordinal).SequenceEqual(
            expected.OrderBy(item => item.Id, StringComparer.Ordinal),
            BusinessOntologySemanticDomainCharterComparer.Instance);

    private static readonly IReadOnlySet<string> KnownBindingTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        BusinessOntologySemanticClaimBindingTypes.ServiceCall,
        BusinessOntologySemanticClaimBindingTypes.TypedReference,
        BusinessOntologySemanticClaimBindingTypes.RouteFlow,
        BusinessOntologySemanticClaimBindingTypes.ValidationBranch,
        BusinessOntologySemanticClaimBindingTypes.StateUpdate,
    };

    private sealed class BusinessOntologySemanticDomainCharterComparer : IEqualityComparer<BusinessOntologySemanticDomainCharter>
    {
        public static BusinessOntologySemanticDomainCharterComparer Instance { get; } = new();

        public bool Equals(BusinessOntologySemanticDomainCharter? left, BusinessOntologySemanticDomainCharter? right) =>
            left is not null && right is not null
            && left.Id == right.Id && left.NameZh == right.NameZh && left.DescriptionZh == right.DescriptionZh
            && left.EvidenceIds.SequenceEqual(right.EvidenceIds, StringComparer.Ordinal)
            && left.WorkflowNames.SequenceEqual(right.WorkflowNames, StringComparer.Ordinal);

        public int GetHashCode(BusinessOntologySemanticDomainCharter value) =>
            HashCode.Combine(value.Id, value.NameZh, value.DescriptionZh);
    }

    private static IReadOnlyList<T> DeserializeArray<T>(JsonElement root, string name, string path, int maximum)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
        {
            throw Invalid(path + "." + name, $"must be an array of at most {maximum} items.");
        }
        var values = JsonSerializer.Deserialize<T[]>(value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = false });
        if (values is null || values.Any(item => item is null))
        {
            throw Invalid(path + "." + name, "contains an invalid item.");
        }
        return values;
    }

    private static void RequireBounded<T>(IReadOnlyList<T> items, string name, int maximum, bool requireItems = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > maximum || (requireItems && items.Count == 0))
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must contain {(requireItems ? "1" : "0")} to {maximum} items.");
        }
    }

    private static void RequireObject(JsonElement element, string path, IReadOnlyList<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal) == false)
        {
            throw Invalid(path, "has unsupported or missing properties.");
        }
    }

    private static void RequireArrayShapes(
        JsonElement root,
        string name,
        IReadOnlyList<string> itemProperties,
        params (string Name, IReadOnlyList<string> ItemProperties)[] nestedArrays)
    {
        var array = root.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("$." + name, "must be an array.");
        }
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            RequireObject(item, "$." + name + "[" + index + "]", itemProperties);
            foreach (var nested in nestedArrays)
            {
                if (item.GetProperty(nested.Name).ValueKind != JsonValueKind.Array)
                {
                    throw Invalid("$." + name + "[" + index + "]." + nested.Name, "must be an array.");
                }
                var nestedIndex = 0;
                foreach (var nestedItem in item.GetProperty(nested.Name).EnumerateArray())
                {
                    RequireObject(nestedItem, "$." + name + "[" + index + "]." + nested.Name + "[" + nestedIndex++ + "]", nested.ItemProperties);
                }
            }
            index++;
        }
    }

    private static string RequireString(JsonElement element, string name, string path) =>
        element.GetProperty(name).ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetProperty(name).GetString())
            ? element.GetProperty(name).GetString()! : throw Invalid(path + "." + name, "must be a non-empty string.");

    private static bool ContainsCjk(string? value) => !string.IsNullOrWhiteSpace(value) && value.Any(character => character is >= '\u4e00' and <= '\u9fff');
    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    private static ArgumentException Invalid(string path, string message) => new($"Pending semantic draft field '{path}' {message}");

}
