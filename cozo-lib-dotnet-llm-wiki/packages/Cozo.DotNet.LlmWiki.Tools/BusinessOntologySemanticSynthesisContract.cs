using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// Pending-only v3 semantic model. The deterministic projection baseline is deliberately supplied
/// by a trusted provider so candidate-owned data cannot certify its own projection comparison.
/// </summary>
public sealed record BusinessOntologySemanticEvidence(
    string Id,
    string SourceKind,
    string Repository,
    string RelativePath,
    string SymbolId);

public sealed record BusinessOntologySemanticProjectionCarrier(
    string ConceptId,
    IReadOnlyList<string> SymbolIds);

/// <summary>Computes the canonical carrier digest required for an external projection baseline.</summary>
public static class BusinessOntologySemanticProjectionBaselineDigest
{
    public static string Compute(IReadOnlyList<BusinessOntologySemanticProjectionCarrier> carriers)
    {
        ArgumentNullException.ThrowIfNull(carriers);

        var canonical = new StringBuilder();
        canonical.Append("carrier-count=").Append(carriers.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var carrier in carriers.OrderBy(item => item.ConceptId, StringComparer.Ordinal))
        {
            AppendField(canonical, "concept", carrier.ConceptId);
            var symbols = carrier.SymbolIds.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
            canonical.Append("symbol-count=").Append(symbols.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var symbol in symbols)
            {
                AppendField(canonical, "symbol", symbol);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>Computes the canonical trusted-baseline envelope digest.</summary>
    public static string ComputeIntegrityDigest(
        string sourceFingerprint,
        string inputFingerprint,
        string carrierDigest,
        int carrierCount)
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(inputFingerprint);
        ArgumentNullException.ThrowIfNull(carrierDigest);

        var canonical = new StringBuilder();
        AppendField(canonical, "source-fingerprint", sourceFingerprint);
        AppendField(canonical, "input-fingerprint", inputFingerprint);
        AppendField(canonical, "carrier-digest", carrierDigest);
        canonical.Append("carrier-count=").Append(carrierCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder canonical, string name, string value) =>
        canonical.Append(name).Append('=').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
}

public sealed record BusinessOntologySemanticVerifiedProjectionBaseline(
    string SourceFingerprint,
    string InputFingerprint,
    string Digest,
    string IntegrityDigest,
    int ExpectedCarrierCount,
    IReadOnlyList<BusinessOntologySemanticProjectionCarrier> Carriers);

/// <summary>
/// Runtime-owned identity for one quality-gate evaluation. Candidate data may describe this run,
/// but it cannot select the evaluation input, synthesis run, or critic snapshot being trusted.
/// </summary>
public sealed record BusinessOntologySemanticEvaluationContext(
    string SourceFingerprint,
    string InputFingerprint,
    string SynthesisRunId,
    string ExpectedCriticSnapshotDigest);

/// <summary>
/// Resolves a projection baseline from the runtime-owned evaluation context. P2 will bind this to
/// the deterministic code-projection source; the quality gate only trusts this boundary after it
/// revalidates the returned carrier and envelope digests.
/// </summary>
public interface IBusinessOntologySemanticVerifiedProjectionBaselineProvider
{
    BusinessOntologySemanticVerifiedProjectionBaseline GetVerifiedBaseline(BusinessOntologySemanticEvaluationContext context);
}

public sealed record BusinessOntologySemanticDomainCharter(
    string Id,
    string NameZh,
    string DescriptionZh,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> WorkflowNames);

public sealed record BusinessOntologySemanticImplementationAnchor(
    string SymbolId,
    string Role,
    string RelativePath,
    string EvidenceId);

public sealed record BusinessOntologySemanticCluster(
    string Id,
    string DomainId,
    string ConceptId,
    string NameZh,
    string DescriptionZh,
    IReadOnlyList<BusinessOntologySemanticImplementationAnchor> ImplementationAnchors);

/// <summary>Connects a semantic claim to one concrete evidence item and its Chinese rationale.</summary>
public sealed record BusinessOntologySemanticClaimEvidenceBinding(
    string BindingType,
    string SummaryZh,
    string EvidenceId);

/// <summary>Closed claim binding vocabulary; each type is tied to one evidence source kind.</summary>
public static class BusinessOntologySemanticClaimBindingTypes
{
    public const string ServiceCall = "service-call";
    public const string TypedReference = "typed-reference";
    public const string RouteFlow = "route-flow";
    public const string ValidationBranch = "validation-branch";
    public const string StateUpdate = "state-update";
}

public sealed record BusinessOntologySemanticRelation(
    string Id,
    string FromConceptId,
    string ToConceptId,
    string Name,
    string DescriptionZh,
    IReadOnlyList<BusinessOntologySemanticClaimEvidenceBinding> EvidenceBindings);

public sealed record BusinessOntologySemanticRule(
    string Id,
    string SubjectConceptId,
    string DescriptionZh,
    IReadOnlyList<BusinessOntologySemanticClaimEvidenceBinding> EvidenceBindings);

public sealed record BusinessOntologySemanticLifecycle(
    string Id,
    string SubjectConceptId,
    string StateProperty,
    string DescriptionZh,
    IReadOnlyList<BusinessOntologySemanticClaimEvidenceBinding> EvidenceBindings);

public sealed record BusinessOntologySemanticCriticRunProvenance(
    string RunId,
    string SourceFingerprint,
    string InputFingerprint,
    string SnapshotDigest);

/// <summary>
/// Verifies critic-run provenance from a trusted runtime registry rather than candidate-owned
/// identifiers. Returning <see langword="false"/> rejects the candidate's claimed critic run.
/// </summary>
public interface IBusinessOntologySemanticTrustedCriticRunVerifier
{
    bool VerifyCriticRun(
        BusinessOntologySemanticEvaluationContext context,
        BusinessOntologySemanticCriticRunProvenance criticRun);
}

public static class BusinessOntologySemanticCriticDecisions
{
    public const string Keep = "keep";
    public const string Merge = "merge";
    public const string Drop = "drop";
    public const string Defer = "defer";
    public const string RequestEvidence = "request_evidence";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Keep, Merge, Drop, Defer, RequestEvidence,
    };
}

public sealed record BusinessOntologySemanticCriticVerdict(
    string CandidateId,
    string Decision,
    string RationaleZh,
    IReadOnlyList<string> EvidenceIds,
    string? TargetCandidateId = null,
    string? RequestZh = null);

public sealed record BusinessOntologySemanticCandidateBundle(
    string OntologyId,
    string SynthesisRunId,
    string SourceFingerprint,
    string InputFingerprint,
    BusinessOntologySemanticCriticRunProvenance CriticRun,
    IReadOnlyList<BusinessOntologySemanticEvidence> Evidence,
    IReadOnlyList<BusinessOntologySemanticDomainCharter> DomainCharters,
    IReadOnlyList<BusinessOntologySemanticCluster> Clusters,
    IReadOnlyList<BusinessOntologySemanticRelation> Relations,
    IReadOnlyList<BusinessOntologySemanticRule> Rules,
    IReadOnlyList<BusinessOntologySemanticLifecycle> Lifecycles,
    IReadOnlyList<BusinessOntologySemanticCriticVerdict> CriticVerdicts);

public sealed record BusinessOntologySemanticQualityDiagnostic(
    string Code,
    string Severity,
    string MessageZh,
    string? SubjectId = null);

public sealed record BusinessOntologySemanticQualityReport(
    string Status,
    int DomainCount,
    int ConceptCount,
    int RelationCount,
    int RuleCount,
    int LifecycleCount,
    double SingleAnchorConceptRatio,
    double ProjectionLikeConceptRatio,
    IReadOnlyList<BusinessOntologySemanticQualityDiagnostic> Diagnostics);

internal static class BusinessOntologySemanticIdentifierGrammar
{
    private static readonly Regex PascalCaseSegment = new("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
    private static readonly Regex TechnicalSuffix = new(
        "(?:DTO|Dto|Entity|VO|Vo|Request|Response|Controller|Service|Repository|Handler|Manager|Page|Form|Info)$",
        RegexOptions.CultureInvariant);

    public static bool IsValidDomainId(string? domainId)
    {
        if (string.IsNullOrWhiteSpace(domainId))
        {
            return false;
        }

        var segments = domainId.Split('.');
        return segments.Length is 1 or 2
            && segments.All(segment => PascalCaseSegment.IsMatch(segment))
            && !TechnicalSuffix.IsMatch(segments[^1]);
    }

    public static bool IsValidConceptId(string? conceptId, string? domainId)
    {
        if (string.IsNullOrWhiteSpace(conceptId) || string.IsNullOrWhiteSpace(domainId))
        {
            return false;
        }

        var conceptSegments = conceptId.Split('.');
        var domainRoot = domainId.Split('.', 2)[0];
        return conceptSegments.Length == 2
            && conceptSegments.All(segment => PascalCaseSegment.IsMatch(segment))
            && string.Equals(conceptSegments[0], domainRoot, StringComparison.Ordinal)
            && !TechnicalSuffix.IsMatch(conceptSegments[1]);
    }

    public static bool HasTechnicalSuffix(string? identifier) =>
        !string.IsNullOrWhiteSpace(identifier) && TechnicalSuffix.IsMatch(identifier!.Split('.').Last());
}

/// <summary>
/// A local quality gate for v3 pending bundles. Well-formed data containing a code projection is
/// a semantic failure, while review-routed candidates cannot support a retained semantic edge.
/// </summary>
public sealed class BusinessOntologySemanticQualityGate
{
    public const string Passed = "semantic_quality_passed";
    public const string Failed = "semantic_quality_failed";

    private const double ProjectionCoverageThreshold = 0.8;
    public BusinessOntologySemanticQualityReport Evaluate(
        BusinessOntologySemanticCandidateBundle bundle,
        BusinessOntologySemanticEvaluationContext context,
        IBusinessOntologySemanticVerifiedProjectionBaselineProvider verifiedBaselineProvider,
        IBusinessOntologySemanticTrustedCriticRunVerifier trustedCriticRunVerifier)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verifiedBaselineProvider);
        ArgumentNullException.ThrowIfNull(trustedCriticRunVerifier);

        var diagnostics = new List<BusinessOntologySemanticQualityDiagnostic>();
        ValidateEvaluationContext(context, diagnostics);
        ValidateBundleMatchesEvaluationContext(bundle, context, diagnostics);
        var evidenceById = ValidateEvidenceRegistry(bundle.Evidence, diagnostics);
        var evidenceIds = evidenceById.Keys.ToHashSet(StringComparer.Ordinal);
        var baseline = ResolveVerifiedBaseline(context, verifiedBaselineProvider, diagnostics);
        var charters = bundle.DomainCharters.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var clusters = bundle.Clusters.OrderBy(item => item.ConceptId, StringComparer.Ordinal).ToArray();

        ValidateRequiredIdentifiers(bundle, diagnostics);

        RejectDuplicateIds(charters.Select(item => item.Id), "domain_charter_duplicate", "业务域 charter 标识重复，不能由集合去重掩盖。", diagnostics);
        RejectDuplicateIds(clusters.Select(item => item.Id), "cluster_id_duplicate", "semantic cluster 标识重复，不能由集合去重掩盖。", diagnostics);
        RejectDuplicateIds(clusters.Select(item => item.ConceptId), "concept_id_duplicate", "业务概念标识重复，不能由集合去重掩盖。", diagnostics);
        RejectDuplicateIds(bundle.Relations.Select(item => item.Id), "relation_id_duplicate", "业务关系标识重复，不能由集合去重掩盖。", diagnostics);
        RejectDuplicateIds(bundle.Rules.Select(item => item.Id), "rule_id_duplicate", "业务规则标识重复，不能由集合去重掩盖。", diagnostics);
        RejectDuplicateIds(bundle.Lifecycles.Select(item => item.Id), "lifecycle_id_duplicate", "业务生命周期标识重复，不能由集合去重掩盖。", diagnostics);

        var candidateEntries = clusters.Select(item => (Id: item.ConceptId, Kind: "concept"))
            .Concat(bundle.Relations.Select(item => (Id: item.Id, Kind: "relation")))
            .Concat(bundle.Rules.Select(item => (Id: item.Id, Kind: "rule")))
            .Concat(bundle.Lifecycles.Select(item => (Id: item.Id, Kind: "lifecycle")))
            .ToArray();
        foreach (var duplicate in candidateEntries.Where(item => !Blank(item.Id)).GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new("candidate_id_duplicate", "error", "候选标识跨种类重复，不能由 HashSet 合并为一个候选。", duplicate.Key));
        }

        var conceptIds = clusters.Select(item => item.ConceptId).Where(id => !Blank(id)).ToHashSet(StringComparer.Ordinal);
        var domains = charters.Select(item => item.Id).Where(id => !Blank(id)).ToHashSet(StringComparer.Ordinal);
        var candidateIds = candidateEntries.Select(item => item.Id).Where(id => !Blank(id)).ToHashSet(StringComparer.Ordinal);
        ValidateCriticProvenance(bundle, context, trustedCriticRunVerifier, diagnostics);

        if (charters.Length == 0)
        {
            diagnostics.Add(new("domain_charters_absent", "error", "没有业务域 charter，无法证明候选模型按业务域而非代码文件组织。"));
        }
        foreach (var charter in charters)
        {
            if (!ContainsCjk(charter.NameZh) || !ContainsCjk(charter.DescriptionZh) || !KnownEvidence(charter.EvidenceIds, evidenceIds))
            {
                diagnostics.Add(new("domain_charter_incomplete", "error", "业务域 charter 缺少中文业务说明或有效证据。", charter.Id));
            }
            if (!BusinessOntologySemanticIdentifierGrammar.IsValidDomainId(charter.Id))
            {
                diagnostics.Add(new("domain_charter_id_invalid", "error", "业务域 charter 标识必须是一段或两段 PascalCase 业务 ID，且末段不能是实现技术后缀。", charter.Id));
            }
            if (HasTechnicalSuffix(charter.Id))
            {
                diagnostics.Add(new("domain_charter_implementation_identity", "error", "业务域 charter 标识的末段带有实现技术后缀，不能作为业务域语义标识发布。", charter.Id));
            }
        }

        var candidateEvidence = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var anchorEvidenceByConcept = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var anchorSymbolsByConcept = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var singleAnchorConcepts = 0;
        foreach (var cluster in clusters)
        {
            if (!domains.Contains(cluster.DomainId))
            {
                diagnostics.Add(new("concept_domain_missing", "error", "业务概念没有归属到已声明的业务域。", cluster.ConceptId));
            }
            if (!BusinessOntologySemanticIdentifierGrammar.IsValidDomainId(cluster.DomainId))
            {
                diagnostics.Add(new("concept_domain_id_invalid", "error", "业务概念所属业务域标识必须是一段或两段 PascalCase 业务 ID，且末段不能是实现技术后缀。", cluster.DomainId));
            }
            if (HasTechnicalSuffix(cluster.DomainId))
            {
                diagnostics.Add(new("concept_domain_implementation_identity", "error", "业务概念所属业务域标识的末段带有实现技术后缀，不能作为业务域语义标识发布。", cluster.DomainId));
            }
            if (!ContainsCjk(cluster.NameZh) || !ContainsCjk(cluster.DescriptionZh))
            {
                diagnostics.Add(new("concept_business_description_missing", "error", "业务概念缺少中文业务名称或说明。", cluster.ConceptId));
            }
            if (cluster.ImplementationAnchors.Any(anchor => Blank(anchor.SymbolId) || Blank(anchor.Role) || Blank(anchor.RelativePath) || !evidenceIds.Contains(anchor.EvidenceId)))
            {
                diagnostics.Add(new("concept_anchor_evidence_missing", "error", "业务概念含有缺少身份、角色、相对路径或有效证据的实现锚点。", cluster.ConceptId));
            }
            if (cluster.ImplementationAnchors.Any(anchor => !EvidenceMatchesAnchor(anchor, bundle.Evidence)))
            {
                diagnostics.Add(new("concept_anchor_evidence_mismatch", "error", "实现锚点与其 evidence 的符号或相对路径不一致，不能作为可追溯语义证据。", cluster.ConceptId));
            }
            if (cluster.ImplementationAnchors.GroupBy(anchor => anchor.SymbolId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                diagnostics.Add(new("concept_anchor_duplicate", "error", "业务概念重复使用同一个实现符号作为多个锚点，不能伪造聚合度。", cluster.ConceptId));
            }
            if (cluster.ImplementationAnchors.GroupBy(anchor => anchor.RelativePath, StringComparer.Ordinal)
                .Any(group => !Blank(group.Key) && group.Select(anchor => anchor.SymbolId).Distinct(StringComparer.Ordinal).Count() > 1))
            {
                diagnostics.Add(new("concept_anchor_path_reused", "error", "多个不同实现锚点复用了同一相对路径，不能伪造跨角色聚合。", cluster.ConceptId));
            }

            var anchors = cluster.ImplementationAnchors
                .Where(anchor => !Blank(anchor.SymbolId) && !Blank(anchor.Role) && !Blank(anchor.RelativePath)
                    && evidenceIds.Contains(anchor.EvidenceId) && EvidenceMatchesAnchor(anchor, bundle.Evidence))
                .GroupBy(anchor => anchor.SymbolId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            var anchorEvidenceIds = anchors.Select(anchor => anchor.EvidenceId).ToHashSet(StringComparer.Ordinal);
            candidateEvidence[cluster.ConceptId] = anchorEvidenceIds;
            anchorEvidenceByConcept[cluster.ConceptId] = anchorEvidenceIds;
            anchorSymbolsByConcept[cluster.ConceptId] = anchors.Select(anchor => anchor.SymbolId).ToHashSet(StringComparer.Ordinal);
            if (anchors.Length < 2)
            {
                singleAnchorConcepts++;
                diagnostics.Add(new("concept_not_aggregated", "error", "业务概念只由单个有效实现锚点支撑，仍可能是代码符号投影。", cluster.ConceptId));
            }
            else if (anchors.Select(anchor => anchor.Role).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                diagnostics.Add(new("concept_anchor_diversity_missing", "error", "业务概念的实现锚点缺少跨角色佐证。", cluster.ConceptId));
            }
            if (!HasSemanticConceptId(cluster.ConceptId, cluster.DomainId))
            {
                diagnostics.Add(new("concept_semantic_id_invalid", "error", "业务概念标识必须是与所属业务域根一致的两段 PascalCase 语义 ID，不能使用 FQN、路径或裸类名。", cluster.ConceptId));
            }
            if (HasTechnicalSuffix(cluster.ConceptId))
            {
                diagnostics.Add(new("concept_implementation_identity", "error", "业务概念标识带有实现技术后缀，不能作为业务语义概念发布。", cluster.ConceptId));
            }
        }

        var semanticEdgeCount = bundle.Relations.Count(IsStructurallyValid)
            + bundle.Rules.Count(IsStructurallyValid)
            + bundle.Lifecycles.Count(IsStructurallyValid);
        if (semanticEdgeCount == 0)
        {
            diagnostics.Add(new("business_semantics_absent", "error", "候选模型没有关系、规则或生命周期，不能声称已完成业务语义理解。"));
        }
        foreach (var relation in bundle.Relations)
        {
            var bindings = ValidateBindings(relation.EvidenceBindings, evidenceById, relation.Id, "relation", diagnostics);
            candidateEvidence[relation.Id] = bindings;
            ValidateRelationBindingConnectivity(
                relation, bindings, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept, diagnostics);
            if (Blank(relation.Name) || !ContainsCjk(relation.DescriptionZh))
            {
                diagnostics.Add(new("relation_name_or_description_missing", "error", "业务关系必须有非空名称和中文说明。", relation.Id));
            }
            if (!conceptIds.Contains(relation.FromConceptId) || !conceptIds.Contains(relation.ToConceptId))
            {
                diagnostics.Add(new("relation_endpoint_missing", "error", "关系缺少已归纳的两端业务概念。", relation.Id));
            }
        }
        foreach (var rule in bundle.Rules)
        {
            var bindings = ValidateBindings(rule.EvidenceBindings, evidenceById, rule.Id, "rule", diagnostics);
            candidateEvidence[rule.Id] = bindings;
            ValidateSubjectBindingConnectivity(
                rule.Id, "rule", rule.SubjectConceptId, bindings, evidenceById,
                anchorEvidenceByConcept, anchorSymbolsByConcept, diagnostics);
            if (!conceptIds.Contains(rule.SubjectConceptId) || !ContainsCjk(rule.DescriptionZh))
            {
                diagnostics.Add(new("rule_subject_or_description_missing", "error", "业务规则缺少业务主体或中文说明。", rule.Id));
            }
        }
        foreach (var lifecycle in bundle.Lifecycles)
        {
            var bindings = ValidateBindings(lifecycle.EvidenceBindings, evidenceById, lifecycle.Id, "lifecycle", diagnostics);
            candidateEvidence[lifecycle.Id] = bindings;
            ValidateSubjectBindingConnectivity(
                lifecycle.Id, "lifecycle", lifecycle.SubjectConceptId, bindings, evidenceById,
                anchorEvidenceByConcept, anchorSymbolsByConcept, diagnostics);
            if (!conceptIds.Contains(lifecycle.SubjectConceptId) || Blank(lifecycle.StateProperty) || !ContainsCjk(lifecycle.DescriptionZh))
            {
                diagnostics.Add(new("lifecycle_subject_or_description_missing", "error", "生命周期缺少业务主体、状态属性或中文说明。", lifecycle.Id));
            }
        }

        var criticDecisions = ValidateCriticVerdicts(bundle.CriticVerdicts, candidateIds, candidateEvidence, evidenceIds, diagnostics);
        ValidateKeptSemanticDependencies(bundle, criticDecisions, diagnostics);

        var matchingCount = ProjectionMatchingCount(clusters, baseline);
        var candidateAnchorSymbols = clusters.SelectMany(cluster => cluster.ImplementationAnchors)
            .Select(anchor => anchor.SymbolId).Where(symbol => !Blank(symbol)).ToHashSet(StringComparer.Ordinal);
        var allBaselineCarriersUnionCovered = baseline.Count > 0
            && baseline.All(carrier => BaselineCoverage(carrier.Value, candidateAnchorSymbols) >= ProjectionCoverageThreshold);
        var allBaselineCarriersCovered = baseline.Count > 0 && matchingCount == baseline.Count;
        var allCandidateClustersProjected = clusters.Length > 0 && matchingCount == clusters.Length;
        var projectionLikeRatio = allCandidateClustersProjected ? 1d : 0d;
        if (allBaselineCarriersUnionCovered)
        {
            diagnostics.Add(new("projection_baseline_union_fully_covered", "error", "候选全集的实现锚点合并后覆盖所有已验证基础 carrier，不能通过拆分 cluster 绕过投影检查。"));
        }
        if (allBaselineCarriersCovered && allCandidateClustersProjected)
        {
            diagnostics.Add(new("candidate_set_matches_projection_baseline", "error", "整个候选集与已验证基础投影近似一对一等价，没有形成业务语义差异。"));
            diagnostics.Add(new("projection_likeness_high", "error", "候选集与基础代码投影一对一等价，候选图没有形成业务语义差异。"));
        }
        else if (allBaselineCarriersCovered)
        {
            diagnostics.Add(new("projection_baseline_fully_covered", "error", "所有已验证基础 carrier 都被候选一对一覆盖；额外概念不能掩盖符号投影。"));
        }
        else if (allCandidateClustersProjected)
        {
            diagnostics.Add(new("candidate_set_partially_matches_projection_baseline", "error", "候选集只是已验证基础投影的部分一对一复制，不能作为业务语义发布。"));
        }

        var singleAnchorRatio = clusters.Length == 0 ? 1 : singleAnchorConcepts / (double)clusters.Length;
        var ordered = diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.SubjectId, StringComparer.Ordinal).ToArray();
        return new BusinessOntologySemanticQualityReport(
            ordered.Any(item => item.Severity == "error") ? Failed : Passed,
            charters.Length, clusters.Length, bundle.Relations.Count, bundle.Rules.Count, bundle.Lifecycles.Count,
            singleAnchorRatio, projectionLikeRatio, ordered);
    }

    private static Dictionary<string, HashSet<string>> ResolveVerifiedBaseline(
        BusinessOntologySemanticEvaluationContext context,
        IBusinessOntologySemanticVerifiedProjectionBaselineProvider provider,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        BusinessOntologySemanticVerifiedProjectionBaseline? baseline;
        try
        {
            baseline = provider.GetVerifiedBaseline(context);
        }
        catch (Exception)
        {
            diagnostics.Add(new("verified_baseline_provider_unavailable", "error", "可信基线 provider 未能为候选输入返回已验证投影基线。"));
            return new(StringComparer.Ordinal);
        }
        if (baseline is null)
        {
            diagnostics.Add(new("verified_baseline_provider_missing", "error", "可信基线 provider 未返回已验证投影基线。"));
            return new(StringComparer.Ordinal);
        }
        return ValidateVerifiedBaseline(context, baseline, diagnostics);
    }

    private static void ValidateEvaluationContext(
        BusinessOntologySemanticEvaluationContext context,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (Blank(context.SourceFingerprint) || Blank(context.InputFingerprint)
            || Blank(context.SynthesisRunId) || Blank(context.ExpectedCriticSnapshotDigest))
        {
            diagnostics.Add(new("evaluation_context_invalid", "error", "runtime-owned evaluation context 缺少来源、输入、synthesis run 或预期 critic snapshot digest。"));
        }
    }

    private static void ValidateBundleMatchesEvaluationContext(
        BusinessOntologySemanticCandidateBundle bundle,
        BusinessOntologySemanticEvaluationContext context,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (Blank(bundle.SourceFingerprint) || Blank(bundle.InputFingerprint) || Blank(bundle.SynthesisRunId)
            || bundle.SourceFingerprint != context.SourceFingerprint
            || bundle.InputFingerprint != context.InputFingerprint
            || bundle.SynthesisRunId != context.SynthesisRunId)
        {
            diagnostics.Add(new("bundle_evaluation_context_mismatch", "error", "候选 bundle 的来源、输入或 synthesis run 必须与 runtime-owned evaluation context 完全一致。"));
        }
    }

    private static void ValidateRequiredIdentifiers(
        BusinessOntologySemanticCandidateBundle bundle,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (Blank(bundle.OntologyId) || Blank(bundle.SynthesisRunId))
        {
            diagnostics.Add(new("bundle_id_required", "error", "候选 bundle 必须具有非空 OntologyId 和 SynthesisRunId。"));
        }
        foreach (var charter in bundle.DomainCharters.Where(item => Blank(item.Id)))
        {
            diagnostics.Add(new("domain_charter_id_required", "error", "业务域 charter 必须具有非空标识。", charter.Id));
        }
        foreach (var cluster in bundle.Clusters.Where(item => Blank(item.Id) || Blank(item.DomainId) || Blank(item.ConceptId)))
        {
            diagnostics.Add(new("cluster_identity_required", "error", "semantic cluster 必须具有非空 Id、DomainId 和 ConceptId。", cluster.Id));
        }
        foreach (var relation in bundle.Relations.Where(item => Blank(item.Id)))
        {
            diagnostics.Add(new("relation_id_required", "error", "业务关系必须具有非空标识。", relation.Id));
        }
        foreach (var rule in bundle.Rules.Where(item => Blank(item.Id)))
        {
            diagnostics.Add(new("rule_id_required", "error", "业务规则必须具有非空标识。", rule.Id));
        }
        foreach (var lifecycle in bundle.Lifecycles.Where(item => Blank(item.Id)))
        {
            diagnostics.Add(new("lifecycle_id_required", "error", "业务生命周期必须具有非空标识。", lifecycle.Id));
        }
    }

    private static Dictionary<string, BusinessOntologySemanticEvidence> ValidateEvidenceRegistry(
        IReadOnlyList<BusinessOntologySemanticEvidence> evidence,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        var evidenceById = new Dictionary<string, BusinessOntologySemanticEvidence>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (Blank(item.Id) || Blank(item.SourceKind) || Blank(item.Repository) || Blank(item.RelativePath) || Blank(item.SymbolId)
                || !evidenceById.TryAdd(item.Id, item))
            {
                diagnostics.Add(new("evidence_registry_invalid", "error", "evidence registry 含空、重复或缺少来源类别的证据标识。", item.Id));
            }
        }
        return evidenceById;
    }

    private static Dictionary<string, HashSet<string>> ValidateVerifiedBaseline(
        BusinessOntologySemanticEvaluationContext context,
        BusinessOntologySemanticVerifiedProjectionBaseline baseline,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (Blank(baseline.SourceFingerprint) || Blank(baseline.InputFingerprint) || Blank(baseline.Digest) || Blank(baseline.IntegrityDigest))
        {
            diagnostics.Add(new("verified_baseline_integrity_missing", "error", "外部可信基线缺少来源、输入、digest 或完整性信息。"));
        }
        if (baseline.SourceFingerprint != context.SourceFingerprint || baseline.InputFingerprint != context.InputFingerprint)
        {
            diagnostics.Add(new("verified_baseline_fingerprint_mismatch", "error", "外部可信基线的来源或输入指纹与 runtime-owned evaluation context 不一致。"));
        }
        if (baseline.ExpectedCarrierCount <= 0 || baseline.Carriers.Count == 0
            || baseline.ExpectedCarrierCount != baseline.Carriers.Count
            || baseline.Carriers.Any(item => Blank(item.ConceptId) || item.SymbolIds.Count == 0 || item.SymbolIds.Any(Blank)
                || item.SymbolIds.Distinct(StringComparer.Ordinal).Count() != item.SymbolIds.Count)
            || baseline.Carriers.GroupBy(item => item.ConceptId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            diagnostics.Add(new("verified_baseline_invalid", "error", "外部可信基线缺少完整 carrier 内容、唯一性或数量校验。"));
            return new(StringComparer.Ordinal);
        }
        if (!string.Equals(baseline.Digest, BusinessOntologySemanticProjectionBaselineDigest.Compute(baseline.Carriers), StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new("verified_baseline_digest_mismatch", "error", "外部可信基线的 digest 必须等于其规范化 carrier 内容的 SHA-256。"));
        }
        var computedCarrierDigest = BusinessOntologySemanticProjectionBaselineDigest.Compute(baseline.Carriers);
        if (!string.Equals(
                baseline.IntegrityDigest,
                BusinessOntologySemanticProjectionBaselineDigest.ComputeIntegrityDigest(
                    baseline.SourceFingerprint,
                    baseline.InputFingerprint,
                    computedCarrierDigest,
                    baseline.ExpectedCarrierCount),
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new("verified_baseline_integrity_digest_mismatch", "error", "外部可信基线的 IntegrityDigest 必须等于绑定来源、输入、carrier digest 和 carrier 数量的规范化 SHA-256 envelope。"));
        }
        return baseline.Carriers.OrderBy(item => item.ConceptId, StringComparer.Ordinal)
            .ToDictionary(item => item.ConceptId, item => item.SymbolIds.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static void ValidateCriticProvenance(
        BusinessOntologySemanticCandidateBundle bundle,
        BusinessOntologySemanticEvaluationContext context,
        IBusinessOntologySemanticTrustedCriticRunVerifier trustedCriticRunVerifier,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        var critic = bundle.CriticRun;
        if (critic is null || Blank(bundle.SynthesisRunId) || Blank(critic.RunId) || Blank(critic.SnapshotDigest)
            || critic.SourceFingerprint != bundle.SourceFingerprint || critic.InputFingerprint != bundle.InputFingerprint
            || critic.RunId == bundle.SynthesisRunId)
        {
            diagnostics.Add(new("critic_provenance_invalid", "error", "critic run/snapshot provenance 必须绑定 bundle 输入且与 synthesis run 不同。"));
            return;
        }
        if (critic.SourceFingerprint != context.SourceFingerprint || critic.InputFingerprint != context.InputFingerprint
            || critic.SnapshotDigest != context.ExpectedCriticSnapshotDigest)
        {
            diagnostics.Add(new("critic_provenance_context_mismatch", "error", "critic run 的来源、输入和 snapshot digest 必须与 runtime-owned evaluation context 完全一致。"));
            return;
        }
        try
        {
            if (!trustedCriticRunVerifier.VerifyCriticRun(context, critic))
            {
                diagnostics.Add(new("critic_provenance_unverified", "error", "可信 critic-run verifier 未确认候选声明的独立 critic provenance。"));
            }
        }
        catch (Exception)
        {
            diagnostics.Add(new("critic_provenance_unverified", "error", "可信 critic-run verifier 无法确认候选声明的独立 critic provenance。"));
        }
    }

    private static HashSet<string> ValidateBindings(
        IReadOnlyList<BusinessOntologySemanticClaimEvidenceBinding> bindings,
        IReadOnlyDictionary<string, BusinessOntologySemanticEvidence> evidenceById,
        string candidateId,
        string candidateKind,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (bindings.Count == 0 || bindings.Any(binding => Blank(binding.BindingType) || !ContainsCjk(binding.SummaryZh)
            || Blank(binding.EvidenceId) || !evidenceById.TryGetValue(binding.EvidenceId, out var evidence)
            || !BusinessOntologySemanticBindingValidation.HasCompatibleBindingType(candidateKind, binding.BindingType, evidence.SourceKind))
            || bindings.GroupBy(binding => binding.EvidenceId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            diagnostics.Add(new($"{candidateKind}_evidence_binding_invalid", "error", "语义主张必须使用闭合 binding type、中文摘要和来源类别兼容且唯一的 evidence binding。", candidateId));
        }
        return bindings.Where(binding => !Blank(binding.EvidenceId)
                && evidenceById.TryGetValue(binding.EvidenceId, out var evidence)
                && BusinessOntologySemanticBindingValidation.HasCompatibleBindingType(candidateKind, binding.BindingType, evidence.SourceKind))
            .Select(binding => binding.EvidenceId).ToHashSet(StringComparer.Ordinal);
    }

    private static void ValidateRelationBindingConnectivity(
        BusinessOntologySemanticRelation relation,
        IReadOnlySet<string> bindingEvidenceIds,
        IReadOnlyDictionary<string, BusinessOntologySemanticEvidence> evidenceById,
        IReadOnlyDictionary<string, HashSet<string>> anchorEvidenceByConcept,
        IReadOnlyDictionary<string, HashSet<string>> anchorSymbolsByConcept,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (!HasAnchorConnectedBinding(
                relation.FromConceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept)
            || !HasAnchorConnectedBinding(
                relation.ToConceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept))
        {
            diagnostics.Add(new("relation_evidence_not_connected_to_endpoint", "error", "关系 evidence binding 必须分别连接 from 和 to 业务概念的有效实现锚点或其 anchor symbol。", relation.Id));
        }
    }

    private static void ValidateSubjectBindingConnectivity(
        string candidateId,
        string candidateKind,
        string subjectConceptId,
        IReadOnlySet<string> bindingEvidenceIds,
        IReadOnlyDictionary<string, BusinessOntologySemanticEvidence> evidenceById,
        IReadOnlyDictionary<string, HashSet<string>> anchorEvidenceByConcept,
        IReadOnlyDictionary<string, HashSet<string>> anchorSymbolsByConcept,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (!HasAnchorConnectedBinding(
                subjectConceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept))
        {
            diagnostics.Add(new($"{candidateKind}_evidence_not_connected_to_subject", "error", "规则或生命周期的 evidence binding 必须连接其业务主体的有效实现锚点或其 anchor symbol。", candidateId));
        }
    }

    private static bool HasAnchorConnectedBinding(
        string conceptId,
        IReadOnlySet<string> bindingEvidenceIds,
        IReadOnlyDictionary<string, BusinessOntologySemanticEvidence> evidenceById,
        IReadOnlyDictionary<string, HashSet<string>> anchorEvidenceByConcept,
        IReadOnlyDictionary<string, HashSet<string>> anchorSymbolsByConcept) =>
        BusinessOntologySemanticBindingValidation.HasAnchorConnectedBinding(
            conceptId, bindingEvidenceIds, evidenceById, anchorEvidenceByConcept, anchorSymbolsByConcept);

    private static IReadOnlyDictionary<string, string> ValidateCriticVerdicts(
        IReadOnlyList<BusinessOntologySemanticCriticVerdict> verdicts,
        IReadOnlySet<string> candidateIds,
        IReadOnlyDictionary<string, HashSet<string>> candidateEvidence,
        IReadOnlySet<string> evidenceIds,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        var decisions = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = verdicts.GroupBy(item => item.CandidateId, StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
        {
            var verdict = group.First();
            var valid = group.Count() == 1 && candidateIds.Contains(verdict.CandidateId)
                && BusinessOntologySemanticCriticDecisions.All.Contains(verdict.Decision)
                && ContainsCjk(verdict.RationaleZh) && KnownEvidence(verdict.EvidenceIds, evidenceIds);
            if (verdict.Decision == BusinessOntologySemanticCriticDecisions.Keep
                && (!candidateEvidence.TryGetValue(verdict.CandidateId, out var closure)
                    || !verdict.EvidenceIds.Any(closure.Contains)))
            {
                valid = false;
            }
            if (verdict.Decision == BusinessOntologySemanticCriticDecisions.Merge
                && (verdict.TargetCandidateId is null || Blank(verdict.TargetCandidateId)
                    || verdict.TargetCandidateId == verdict.CandidateId || !candidateIds.Contains(verdict.TargetCandidateId)))
            {
                valid = false;
            }
            if (verdict.Decision == BusinessOntologySemanticCriticDecisions.RequestEvidence && !ContainsCjk(verdict.RequestZh))
            {
                valid = false;
            }
            if (!valid)
            {
                diagnostics.Add(new("critic_verdict_invalid", "error", "critic verdict 缺少候选引用、有效闭包、中文理由或决定专属信息。", verdict.CandidateId));
            }
            else
            {
                decisions.Add(verdict.CandidateId, verdict.Decision);
                if (verdict.Decision != BusinessOntologySemanticCriticDecisions.Keep)
                {
                    diagnostics.Add(new("critic_review_routing", "info", "该 critic 决定需进入人工 review 路由，不作为独立候选发布错误。", verdict.CandidateId));
                }
            }
        }
        foreach (var candidateId in candidateIds.Where(id => !groups.Any(group => group.Key == id)))
        {
            diagnostics.Add(new("critic_verdict_missing", "error", "每个待发布业务候选都必须有且仅有一个 critic verdict。", candidateId));
        }
        foreach (var verdict in verdicts.Where(item => item.Decision == BusinessOntologySemanticCriticDecisions.Merge
            && !Blank(item.TargetCandidateId)))
        {
            if (!decisions.TryGetValue(verdict.TargetCandidateId!, out var targetDecision)
                || targetDecision != BusinessOntologySemanticCriticDecisions.Keep)
            {
                diagnostics.Add(new("critic_merge_target_not_kept", "error", "merge 候选只能合并到具有有效 keep verdict 的目标候选。", verdict.CandidateId));
            }
        }
        return decisions;
    }

    private static void ValidateKeptSemanticDependencies(
        BusinessOntologySemanticCandidateBundle bundle,
        IReadOnlyDictionary<string, string> criticDecisions,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        foreach (var relation in bundle.Relations)
        {
            ValidateKeptDependency(relation.Id, [relation.FromConceptId, relation.ToConceptId], criticDecisions, diagnostics);
        }
        foreach (var rule in bundle.Rules)
        {
            ValidateKeptDependency(rule.Id, [rule.SubjectConceptId], criticDecisions, diagnostics);
        }
        foreach (var lifecycle in bundle.Lifecycles)
        {
            ValidateKeptDependency(lifecycle.Id, [lifecycle.SubjectConceptId], criticDecisions, diagnostics);
        }
    }

    private static void ValidateKeptDependency(
        string edgeId,
        IReadOnlyList<string> conceptIds,
        IReadOnlyDictionary<string, string> criticDecisions,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        if (!criticDecisions.TryGetValue(edgeId, out var edgeDecision) || edgeDecision != BusinessOntologySemanticCriticDecisions.Keep)
        {
            return;
        }
        foreach (var conceptId in conceptIds.Distinct(StringComparer.Ordinal))
        {
            if (criticDecisions.TryGetValue(conceptId, out var conceptDecision) && conceptDecision != BusinessOntologySemanticCriticDecisions.Keep)
            {
                diagnostics.Add(new("keep_semantic_dependency_review_routed", "error", "保留的关系、规则或生命周期不能依赖进入 review 路由的业务概念。", edgeId));
            }
        }
    }

    private static int ProjectionMatchingCount(
        IReadOnlyList<BusinessOntologySemanticCluster> clusters,
        IReadOnlyDictionary<string, HashSet<string>> baseline)
    {
        var baselineOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var matches = 0;
        foreach (var cluster in clusters.OrderBy(item => item.ConceptId, StringComparer.Ordinal))
        {
            var symbols = cluster.ImplementationAnchors.Select(anchor => anchor.SymbolId)
                .Where(symbol => !Blank(symbol)).ToHashSet(StringComparer.Ordinal);
            var options = baseline.Where(item => BaselineCoverage(item.Value, symbols) >= ProjectionCoverageThreshold)
                .Select(item => item.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (TryMatch(cluster.ConceptId, options, baselineOwners, clusters, baseline, new HashSet<string>(StringComparer.Ordinal)))
            {
                matches++;
            }
        }
        return matches;
    }

    private static bool TryMatch(
        string conceptId,
        IReadOnlyList<string> options,
        IDictionary<string, string> baselineOwners,
        IReadOnlyList<BusinessOntologySemanticCluster> clusters,
        IReadOnlyDictionary<string, HashSet<string>> baseline,
        ISet<string> visited)
    {
        foreach (var baselineId in options)
        {
            if (!visited.Add(baselineId))
            {
                continue;
            }
            if (!baselineOwners.TryGetValue(baselineId, out var owner))
            {
                baselineOwners[baselineId] = conceptId;
                return true;
            }
            var ownerCluster = clusters.First(item => item.ConceptId == owner);
            var ownerSymbols = ownerCluster.ImplementationAnchors.Select(anchor => anchor.SymbolId)
                .Where(symbol => !Blank(symbol)).ToHashSet(StringComparer.Ordinal);
            var ownerOptions = baseline.Where(item => BaselineCoverage(item.Value, ownerSymbols) >= ProjectionCoverageThreshold)
                .Select(item => item.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (TryMatch(owner, ownerOptions, baselineOwners, clusters, baseline, visited))
            {
                baselineOwners[baselineId] = conceptId;
                return true;
            }
        }
        return false;
    }

    private static void RejectDuplicateIds(
        IEnumerable<string> ids,
        string code,
        string messageZh,
        ICollection<BusinessOntologySemanticQualityDiagnostic> diagnostics)
    {
        foreach (var duplicate in ids.Where(id => !Blank(id)).GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new(code, "error", messageZh, duplicate.Key));
        }
    }

    private static bool KnownEvidence(IReadOnlyList<string> ids, IReadOnlySet<string> known) =>
        ids.Count > 0 && ids.All(id => !Blank(id) && known.Contains(id)) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;

    private static bool EvidenceMatchesAnchor(
        BusinessOntologySemanticImplementationAnchor anchor,
        IReadOnlyList<BusinessOntologySemanticEvidence> evidence) =>
        evidence.Any(item => item.Id == anchor.EvidenceId
            && item.SymbolId == anchor.SymbolId
            && item.RelativePath == anchor.RelativePath);

    private static bool ContainsCjk(string? value) => !Blank(value) && value!.Any(character => character is >= '\u4e00' and <= '\u9fff');

    private static bool HasSemanticConceptId(string? conceptId, string? domainId) =>
        BusinessOntologySemanticIdentifierGrammar.IsValidConceptId(conceptId, domainId);

    private static bool HasTechnicalSuffix(string? identifier) =>
        BusinessOntologySemanticIdentifierGrammar.HasTechnicalSuffix(identifier);

    private static double BaselineCoverage(IReadOnlySet<string> baseline, IReadOnlySet<string> anchors) =>
        baseline.Count == 0 ? 0 : baseline.Count(symbol => anchors.Contains(symbol)) / (double)baseline.Count;

    private static bool IsStructurallyValid(BusinessOntologySemanticRelation relation) =>
        !Blank(relation.Id) && !Blank(relation.FromConceptId) && !Blank(relation.ToConceptId)
        && !Blank(relation.Name) && ContainsCjk(relation.DescriptionZh);

    private static bool IsStructurallyValid(BusinessOntologySemanticRule rule) =>
        !Blank(rule.Id) && !Blank(rule.SubjectConceptId) && ContainsCjk(rule.DescriptionZh);

    private static bool IsStructurallyValid(BusinessOntologySemanticLifecycle lifecycle) =>
        !Blank(lifecycle.Id) && !Blank(lifecycle.SubjectConceptId) && !Blank(lifecycle.StateProperty)
        && ContainsCjk(lifecycle.DescriptionZh);

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// Shared structural checks for pending staging and the final quality gate. These checks only
/// compare runtime-owned evidence and candidate anchors; they do not grant baseline or critic trust.
/// </summary>
internal static class BusinessOntologySemanticBindingValidation
{
    internal static bool HasCompatibleBindingType(string candidateKind, string bindingType, string sourceKind) =>
        (candidateKind, bindingType, sourceKind) switch
        {
            ("relation", BusinessOntologySemanticClaimBindingTypes.ServiceCall, "business_guard") => true,
            ("relation", BusinessOntologySemanticClaimBindingTypes.TypedReference, "typed_reference") => true,
            ("relation", BusinessOntologySemanticClaimBindingTypes.RouteFlow, "route_binding") => true,
            ("rule", BusinessOntologySemanticClaimBindingTypes.ValidationBranch, "business_guard") => true,
            ("lifecycle", BusinessOntologySemanticClaimBindingTypes.StateUpdate, "state_assignment") => true,
            _ => false,
        };

    internal static bool HasAnchorConnectedBinding(
        string conceptId,
        IReadOnlySet<string> bindingEvidenceIds,
        IReadOnlyDictionary<string, BusinessOntologySemanticEvidence> evidenceById,
        IReadOnlyDictionary<string, HashSet<string>> anchorEvidenceByConcept,
        IReadOnlyDictionary<string, HashSet<string>> anchorSymbolsByConcept) =>
        bindingEvidenceIds.Any(evidenceId => evidenceById.TryGetValue(evidenceId, out var evidence)
            && ((anchorEvidenceByConcept.TryGetValue(conceptId, out var anchorEvidenceIds) && anchorEvidenceIds.Contains(evidenceId))
                || (anchorSymbolsByConcept.TryGetValue(conceptId, out var anchorSymbols) && anchorSymbols.Contains(evidence.SymbolId))));
}
